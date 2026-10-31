using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar.Logi;

// Канал HID++ к приёмнику Logitech или к устройству на проводе (2.3, написан заново).
// Один поток-владелец берёт запросы из очереди и отправляет их строго по одному; ответ узнаётся по номеру устройства,
// индексу функции, номеру функции и программному id (2–15 по кругу), поэтому ответы другим программам (G HUB) и
// запоздавшие ответы на прошлые запросы не принимаются за свои. При «занято» и без ответа запрос повторяется, если
// вызывающий разрешил повторы. Уведомления (id 0) и события подключения приёмника (регистр 0x41) раздаются в своём
// потоке по порядку прихода, чтобы долгий обработчик не задерживал ответы. Raw — все пакеты в обе стороны (диагностика).
// Обмен с железом — через IHidppPort (HidPort — настоящий HID, в тестах — модель клавиатуры).
internal sealed class HidppLink : IDisposable
{
	// Коды ошибок: положительные — от устройства (HID++ 2.0: 8 — занято; HID++ 1.0: 1 — нет такой команды),
	// отрицательные — свои.
	public const int ErrBusy = 8;

	public const int ErrTimeout = -1;

	public const int ErrClosed = -2;

	public const int ErrUnsupported = -3;

	private const byte ReportShort = 0x10;

	private const byte ReportLong = 0x11;

	private sealed class Request
	{
		public required byte[] Msg;

		public bool Short;

		public int Attempts;

		public int TimeoutMs;

		public byte Dev;

		public byte Feat;

		public byte FnSw;

		public readonly ManualResetEventSlim Answered = new ManualResetEventSlim();

		public readonly ManualResetEventSlim Done = new ManualResetEventSlim();

		public byte[]? Reply;

		public int Error = ErrTimeout;
	}

	// Приёмники Unifying, Lightspeed и Bolt: у них устройства по номерам 1–6, у остальных (провод) — номер 0xFF.
	private static readonly HashSet<ushort> Receivers = new HashSet<ushort> { 0xC52B, 0xC532, 0xC534, 0xC539, 0xC53A, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548 };

	private readonly IHidppPort _port;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly BlockingCollection<Request> _requests = new BlockingCollection<Request>();

	private readonly BlockingCollection<Action> _events = new BlockingCollection<Action>();

	private readonly object _pendLock = new object();

	private Request? _pend;

	private int _swid;

	public ushort Pid { get; }

	public bool IsReceiver { get; }

	public string Path { get; }

	public bool Alive => !_cts.IsCancellationRequested;

	// (устройство, индекс функции, номер функции, данные) — уведомление с программным id 0.
	public event Action<byte, byte, byte, byte[]>? Notification;

	// (устройство, на связи) — событие подключения от приёмника.
	public event Action<byte, bool>? Connection;

	// (входящий, пакет целиком) — для журнала диагностики; вызывается в потоке чтения, обработчик должен быть коротким.
	public event Action<bool, byte[]>? Raw;

	public event Action? Died;

	public HidppLink(IHidppPort port, ushort pid, bool receiver, string path)
	{
		Pid = pid;
		IsReceiver = receiver;
		Path = path;
		_port = port;
		_port.Received += OnReport;
		_port.Closed += OnClosed;
		new Thread(Own)
		{
			IsBackground = true,
			Name = $"HID++ {pid:X4} requests"
		}.Start();
		new Thread(Dispatch)
		{
			IsBackground = true,
			Name = $"HID++ {pid:X4} events"
		}.Start();
		_port.Start();
	}

	// Все каналы HID++ Logitech (VID 046D, страница 0xFF00): длинные пакеты (20 байт) обязательны, короткие (7) — если есть.
	public static List<HidppLink> OpenAll(IEnumerable<ushort>? skipPids = null)
	{
		HashSet<ushort> skip = new HashSet<ushort>(skipPids ?? Array.Empty<ushort>());
		List<HidppLink> list = new List<HidppLink>();
		foreach (IGrouping<ushort, HidInfo> g in Hid.Enumerate(0x046D).Where((HidInfo h) => h.UsagePage == 0xFF00).GroupBy((HidInfo h) => h.Pid))
		{
			if (skip.Contains(g.Key))
			{
				continue;
			}
			HidInfo? lg = g.FirstOrDefault((HidInfo h) => h.OutLen == 20 && h.Usage == 2);
			HidInfo? sh = g.FirstOrDefault((HidInfo h) => h.OutLen == 7 && h.Usage == 1);
			if (lg == null)
			{
				continue;
			}
			FileStream? fl = Hid.Open(lg.Path);
			if (fl == null)
			{
				continue;
			}
			FileStream? fs = (sh != null) ? Hid.Open(sh.Path) : null;
			list.Add(new HidppLink(new HidPort(fs, fl), g.Key, Receivers.Contains(g.Key), lg.Path));
		}
		return list;
	}

	// Запрос к функции HID++ 2.0: feat — индекс функции у устройства, fn — номер функции (0–15), до 16 байт аргументов.
	// attempts > 1 — повторять при «занято» и без ответа (только для запросов, которые безопасно повторить).
	public byte[]? Call(byte dev, byte feat, byte fn, ReadOnlySpan<byte> args, out int error, int attempts = 1, int timeoutMs = 1000)
	{
		byte[] msg = new byte[20];
		msg[0] = ReportLong;
		msg[1] = dev;
		msg[2] = feat;
		msg[3] = (byte)(fn << 4);
		args.Slice(0, Math.Min(args.Length, 16)).CopyTo(msg.AsSpan(4));
		return Send(new Request
		{
			Msg = msg,
			Attempts = Math.Max(1, attempts),
			TimeoutMs = timeoutMs,
			Dev = dev,
			Feat = feat
		}, out error);
	}

	// Регистр HID++ 1.0 (приёмник): sub — 0x80/0x81 запись/чтение короткого, 0x82/0x83 — длинного; до 3 байт аргументов.
	public byte[]? Register(byte dev, byte sub, byte reg, ReadOnlySpan<byte> args, out int error, int attempts = 2)
	{
		if (!_port.HasShort)
		{
			error = ErrUnsupported;
			return null;
		}
		byte[] msg = new byte[7];
		msg[0] = ReportShort;
		msg[1] = dev;
		msg[2] = sub;
		msg[3] = reg;
		args.Slice(0, Math.Min(args.Length, 3)).CopyTo(msg.AsSpan(4));
		return Send(new Request
		{
			Msg = msg,
			Short = true,
			Attempts = Math.Max(1, attempts),
			TimeoutMs = 500,
			Dev = dev,
			Feat = sub,
			FnSw = reg
		}, out error);
	}

	private byte[]? Send(Request q, out int error)
	{
		if (_cts.IsCancellationRequested)
		{
			error = ErrClosed;
			return null;
		}
		try
		{
			_requests.Add(q);
		}
		catch (InvalidOperationException)
		{
			error = ErrClosed;
			return null;
		}
		// Владелец ответит не позже, чем за все попытки с паузами; запас — на очередь перед этим запросом.
		int wait = q.Attempts * (q.TimeoutMs + 400) + 5000;
		if (!q.Done.Wait(wait))
		{
			error = ErrTimeout;
			return null;
		}
		error = q.Error;
		return (q.Error == 0) ? q.Reply : null;
	}

	private byte NextSwId()
	{
		return (byte)(Interlocked.Increment(ref _swid) % 14 + 2);
	}

	// Поток-владелец: единственный, кто пишет в устройство.
	private void Own()
	{
		try
		{
			foreach (Request q in _requests.GetConsumingEnumerable(_cts.Token))
			{
				for (int i = 0; i < q.Attempts; i++)
				{
					if (i > 0)
					{
						Thread.Sleep(60 * (2 * i - 1));
					}
					if (!q.Short)
					{
						q.Msg[3] = (byte)((q.Msg[3] & 0xF0) | NextSwId());
						q.FnSw = q.Msg[3];
					}
					q.Answered.Reset();
					q.Reply = null;
					q.Error = ErrTimeout;
					lock (_pendLock)
					{
						_pend = q;
					}
					try
					{
						Raw?.Invoke(false, q.Msg);
						_port.Write(q.Msg);
						q.Answered.Wait(q.TimeoutMs, _cts.Token);
					}
					catch (OperationCanceledException)
					{
						q.Error = ErrClosed;
					}
					catch (Exception ex)
					{
						Log.Write($"logi: write {Pid:X4}: {ex.Message}");
						q.Error = ErrClosed;
					}
					lock (_pendLock)
					{
						_pend = null;
					}
					if (q.Error != ErrBusy && q.Error != ErrTimeout)
					{
						break;
					}
				}
				q.Done.Set();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
		// Закрытие: всем, кто ещё ждёт, — «канал закрыт».
		foreach (Request q in _requests)
		{
			q.Error = ErrClosed;
			q.Done.Set();
		}
	}

	private void Dispatch()
	{
		try
		{
			foreach (Action a in _events.GetConsumingEnumerable())
			{
				try
				{
					a();
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

	private void OnClosed()
	{
		if (!_cts.IsCancellationRequested)
		{
			_cts.Cancel();
			Died?.Invoke();
		}
	}

	private void OnReport(byte[] r)
	{
		if (r.Length < 5 || (r[0] != ReportShort && r[0] != ReportLong))
		{
			return;
		}
		try
		{
			Raw?.Invoke(true, r);
		}
		catch
		{
		}
		byte dev = r[1];
		byte b2 = r[2];
		byte b3 = r[3];
		lock (_pendLock)
		{
			Request? q = _pend;
			if (q != null && dev == q.Dev && !q.Answered.IsSet)
			{
				// Ошибка HID++ 2.0 (0xFF) или 1.0 (0x8F): [индекс функции или sub][fn|id или регистр][код].
				if ((b2 == 0xFF || b2 == 0x8F) && b3 == q.Feat && r.Length > 5 && r[4] == q.FnSw)
				{
					q.Error = r[5];
					q.Answered.Set();
					return;
				}
				if (b2 == q.Feat && b3 == q.FnSw)
				{
					q.Reply = r[4..];
					q.Error = 0;
					q.Answered.Set();
					return;
				}
			}
		}
		if (r[0] == ReportShort && b2 == 0x41)
		{
			bool linked = (r[4] & 0x40) == 0;
			Raise(delegate
			{
				Connection?.Invoke(dev, linked);
			});
		}
		else if (b2 < 0x80 && (b3 & 0x0F) == 0)
		{
			byte[] data = r[4..];
			byte fn = (byte)(b3 >> 4);
			Raise(delegate
			{
				Notification?.Invoke(dev, b2, fn, data);
			});
		}
	}

	public void Dispose()
	{
		if (!_cts.IsCancellationRequested)
		{
			_cts.Cancel();
		}
		_requests.CompleteAdding();
		_events.CompleteAdding();
		_port.Received -= OnReport;
		_port.Closed -= OnClosed;
		_port.Dispose();
	}
}

// Обмен пакетами HID++ с железом: Write — короткий (7 байт, 0x10) или длинный (20 байт, 0x11) пакет; Received — каждый
// входящий пакет (в потоке чтения); Closed — устройство пропало. Start вызывается после подписки на события.
internal interface IHidppPort : IDisposable
{
	bool HasShort { get; }

	event Action<byte[]>? Received;

	event Action? Closed;

	void Start();

	void Write(byte[] report);
}

// Настоящий HID: две коллекции приёмника или устройства (короткие и длинные пакеты), чтение асинхронное.
internal sealed class HidPort : IHidppPort
{
	private readonly FileStream? _short;

	private readonly FileStream _long;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	public bool HasShort => _short != null;

	public event Action<byte[]>? Received;

	public event Action? Closed;

	public HidPort(FileStream? sh, FileStream lg)
	{
		_short = sh;
		_long = lg;
	}

	public void Start()
	{
		if (_short != null)
		{
			_ = ReadLoop(_short, 7);
		}
		_ = ReadLoop(_long, 20);
	}

	public void Write(byte[] report)
	{
		((report[0] == 0x10 && _short != null) ? _short : _long).Write(report);
	}

	private async Task ReadLoop(FileStream fs, int len)
	{
		byte[] buf = new byte[64];
		try
		{
			while (!_cts.IsCancellationRequested)
			{
				int n = await fs.ReadAsync(buf.AsMemory(0, len), _cts.Token).ConfigureAwait(continueOnCapturedContext: false);
				if (n <= 0)
				{
					break;
				}
				Received?.Invoke(buf.AsSpan(0, n).ToArray());
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Log.Write($"logi: read: {ex.Message}");
		}
		if (!_cts.IsCancellationRequested)
		{
			_cts.Cancel();
			Closed?.Invoke();
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
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
