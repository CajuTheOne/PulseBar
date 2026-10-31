using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar.Logi;

// Канал HID++ к приёмнику или устройству. С 2.2: у каждого запроса свой программный id (2–15 по кругу) — ответы
// другим программам (G HUB) и запоздавшие ответы на прошлые запросы не принимаются за свои; уведомления и события
// подключения обрабатывает отдельный поток, чтобы долгий обработчик (макрос) не задерживал чтение ответов.
internal sealed class HidppChannel : IDisposable
{
	private sealed class Pending
	{
		public byte Dev;

		public byte Feat;

		public byte FnSw;

		public bool Short;

		public readonly ManualResetEventSlim Done = new ManualResetEventSlim();

		public byte[]? Reply;

		public int Error;
	}

	private int _swid;

	private readonly BlockingCollection<Action> _events = new BlockingCollection<Action>();

	// Ошибки HID++ 2.0, после которых имеет смысл повторить: 8 — устройство занято; -1 — нет ответа.
	public const int ErrBusy = 8;

	public const int ErrTimeout = -1;

	private readonly FileStream? _short;

	private readonly FileStream _long;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

	private readonly object _pendLock = new object();

	private Pending? _pend;

	private static readonly HashSet<ushort> Receivers = new HashSet<ushort> { 50475, 50482, 50484, 50489, 50490, 50495, 50497, 50501, 50503, 50504 };

	public ushort Pid { get; }

	public bool IsReceiver { get; }

	public bool Alive => !_cts.IsCancellationRequested;

	public event Action<byte, byte, byte, byte[]>? Notification;

	public event Action<byte, bool>? Connection;

	public event Action? Died;

	private HidppChannel(ushort pid, bool receiver, FileStream? sh, FileStream lg)
	{
		Pid = pid;
		IsReceiver = receiver;
		_short = sh;
		_long = lg;
		if (_short != null)
		{
			_ = ReadLoop(_short, 7);
		}
		_ = ReadLoop(_long, 20);
		new Thread(DispatchEvents)
		{
			IsBackground = true,
			Name = $"HID++ {pid:X4} events"
		}.Start();
	}

	private void DispatchEvents()
	{
		try
		{
			foreach (Action item in _events.GetConsumingEnumerable())
			{
				try
				{
					item();
				}
				catch (Exception ex)
				{
					Log.Write($"logi: event {Pid:X4}: {ex.Message}");
				}
			}
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void Raise(Action a)
	{
		try
		{
			_events.Add(a);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private byte NextSwId()
	{
		return (byte)(Interlocked.Increment(ref _swid) % 14 + 2);
	}

	public static List<HidppChannel> OpenAll(IEnumerable<ushort> skipPids)
	{
		HashSet<ushort> hashSet = new HashSet<ushort>(skipPids);
		List<HidppChannel> list = new List<HidppChannel>();
		foreach (IGrouping<ushort, HidInfo> item in from h in (from h in Hid.Enumerate(1133)
				where h.UsagePage == 65280
				select h).ToList()
			group h by h.Pid)
		{
			if (hashSet.Contains(item.Key))
			{
				continue;
			}
			HidInfo hidInfo = item.FirstOrDefault((HidInfo h) => h.OutLen == 20 && h.Usage == 2);
			HidInfo hidInfo2 = item.FirstOrDefault((HidInfo h) => h.OutLen == 7 && h.Usage == 1);
			if (!(hidInfo == null))
			{
				FileStream fileStream = Hid.Open(hidInfo.Path);
				if (fileStream != null)
				{
					FileStream sh = ((hidInfo2 != null) ? Hid.Open(hidInfo2.Path) : null);
					list.Add(new HidppChannel(item.Key, Receivers.Contains(item.Key), sh, fileStream));
				}
			}
		}
		return list;
	}

	private async Task ReadLoop(FileStream fs, int len)
	{
		byte[] buf = new byte[Math.Max(len, 64)];
		try
		{
			while (!_cts.IsCancellationRequested)
			{
				int num = await fs.ReadAsync(buf.AsMemory(0, len), _cts.Token).ConfigureAwait(continueOnCapturedContext: false);
				if (num > 0)
				{
					OnReport(buf.AsSpan(0, num).ToArray());
					continue;
				}
				break;
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex2)
		{
			Log.Write($"logi: read {Pid:X4}: {ex2.Message}");
		}
		if (!_cts.IsCancellationRequested)
		{
			_cts.Cancel();
			this.Died?.Invoke();
		}
	}

	private void OnReport(byte[] r)
	{
		if (r.Length < 4 || (r[0] != 16 && r[0] != 17))
		{
			return;
		}
		byte b = r[1];
		byte b2 = r[2];
		byte b3 = r[3];
		lock (_pendLock)
		{
			Pending pend = _pend;
			if (pend != null && b == pend.Dev)
			{
				if ((b2 == byte.MaxValue && b3 == pend.Feat && r.Length > 5 && r[4] == pend.FnSw) || (b2 == 143 && b3 == pend.Feat && r.Length > 5 && (pend.Short || r[4] == pend.FnSw)))
				{
					pend.Error = ((r.Length <= 5) ? 1 : r[5]);
					pend.Done.Set();
					_pend = null;
					return;
				}
				if (b2 == pend.Feat && b3 == pend.FnSw)
				{
					pend.Reply = r[4..];
					pend.Done.Set();
					_pend = null;
					return;
				}
			}
		}
		if (r[0] == 16 && b2 == 65 && r.Length >= 5)
		{
			bool linked = (r[4] & 0x40) == 0;
			Raise(delegate
			{
				this.Connection?.Invoke(b, linked);
			});
		}
		else if (b2 < 128 && (b3 & 0xF) == 0)
		{
			byte[] data = r[4..];
			Raise(delegate
			{
				this.Notification?.Invoke(b, b2, (byte)(b3 >> 4), data);
			});
		}
	}

	public byte[]? Call(byte dev, byte feat, byte fn, ReadOnlySpan<byte> args, out int error, int timeoutMs = 1000)
	{
		byte[] array = new byte[20]
		{
			17,
			dev,
			feat,
			(byte)((fn << 4) | NextSwId()),
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0
		};
		args.Slice(0, Math.Min(args.Length, 16)).CopyTo(array.AsSpan(4));
		return Send(_long, array, dev, feat, array[3], isShort: false, out error, timeoutMs);
	}

	public byte[]? Call(byte dev, byte feat, byte fn, params byte[] args)
	{
		int error;
		return Call(dev, feat, fn, args, out error);
	}

	// Запрос с повторами при «занято» и отсутствии ответа (пауза 60, 180, … мс). Только для запросов, которые можно
	// безопасно повторить (чтение, установка значения целиком).
	public byte[]? CallRetry(byte dev, byte feat, byte fn, ReadOnlySpan<byte> args, out int error, int attempts = 3, int timeoutMs = 1000)
	{
		byte[] array = args.ToArray();
		error = ErrTimeout;
		for (int i = 0; i < attempts; i++)
		{
			byte[] array2 = Call(dev, feat, fn, array, out error, timeoutMs);
			if (array2 != null || (error != ErrBusy && error != ErrTimeout))
			{
				return array2;
			}
			if (i + 1 < attempts)
			{
				Thread.Sleep(60 * (2 * i + 1));
			}
		}
		return null;
	}

	public byte[]? Register(byte dev, byte sub, byte reg, params byte[] args)
	{
		if (_short == null)
		{
			return null;
		}
		byte[] array = new byte[7] { 16, dev, sub, reg, 0, 0, 0 };
		args.AsSpan(0, Math.Min(args.Length, 3)).CopyTo(array.AsSpan(4));
		int error;
		return Send(_short, array, dev, sub, reg, isShort: true, out error, 500);
	}

	private byte[]? Send(FileStream fs, byte[] msg, byte dev, byte feat, byte fnsw, bool isShort, out int error, int timeoutMs)
	{
		error = -1;
		if (_cts.IsCancellationRequested)
		{
			return null;
		}
		if (!_gate.Wait(2000))
		{
			return null;
		}
		Pending pending = new Pending
		{
			Dev = dev,
			Feat = feat,
			FnSw = fnsw,
			Short = isShort
		};
		try
		{
			lock (_pendLock)
			{
				_pend = pending;
			}
			fs.Write(msg);
			if (!pending.Done.Wait(timeoutMs))
			{
				error = -1;
				return null;
			}
			error = pending.Error;
			return (pending.Error == 0) ? pending.Reply : null;
		}
		catch (Exception ex)
		{
			Log.Write($"logi: write {Pid:X4}: {ex.Message}");
			return null;
		}
		finally
		{
			lock (_pendLock)
			{
				if (_pend == pending)
				{
					_pend = null;
				}
			}
			pending.Done.Dispose();
			_gate.Release();
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		_events.CompleteAdding();
		try
		{
			_short?.Dispose();
		}
		catch
		{
		}
		try
		{
			_long.Dispose();
		}
		catch
		{
		}
	}
}
