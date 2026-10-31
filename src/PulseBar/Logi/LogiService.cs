using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar.Logi;

internal sealed class LogiService : IDisposable
{
	private struct LASTINPUTINFO
	{
		public uint cbSize;

		public uint dwTime;
	}

	private readonly object _lock = new object();

	private readonly List<HidppChannel> _channels = new List<HidppChannel>();

	private readonly List<HidppDevice> _devices = new List<HidppDevice>();

	private readonly Dictionary<HidppDevice, Dictionary<(int cluster, int effect), byte>> _effects = new Dictionary<HidppDevice, Dictionary<(int, int), byte>>();

	private readonly Timer _batteryTimer;

	private readonly Timer _idleTimer;

	private volatile bool _idle;

	private List<PointingDevice> _otherMice = new List<PointingDevice>();

	private Task _queue = Task.CompletedTask;

	private int _auto = -1;

	private int _gPrev;

	private int _mPrev;

	private int _mrPrev;

	private long _rescanAt;

	private bool _disposed;

	private static readonly Dictionary<string, int> EffectIds = new Dictionary<string, int>
	{
		["off"] = 0,
		["static"] = 1
	};

	private readonly Dictionary<HidppDevice, byte[]> _keyIds = new Dictionary<HidppDevice, byte[]>();

	// 2.2: какие клавиатуры переведены в режим «управляет программа» (true) или оставлены на встроенном профиле (false).
	private readonly Dictionary<HidppDevice, bool> _hostMode = new Dictionary<HidppDevice, bool>();

	// Кадр подсветки не подтверждён — повторить при сверке.
	private readonly HashSet<HidppDevice> _paintFailed = new HashSet<HidppDevice>();

	private readonly Timer _reconcileTimer;

	private volatile bool _ghub;

	// Нажатия M подряд: применяется только последнее.
	private int _applyVersion;

	public LogiConfig Config { get; }

	public bool ReadOnly { get; init; }

	public IReadOnlyList<HidppDevice> Devices
	{
		get
		{
			lock (_lock)
			{
				return _devices.ToArray();
			}
		}
	}

	public HidppDevice? Keyboard => Devices.FirstOrDefault((HidppDevice d) => d.IsKeyboard && d.Protocol >= 2);

	public int Current
	{
		get
		{
			if (_auto < 0 || _auto >= Config.Profiles.Count)
			{
				return Config.Active;
			}
			return _auto;
		}
	}

	public bool AutoSwitched => _auto >= 0;

	public string KeyboardName => Keyboard?.Name ?? Devices.FirstOrDefault((HidppDevice d) => d.IsKeyboard)?.Name ?? "";

	public float KeyboardBattery
	{
		get
		{
			HidppDevice keyboard = Keyboard;
			if (keyboard == null || !keyboard.Online || keyboard.BatteryPercent < 0)
			{
				return float.NaN;
			}
			return keyboard.BatteryPercent;
		}
	}

	public bool KeyboardCharging => Keyboard?.Charging ?? false;

	public IReadOnlyList<PointingDevice> OtherMiceList
	{
		get
		{
			lock (_lock)
			{
				return _otherMice.ToArray();
			}
		}
	}

	public (string Name, float Battery, bool Charging, bool Online) CurrentMouse
	{
		get
		{
			HidppDevice hidppDevice = Devices.FirstOrDefault((HidppDevice d) => d.IsMouse && d.Online);
			if (hidppDevice != null)
			{
				return (Name: hidppDevice.Name, Battery: (hidppDevice.BatteryPercent >= 0) ? ((float)hidppDevice.BatteryPercent) : float.NaN, Charging: hidppDevice.Charging, Online: true);
			}
			PointingDevice pointingDevice;
			lock (_lock)
			{
				pointingDevice = _otherMice.FirstOrDefault();
			}
			if (pointingDevice != null)
			{
				return (Name: pointingDevice.Name, Battery: (pointingDevice.BatteryPercent >= 0) ? ((float)pointingDevice.BatteryPercent) : float.NaN, Charging: pointingDevice.Charging, Online: true);
			}
			HidppDevice hidppDevice2 = Devices.FirstOrDefault((HidppDevice d) => d.IsMouse);
			if (hidppDevice2 == null)
			{
				return (Name: "", Battery: float.NaN, Charging: false, Online: false);
			}
			return (Name: hidppDevice2.Name, Battery: float.NaN, Charging: false, Online: false);
		}
	}

	public event Action? Changed;

	public LogiService(LogiConfig config)
	{
		Config = config;
		_batteryTimer = new Timer(delegate
		{
			Post(ReadBatteries);
		}, null, -1, -1);
		_idleTimer = new Timer(delegate
		{
			CheckIdle();
		}, null, -1, -1);
		_reconcileTimer = new Timer(delegate
		{
			Post(Reconcile);
		}, null, -1, -1);
	}

	private void CheckIdle()
	{
		if (ReadOnly)
		{
			return;
		}
		LASTINPUTINFO li = new LASTINPUTINFO
		{
			cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
		};
		if (GetLastInputInfo(ref li))
		{
			uint num = (uint)Environment.TickCount - li.dwTime;
			bool flag = Config.IdleMinutes > 0 && num >= (uint)(Config.IdleMinutes * 60000);
			if (flag != _idle)
			{
				_idle = flag;
				Post(ApplyBrightness);
			}
		}
	}

	private void ApplyBrightness()
	{
		foreach (HidppDevice item in Devices.Where((HidppDevice d) => d.Online && d.IsKeyboard && d.Has(32832)))
		{
			if (Controlling && _hostMode.TryGetValue(item, out var value) && value)
			{
				SetBrightness(item);
			}
		}
	}

	private void SetBrightness(HidppDevice d)
	{
		int num = Math.Clamp(_idle ? Config.IdleBrightness : Config.Profiles[Current].Brightness, 0, 100);
		d.Call(32832, 2, (byte)(num >> 8), (byte)num);
	}

	[DllImport("user32.dll")]
	private static extern bool GetLastInputInfo(ref LASTINPUTINFO li);

	public static bool GHubRunning()
	{
		string[] array = new string[3] { "lghub_agent", "lghub", "lghub_system_tray" };
		for (int i = 0; i < array.Length; i++)
		{
			Process[] processesByName = Process.GetProcessesByName(array[i]);
			bool flag = processesByName.Length != 0;
			Process[] array2 = processesByName;
			for (int j = 0; j < array2.Length; j++)
			{
				array2[j].Dispose();
			}
			if (flag)
			{
				return true;
			}
		}
		return false;
	}

	// С 2.2 устройства ищутся всегда (заряд на строке), а управляет клавиатурой модуль, только если включено и G HUB не запущен.
	public void Start()
	{
		_ghub = GHubRunning();
		Post(Scan);
		_batteryTimer.Change(TimeSpan.FromMinutes(5L), TimeSpan.FromMinutes(5L));
		_idleTimer.Change(1000, 1000);
		_reconcileTimer.Change(20000, 20000);
	}

	public void Stop()
	{
		_batteryTimer.Change(-1, -1);
		_idleTimer.Change(-1, -1);
		_reconcileTimer.Change(-1, -1);
		Post(delegate
		{
			ReleaseAll();
			lock (_lock)
			{
				foreach (HidppChannel channel in _channels)
				{
					channel.Dispose();
				}
				_channels.Clear();
				_devices.Clear();
				_effects.Clear();
				_keyIds.Clear();
			}
			RaiseChanged();
		});
	}

	public void DevicesChanged()
	{
		long value = Environment.TickCount64 + 1500;
		Interlocked.Exchange(ref _rescanAt, value);
		Task.Delay(1600).ContinueWith(delegate
		{
			if (Interlocked.Read(in _rescanAt) <= Environment.TickCount64)
			{
				Post(Scan);
			}
		});
	}

	public void SelectProfile(int index)
	{
		if (index >= 0 && index < Config.Profiles.Count)
		{
			Config.Active = index;
			_auto = -1;
			Config.Save();
			Post(ApplyAll);
			RaiseChanged();
		}
	}

	public void OnForeground(nint hwnd)
	{
		if (!Config.Enabled || !Config.Profiles.Any((Profile p) => p.Apps.Length > 0))
		{
			return;
		}
		string exe = ExeName(hwnd);
		if (exe.Length != 0 && !exe.Equals("PulseBar.exe", StringComparison.OrdinalIgnoreCase))
		{
			int num = Config.Profiles.FindIndex((Profile p) => p.MatchesApp(exe));
			if (num != _auto)
			{
				_auto = num;
				Post(ApplyAll);
				RaiseChanged();
			}
		}
	}

	public void ApplyNow()
	{
		Post(ApplyAll);
	}

	public void Save()
	{
		Config.Save();
	}

	private void Post(Action a)
	{
		lock (_lock)
		{
			if (_disposed)
			{
				return;
			}
			_queue = _queue.ContinueWith(delegate
			{
				try
				{
					a();
				}
				catch (Exception ex)
				{
					Log.Write("logi: " + ex);
				}
			}, TaskScheduler.Default);
		}
	}

	private void RaiseChanged()
	{
		try
		{
			this.Changed?.Invoke();
		}
		catch (Exception ex)
		{
			Log.Write("logi: changed: " + ex.Message);
		}
	}

	private void Scan()
	{
		if (_disposed)
		{
			return;
		}
		List<ushort> skipPids;
		lock (_lock)
		{
			foreach (HidppChannel dead in _channels.Where((HidppChannel c) => !c.Alive).ToList())
			{
				dead.Dispose();
				_channels.Remove(dead);
				foreach (HidppDevice item in _devices.Where((HidppDevice d) => d.Channel == dead).ToList())
				{
					_devices.Remove(item);
					_effects.Remove(item);
					_keyIds.Remove(item);
				}
			}
			skipPids = _channels.Select((HidppChannel c) => c.Pid).ToList();
		}
		foreach (HidppChannel ch in HidppChannel.OpenAll(skipPids))
		{
			ch.Notification += delegate(byte dev, byte feat, byte fn, byte[] data)
			{
				OnNotification(ch, dev, feat, fn, data);
			};
			ch.Connection += delegate(byte dev, bool linked)
			{
				Post(delegate
				{
					OnConnection(ch, dev, linked);
				});
			};
			ch.Died += delegate
			{
				Post(Scan);
			};
			lock (_lock)
			{
				_channels.Add(ch);
			}
			List<HidppDevice> list = new List<HidppDevice>();
			if (ch.IsReceiver)
			{
				if (!ReadOnly)
				{
					EnableReceiverNotifications(ch);
				}
				for (byte b = 1; b <= 6; b++)
				{
					string text = PairedName(ch, b);
					if (text.Length != 0)
					{
						HidppDevice hidppDevice = HidppDevice.Probe(ch, b, text);
						if (hidppDevice != null)
						{
							list.Add(hidppDevice);
						}
					}
				}
			}
			else
			{
				HidppDevice hidppDevice2 = HidppDevice.Probe(ch, byte.MaxValue, "");
				if (hidppDevice2 != null)
				{
					list.Add(hidppDevice2);
				}
			}
			foreach (HidppDevice item2 in list)
			{
				lock (_lock)
				{
					_devices.Add(item2);
				}
				Log.Write($"logi: {ch.Pid:X4}/{item2.Index}: {item2.Name} (HID++ {item2.Protocol}, {(item2.Online ? "online" : "asleep")})");
				if (item2.Online)
				{
					SetUp(item2);
				}
			}
		}
		List<PointingDevice> list2 = OtherMice.Find();
		foreach (PointingDevice item3 in list2)
		{
			OtherMice.ReadBattery(item3);
		}
		lock (_lock)
		{
			_otherMice = list2;
		}
		RaiseChanged();
	}

	private static void EnableReceiverNotifications(HidppChannel ch)
	{
		byte[]? array = ch.Register(byte.MaxValue, 129, 0);
		byte b = (byte)((array != null) ? array[0] : 0);
		byte b2 = (byte)((array != null) ? array[1] : 0);
		byte b3 = (byte)((array != null) ? array[2] : 0);
		ch.Register(byte.MaxValue, 128, 0, b, (byte)(b2 | 9), b3);
	}

	internal static string PairedName(HidppChannel ch, byte slot)
	{
		byte[] array = ch.Register(byte.MaxValue, 131, 181, (byte)(64 + slot - 1));
		if (array == null || array.Length < 3)
		{
			return "";
		}
		int count = Math.Min(array[1], array.Length - 2);
		return Encoding.ASCII.GetString(array, 2, count).Trim('\0', ' ');
	}

	private void OnConnection(HidppChannel ch, byte dev, bool linked)
	{
		HidppDevice hidppDevice = Devices.FirstOrDefault((HidppDevice x) => x.Channel == ch && x.Index == dev);
		if (hidppDevice == null)
		{
			if (linked)
			{
				Post(Scan);
			}
			return;
		}
		hidppDevice.Online = linked;
		if (linked)
		{
			Thread.Sleep(300);
			hidppDevice.LoadFeatures();
			SetUp(hidppDevice);
		}
		RaiseChanged();
	}

	// Гибрид (2.2): если текущему профилю нужны цвета отдельных клавиш или действия PulseBar — режим «управляет
	// программа» (G-клавиши приходят в PulseBar, подсветка рисуется кадрами); иначе клавиатура остаётся на встроенном
	// профиле, а PulseBar только выбирает номер профиля.
	private void SetUp(HidppDevice d)
	{
		if (d.IsKeyboard && d.Protocol >= 2 && Controlling)
		{
			Profile profile = Config.Profiles[Current];
			bool flag = !d.Has(33024) || OnboardBuilder.NeedsHost(profile);
			_hostMode[d] = flag;
			_paintFailed.Remove(d);
			if (flag)
			{
				if (d.Has(33024))
				{
					d.Call(33024, 1, 2);
				}
				if (d.Has(32784))
				{
					d.Call(32784, 2, 1);
				}
				_gPrev = (_mPrev = (_mrPrev = 0));
				Apply(d);
			}
			else
			{
				if (d.Has(32784))
				{
					d.Call(32784, 2, default(byte));
				}
				d.Call(33024, 1, 1);
				if (Current < 3)
				{
					d.Call(33024, 3, default(byte), (byte)(Current + 1));
				}
			}
			Log.Write($"logi: {d.Name}: profile {Current + 1}, {(flag ? "host mode (PulseBar draws key colours)" : "onboard mode")}");
		}
		if (!d.ReadBattery())
		{
			d.BatteryPercent = (d.Online ? d.BatteryPercent : (-1));
		}
	}

	private void ApplyAll()
	{
		if (!Controlling)
		{
			return;
		}
		foreach (HidppDevice item in Devices.Where((HidppDevice d) => d.Online && d.IsKeyboard && d.Protocol >= 2))
		{
			SetUp(item);
		}
		RaiseChanged();
	}

	private void Apply(HidppDevice d)
	{
		Profile profile = Config.Profiles[Current];
		if (d.Has(32897))
		{
			PaintKeys(d, profile);
		}
		else if (d.Has(32881))
		{
			SetLighting(d, 0, profile.Keyboard);
			SetLighting(d, 1, profile.Logo);
		}
		if (d.Has(32832))
		{
			SetBrightness(d);
		}
		if (d.Has(32800))
		{
			d.Call(32800, 1, (byte)((Current < 3) ? ((uint)(1 << Current)) : 0u));
		}
		if (d.Has(32816))
		{
			d.Call(32816, 0, default(byte));
		}
	}

	private void SetLighting(HidppDevice d, int cluster, Lighting l)
	{
		if (!_effects.TryGetValue(d, out Dictionary<(int, int), byte> value))
		{
			value = new Dictionary<(int, int), byte>();
			for (int i = 0; i < 4; i++)
			{
				for (int j = 0; j < 16; j++)
				{
					byte[] array = d.Call(32881, 0, (byte)i, (byte)j);
					if (array == null)
					{
						break;
					}
					value[(i, (array[2] << 8) | array[3])] = array[1];
				}
			}
			_effects[d] = value;
		}
		int valueOrDefault = EffectIds.GetValueOrDefault(l.Effect, 1);
		if (value.TryGetValue((cluster, valueOrDefault), out var value2))
		{
			(byte r, byte g, byte b) tuple = ParseColor(l.Color);
			byte item = tuple.r;
			byte item2 = tuple.g;
			byte item3 = tuple.b;
			byte[] array2 = new byte[13]
			{
				(byte)cluster,
				value2,
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
			if (valueOrDefault == 1)
			{
				array2[2] = item;
				array2[3] = item2;
				array2[4] = item3;
			}
			array2[12] = 0;
			if (d.Channel.Call(d.Index, d.Feat(32881), 1, array2, out var error) == null)
			{
				Log.Write($"logi: lighting {cluster}/{l.Effect} failed ({error})");
			}
		}
	}

	private void PaintKeys(HidppDevice d, Profile p)
	{
		byte br;
		byte bg;
		byte bb;
		if (!(p.Keyboard.Effect == "off"))
		{
			(br, bg, bb) = ParseColor(p.Keyboard.Color);
		}
		else
		{
			br = 0;
			bg = 0;
			bb = 0;
		}
		bool flag = !_keyIds.ContainsKey(d);
		if (flag && !PaintAll(d, br, bg, bb, commit: false))
		{
			return;
		}
		byte[] source = _keyIds[d];
		byte feat = d.Feat(32897);
		string value;
		foreach (IGrouping<(byte, byte, byte), byte> item4 in from id in source
			group id by ((byte, byte, byte))((!p.KeyColors.TryGetValue(id.ToString("X2"), out value)) ? (br, bg, bb) : ParseColor(value)))
		{
			if (flag)
			{
				(byte, byte, byte) key = item4.Key;
				byte item = key.Item1;
				byte item2 = key.Item2;
				byte item3 = key.Item3;
				byte b = br;
				byte b2 = bg;
				byte b3 = bb;
				if (item == b && item2 == b2 && item3 == b3)
				{
					continue;
				}
			}
			byte[] array = item4.ToArray();
			for (int num = 0; num < array.Length; num += 13)
			{
				if (!Send(d, feat, item4.Key.Item1, item4.Key.Item2, item4.Key.Item3, array.AsSpan(num, Math.Min(13, array.Length - num)).ToArray(), out var err))
				{
					Log.Write($"logi: per-key paint failed ({err})");
					if (err != -1)
					{
						_keyIds.Remove(d);
						return;
					}
				}
			}
		}
		if (d.Call(32897, 7, default(byte), default(byte), default(byte), default(byte), default(byte)) == null)
		{
			_paintFailed.Add(d);
			Log.Write("logi: per-key frame not confirmed, will retry");
		}
	}

	private bool PaintAll(HidppDevice d, byte red, byte green, byte blue, bool commit = true)
	{
		byte feat = d.Feat(32897);
		if (!_keyIds.TryGetValue(d, out byte[] value))
		{
			List<byte> list = new List<byte>();
			for (int i = 1; i < 256; i += 13)
			{
				byte[] array = (from num3 in Enumerable.Range(i, Math.Min(13, 256 - i))
					select (byte)num3).ToArray();
				if (Send(d, feat, red, green, blue, array, out var err))
				{
					list.AddRange(array);
					continue;
				}
				if (err != 2)
				{
					Log.Write($"logi: per-key paint failed ({err})");
					return false;
				}
				byte[] array2 = array;
				foreach (byte b in array2)
				{
					if (Send(d, feat, red, green, blue, new byte[1] { b }, out var _))
					{
						list.Add(b);
					}
				}
			}
			value = (_keyIds[d] = list.ToArray());
			Log.Write($"logi: {value.Length} lit keys");
		}
		else
		{
			for (int num2 = 0; num2 < value.Length; num2 += 13)
			{
				if (!Send(d, feat, red, green, blue, value.AsSpan(num2, Math.Min(13, value.Length - num2)).ToArray(), out var err3))
				{
					Log.Write($"logi: per-key paint failed ({err3})");
					_keyIds.Remove(d);
					return false;
				}
			}
		}
		if (commit)
		{
			d.Call(32897, 7, default(byte), default(byte), default(byte), default(byte), default(byte));
		}
		return true;
	}

	private static bool Send(HidppDevice d, byte feat, byte red, byte green, byte blue, byte[] ids, out int err)
	{
		byte[] array = new byte[16]
		{
			red, green, blue, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0
		};
		for (int i = 0; i < 13; i++)
		{
			array[3 + i] = ids[Math.Min(i, ids.Length - 1)];
		}
		int num = 0;
		while (true)
		{
			if (d.Channel.Call(d.Index, feat, 6, array, out err) != null)
			{
				return true;
			}
			if (err != -1 || num == 1)
			{
				break;
			}
			num++;
		}
		return false;
	}

	public static (byte r, byte g, byte b) ParseColor(string hex)
	{
		hex = hex.TrimStart('#');
		if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
		{
			return (r: (byte)(result >> 16), g: (byte)(result >> 8), b: (byte)result);
		}
		return (r: byte.MaxValue, g: byte.MaxValue, b: byte.MaxValue);
	}

	private void OnNotification(HidppChannel ch, byte dev, byte feat, byte fn, byte[] data)
	{
		HidppDevice hidppDevice;
		lock (_lock)
		{
			hidppDevice = _devices.FirstOrDefault((HidppDevice x) => x.Channel == ch && x.Index == dev);
		}
		if (hidppDevice == null || !hidppDevice.IsKeyboard || data.Length < 2 || !Controlling)
		{
			return;
		}
		if (feat == hidppDevice.Feat(33024) && fn == 0)
		{
			// Встроенный режим: клавиатура сама переключила профиль (M1–M3).
			int num0 = ((data[0] << 8) | data[1]) - 1;
			if (num0 >= 0 && num0 < Config.Profiles.Count && num0 != Config.Active)
			{
				Log.Write($"logi: keyboard switched to profile {num0 + 1}");
				Config.Active = num0;
				_auto = -1;
				Post(delegate
				{
					Config.Save();
					if (OnboardBuilder.NeedsHost(Config.Profiles[num0]))
					{
						SetUp(hidppDevice);
					}
					RaiseChanged();
				});
			}
			return;
		}
		Profile profile = Config.Profiles[Current];
		if (feat == hidppDevice.Feat(32784))
		{
			int num = data[0] | (data[1] << 8);
			int num2 = num & ~_gPrev;
			_gPrev = num;
			for (int num3 = 0; num3 < 5; num3++)
			{
				if ((num2 & (1 << num3)) != 0)
				{
					Log.Write($"logi: G{num3 + 1} ({profile.Key("G" + (num3 + 1)).Type})");
					Macro.Run(profile.Key("G" + (num3 + 1)));
				}
			}
		}
		else if (feat == hidppDevice.Feat(32800))
		{
			int num4 = data[0];
			int num5 = num4 & ~_mPrev;
			_mPrev = num4;
			for (int num6 = 0; num6 < 3; num6++)
			{
				if ((num5 & (1 << num6)) != 0 && num6 < Config.Profiles.Count)
				{
					int active = num6;
					Log.Write($"logi: M{num6 + 1}");
					Config.Active = active;
					_auto = -1;
					int version = Interlocked.Increment(ref _applyVersion);
					Post(delegate
					{
						if (version == Volatile.Read(ref _applyVersion))
						{
							Config.Save();
							ApplyAll();
						}
					});
				}
			}
		}
		else if (feat == hidppDevice.Feat(32816))
		{
			int num7 = data[0] & 1;
			if (num7 != 0 && _mrPrev == 0)
			{
				Log.Write("logi: MR");
				Macro.Run(profile.Key("MR"));
			}
			_mrPrev = num7;
		}
	}

	private void ReadBatteries()
	{
		foreach (HidppDevice device in Devices)
		{
			if (device.Online)
			{
				if (!device.ReadBattery())
				{
					device.Online = device.LoadFeatures();
				}
			}
			else if (device.LoadFeatures())
			{
				device.Online = true;
				Log.Write("logi: " + device.Name + " woke up");
				SetUp(device);
			}
		}
		List<PointingDevice> otherMice;
		lock (_lock)
		{
			otherMice = _otherMice;
		}
		foreach (PointingDevice item in otherMice)
		{
			OtherMice.ReadBattery(item);
		}
		RaiseChanged();
	}

	public string ShortStatus()
	{
		List<string> list = new List<string>();
		string keyboardName = KeyboardName;
		if (keyboardName.Length > 0)
		{
			float keyboardBattery = KeyboardBattery;
			list.Add(keyboardName.Split(' ')[0] + " " + (float.IsNaN(keyboardBattery) ? "—" : $"{keyboardBattery:0}%{(KeyboardCharging ? "⚡" : "")}"));
		}
		(string, float, bool, bool) currentMouse = CurrentMouse;
		if (currentMouse.Item1.Length > 0)
		{
			string text;
			if (!currentMouse.Item1.StartsWith("Razer "))
			{
				(text, _, _, _) = currentMouse;
			}
			else
			{
				string item = currentMouse.Item1;
				text = item.Substring(6, item.Length - 6);
			}
			string text2 = text;
			list.Add(text2 + " " + (float.IsNaN(currentMouse.Item2) ? "—" : $"{currentMouse.Item2:0}%{(currentMouse.Item3 ? "⚡" : "")}"));
		}
		return string.Join(", ", list);
	}

	// ---------- 2.2: гибрид встроенной памяти и подсветки из программы ----------

	// Управлять клавиатурой можно, если это включено, это не снимок окна и не запущен G HUB (он перехватывает режим).
	private bool Controlling
	{
		get
		{
			if (Config.Enabled && !ReadOnly)
			{
				return !_ghub;
			}
			return false;
		}
	}

	public bool GHubActive => _ghub;

	// Последний результат записи / восстановления / копии — для окна настроек.
	public string LastResult { get; private set; } = "";

	public string ModeText(HidppDevice d)
	{
		if (!_hostMode.TryGetValue(d, out var value))
		{
			return "";
		}
		if (!value)
		{
			return "onboard";
		}
		return "host";
	}

	// Раз в 20 с: G HUB появился/закрылся; режим клавиатуры не тот, что нужен (сбросила после сна, перехватила
	// другая программа); не дошёл кадр подсветки — привести к нужному состоянию.
	private void Reconcile()
	{
		bool flag = GHubRunning();
		if (flag != _ghub)
		{
			_ghub = flag;
			Log.Write(flag ? "logi: G HUB started, keyboard left to it" : "logi: G HUB closed, PulseBar controls the keyboard");
			if (!flag)
			{
				foreach (HidppDevice item in Devices.Where((HidppDevice d) => d.Online && d.IsKeyboard))
				{
					SetUp(item);
				}
			}
			else
			{
				_hostMode.Clear();
			}
			RaiseChanged();
		}
		if (!Controlling)
		{
			return;
		}
		foreach (HidppDevice item2 in Devices.Where((HidppDevice d) => d.Online && d.IsKeyboard && d.Protocol >= 2))
		{
			bool value;
			bool flag2 = _hostMode.TryGetValue(item2, out value) && value;
			int num = (item2.Has(33024) ? (item2.Call(33024, 2)?[0] ?? (-1)) : (-1));
			if ((num > 0 && num != (flag2 ? 2 : 1)) || _paintFailed.Contains(item2))
			{
				Log.Write($"logi: {item2.Name}: mode {num}, wanted {(flag2 ? 2 : 1)}{(_paintFailed.Contains(item2) ? ", lighting not confirmed" : "")} — restoring");
				SetUp(item2);
			}
		}
	}

	// При выходе и когда управление выключено: вернуть клавиатуру во встроенный режим (подсветка из её профиля).
	private void ReleaseAll()
	{
		foreach (HidppDevice item in Devices.Where((HidppDevice d) => d.Online && d.IsKeyboard))
		{
			if (_hostMode.TryGetValue(item, out var value) && value)
			{
				if (item.Has(32784))
				{
					item.Call(32784, 2, default(byte));
				}
				if (item.Has(33024))
				{
					item.Call(33024, 1, 1);
				}
				Log.Write("logi: " + item.Name + ": back to onboard mode");
			}
		}
		_hostMode.Clear();
	}

	public void WriteOnboard()
	{
		Post(delegate
		{
			LastResult = DoWriteOnboard(factory: false);
			Log.Write("logi: write onboard: " + LastResult);
			RaiseChanged();
		});
	}

	public void RestoreFactory()
	{
		Post(delegate
		{
			LastResult = DoWriteOnboard(factory: true);
			Log.Write("logi: restore factory: " + LastResult);
			RaiseChanged();
		});
	}

	public void BackupNow()
	{
		Post(delegate
		{
			HidppDevice keyboard = Keyboard;
			Onboard onboard = ((keyboard != null && keyboard.Online) ? Onboard.Open(keyboard) : null);
			LastResult = ((onboard == null) ? "no keyboard with onboard memory" : (onboard.Backup(BackupPath()) + " -> " + BackupPath()));
			Log.Write("logi: backup: " + LastResult);
			RaiseChanged();
		});
	}

	private static string BackupPath()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar", $"logitech-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
	}

	// Запись профилей M1–M3 во встроенную память (factory — заводские профили вместо своих). Перед записью — копия всей
	// памяти; каждый сектор проверяется обратным чтением; затем клавиатура перечитывает профиль.
	private string DoWriteOnboard(bool factory)
	{
		if (!Controlling)
		{
			if (!_ghub)
			{
				return "Logitech control is off";
			}
			return "G HUB is running — close it first";
		}
		HidppDevice keyboard = Keyboard;
		if (keyboard == null || !keyboard.Online)
		{
			return "keyboard not connected";
		}
		Onboard onboard = Onboard.Open(keyboard);
		if (onboard == null)
		{
			return "this keyboard has no onboard memory";
		}
		if (onboard.ProfileFormat != 4 || onboard.SectorSize != 255)
		{
			return $"unsupported profile format {onboard.ProfileFormat} / sector {onboard.SectorSize}";
		}
		string text = BackupPath();
		string text2 = onboard.Backup(text);
		if (!text2.EndsWith(" 0 unreadable", StringComparison.Ordinal))
		{
			return "backup failed: " + text2;
		}
		int num = Math.Min(onboard.ProfileCount, Math.Min(3, factory ? onboard.RomProfileCount : Config.Profiles.Count));
		for (int i = 0; i < num; i++)
		{
			byte[] array = onboard.ReadSector(i + 1);
			byte[] array2 = onboard.ReadSector(0x101 + i);
			if (array2 == null)
			{
				return $"cannot read factory profile {i + 1}";
			}
			byte[] data;
			if (factory)
			{
				data = (byte[])array2.Clone();
				Onboard.SetCrc(data);
			}
			else
			{
				OnboardProfile onboardProfile = OnboardProfile.Parse((array != null && Onboard.CrcOk(array)) ? array : array2);
				data = OnboardBuilder.Build(onboardProfile, Config.Profiles[i], Config).Serialize();
			}
			if (!onboard.WriteSector(i + 1, data, out var error))
			{
				return $"profile {i + 1}: {error} (backup: {text})";
			}
		}
		bool value;
		bool flag = _hostMode.TryGetValue(keyboard, out value) && value;
		onboard.SetMode(1);
		onboard.SetCurrentProfile(Math.Clamp(Current, 0, num - 1) + 1);
		if (flag)
		{
			SetUp(keyboard);
		}
		return $"{num} profiles written and verified (backup: {text})";
	}

	// Окно настроек сохранило logitech.json — перечитать и применить.
	public void ReloadConfig()
	{
		Post(delegate
		{
			LogiConfig logiConfig = LogiConfig.Load();
			bool enabled = Config.Enabled;
			Config.Enabled = logiConfig.Enabled;
			Config.Active = logiConfig.Active;
			Config.IdleMinutes = logiConfig.IdleMinutes;
			Config.IdleBrightness = logiConfig.IdleBrightness;
			Config.Profiles = logiConfig.Profiles;
			if (enabled && !Config.Enabled)
			{
				ReleaseAll();
			}
			foreach (HidppDevice item in Devices.Where((HidppDevice d) => d.Online && d.IsKeyboard))
			{
				SetUp(item);
			}
			RaiseChanged();
		});
	}

	private unsafe static string ExeName(nint hwnd)
	{
		if (hwnd == 0)
		{
			return "";
		}
		GetWindowThreadProcessId(hwnd, out var pid);
		if (pid == 0)
		{
			return "";
		}
		nint num = OpenProcess(4096u, inherit: false, pid);
		if (num == 0)
		{
			return "";
		}
		try
		{
			Span<char> span = stackalloc char[520];
			int size = span.Length;
			fixed (char* buf = span)
			{
				if (!QueryFullProcessImageNameW(num, 0u, buf, ref size))
				{
					return "";
				}
			}
			return Path.GetFileName(new string(span.Slice(0, size)));
		}
		finally
		{
			CloseHandle(num);
		}
	}

	public void Dispose()
	{
		_reconcileTimer.Dispose();
		Post(ReleaseAll);
		Task queue;
		lock (_lock)
		{
			queue = _queue;
		}
		queue.Wait(3000);
		lock (_lock)
		{
			_disposed = true;
		}
		_batteryTimer.Dispose();
		_idleTimer.Dispose();
		lock (_lock)
		{
			foreach (HidppChannel channel in _channels)
			{
				channel.Dispose();
			}
			_channels.Clear();
		}
	}

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private unsafe static extern bool QueryFullProcessImageNameW(nint h, uint flags, char* buf, ref int size);
}
