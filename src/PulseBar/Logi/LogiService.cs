using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace PulseBar.Logi;

// Модуль Logitech (2.3, написан заново; согласован 31.10.2026, docs\PLAN-2.x.md).
// Пока PulseBar запущен, клавиатура всегда в режиме PulseBar: подсветку, M1–M3, MR и G1–G5 ведёт PulseBar, сама
// клавиатура между режимами не переключается. Всё, что меняет состояние, выполняется в одном потоке службы (Post);
// клавиатура сверяется с желаемым состоянием после каждого события (нажатие M, подключение, пробуждение, простой,
// смена программы, настройки, экран, заряд) и раз в 20 с. Выход и запуск G HUB — клавиатура возвращается своей памяти
// на профиле с тем же номером. Память клавиатуры обновляется сама, когда меняются профили.
internal sealed class LogiService : IDisposable
{
	private readonly LogiEnv _env;

	private const int LowBattery = 15;

	private const int LowBatteryCap = 30;

	private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();

	private readonly Thread _thread;

	private readonly object _lock = new object();

	private readonly bool _readOnly;

	private List<HidppLink> _links = new List<HidppLink>();

	private List<LogiDevice> _devices = new List<LogiDevice>();

	private readonly Dictionary<LogiDevice, (int Percent, bool Charging, bool Online)> _state = new Dictionary<LogiDevice, (int, bool, bool)>();

	private List<PointingDevice> _otherMice = new List<PointingDevice>();

	// Клавиатуры других фирм — только для списка «Определить» (у них PulseBar показывает настройки Windows).
	private List<PointingDevice> _otherKeyboards = new List<PointingDevice>();

	// Наушники Bluetooth (Headsets): заряд и связь — из свойств устройств Windows, раз в минуту.
	private List<Headsets.Headset> _headsets = new List<Headsets.Headset>();

	private string _headsetNames = "";

	private readonly Timer _headsetTimer;

	// Устройства Logitech, бывшие на связи (devices.json; Id → имя, вид, функции).
	private Dictionary<string, KnownDevice>? _known;

	// Сколько раз пересканировали: окно настроек ждёт смены числа после «Определить».
	private int _scans;

	private string _miceNames = "";

	private int _miceRetry;

	private LogiKeyboard? _kb;

	private int _kick;

	private int _retries;

	private int _auto = -1;

	private string _exe = "";

	private string? _overrideExe;

	private volatile bool _idle;

	// Простой дольше OffMinutes — подсветка погашена.
	private volatile bool _off;

	// Клавиатура погасла сама (яркость 0 %, которую не ставили ни PulseBar, ни кнопка): время события; новый ввод —
	// любая клавиша или мышь — зажигает её. -1 — не гасла.
	private long _darkSince = -1;

	// Последнее нажатие G, M, MR или кнопки яркости (Environment.TickCount64); 0 — не было, простой считает Windows.
	private long _lastKeyInput;

	private bool _screenOff;

	private bool _locked;

	private bool _suspended;

	private volatile bool _ghub;

	private bool _started;

	private bool _disposed;

	private long _onboardDue = -1;

	private long _rescanDue = -1;

	private readonly Timer _tick;

	private readonly Timer _slow;

	private readonly Timer _battery;

	// Кадры анимации (дыхание, цикл, волна) — 10 раз в секунду, пока анимация идёт.
	private const int AnimationMs = 100;

	private readonly Timer _anim;

	private bool _animating;

	private int _animPending;

	// Чужие записи подсветки за последнюю минуту (время) и ждёт ли перерисовка.
	private readonly List<long> _foreign = new List<long>();

	private bool _repaintDue;

	private long _foreignSeen = -1;

	private StreamWriter? _trace;

	public LogiConfig Config { get; private set; }

	// Профиль, который сейчас на клавиатуре (0–2): по программе в фокусе или выбранный вручную.
	public int Current => (_auto >= 0 && _overrideExe == null) ? _auto : Config.Active;

	public bool AutoSwitched => _auto >= 0 && _overrideExe == null;

	public bool GHubActive => _ghub;

	// Последний результат работы с памятью клавиатуры (для окна настроек).
	public string LastResult { get; private set; } = "";

	// Последний отчёт об устройствах («Сохранить отчёт об устройствах»), путь к файлу.
	public string LastReport { get; private set; } = "";

	public event Action? Changed;

	public LogiService(LogiConfig config, bool readOnly = false, LogiEnv? env = null)
	{
		Config = config;
		_readOnly = readOnly;
		_env = env ?? new LogiEnv();
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "Logitech"
		};
		_tick = new Timer(delegate
		{
			Tick();
		}, null, -1, -1);
		_slow = new Timer(delegate
		{
			Post(Check);
		}, null, -1, -1);
		_battery = new Timer(delegate
		{
			Post(ReadBatteries);
		}, null, -1, -1);
		_headsetTimer = new Timer(delegate
		{
			Post(RefreshHeadsets);
		}, null, -1, -1);
		_anim = new Timer(delegate
		{
			if (Interlocked.Exchange(ref _animPending, 1) == 0)
			{
				Post(delegate
				{
					Interlocked.Exchange(ref _animPending, 0);
					Animate();
				});
			}
		}, null, -1, -1);
	}

	// ---------- запуск, остановка, поток службы ----------

	public void Start()
	{
		if (_started)
		{
			return;
		}
		_started = true;
		_thread.Start();
		_ghub = _env.GHubRunning();
		if (!_readOnly && _env.SystemEvents)
		{
			SystemEvents.SessionSwitch += OnSessionSwitch;
			SystemEvents.PowerModeChanged += OnPowerMode;
		}
		Post(Rescan);
		if (_env.Timers)
		{
			_tick.Change(500, 500);
			_slow.Change(3000, 3000);
			_battery.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
			_headsetTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
		}
		// PULSEBAR_LOGI_TRACE=1 — все пакеты HID++ в logi-trace-*.txt (для разбора ошибок на настоящей клавиатуре).
		if (Environment.GetEnvironmentVariable("PULSEBAR_LOGI_TRACE") == "1")
		{
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar");
			Directory.CreateDirectory(dir);
			_trace = new StreamWriter(Path.Combine(dir, $"logi-trace-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), append: false, Encoding.UTF8)
			{
				AutoFlush = true
			};
		}
		// Первая сверка памяти клавиатуры с профилями — через 15 с после запуска (не мешать запуску Windows).
		_onboardDue = Environment.TickCount64 + _env.FirstSyncMs;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_tick.Dispose();
		_slow.Dispose();
		_battery.Dispose();
		_headsetTimer.Dispose();
		_anim.Dispose();
		if (!_readOnly && _env.SystemEvents)
		{
			SystemEvents.SessionSwitch -= OnSessionSwitch;
			SystemEvents.PowerModeChanged -= OnPowerMode;
		}
		if (_started)
		{
			Log.Write("logi: stopping");
			using ManualResetEventSlim done = new ManualResetEventSlim();
			Post(delegate
			{
				ReleaseKeyboard("exit");
				done.Set();
			});
			done.Wait(3000);
			_work.CompleteAdding();
			_thread.Join(1000);
		}
		foreach (HidppLink l in _links)
		{
			l.Dispose();
		}
		lock (_lock)
		{
			_trace?.Dispose();
			_trace = null;
		}
	}

	private void Post(Action a)
	{
		try
		{
			_work.Add(a);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void Run()
	{
		foreach (Action a in _work.GetConsumingEnumerable())
		{
			try
			{
				a();
			}
			catch (Exception ex)
			{
				Log.Write("logi: " + ex);
			}
		}
	}

	// Сверить клавиатуру с желаемым состоянием; несколько запросов подряд объединяются в одну сверку.
	private void Kick()
	{
		if (Interlocked.Exchange(ref _kick, 1) == 0)
		{
			Post(delegate
			{
				Interlocked.Exchange(ref _kick, 0);
				Reconcile();
			});
		}
	}

	private void RaiseChanged()
	{
		try
		{
			Changed?.Invoke();
		}
		catch (Exception ex)
		{
			Log.Write("logi: changed: " + ex.Message);
		}
	}

	private bool Controlling => Config.Enabled && Config.KeyboardOn && !_ghub && !_readOnly;

	// ---------- устройства ----------

	public static bool GHubRunning()
	{
		foreach (string name in new string[3] { "lghub_agent", "lghub", "lghub_system_tray" })
		{
			Process[] p = Process.GetProcessesByName(name);
			bool found = p.Length != 0;
			foreach (Process x in p)
			{
				x.Dispose();
			}
			if (found)
			{
				return true;
			}
		}
		return false;
	}

	// Подключили или отключили устройство USB (WM_DEVICECHANGE): пересканировать через секунду, если состав изменился.
	public void DevicesChanged()
	{
		Interlocked.Exchange(ref _rescanDue, Environment.TickCount64 + 1000);
	}

	private void Rescan()
	{
		Rescan(force: false);
	}

	// force — «Определить»: открыть всё заново, даже если состав не менялся. Клавиатура и мышь выключены — ничего не
	// открывать и не опрашивать.
	private void Rescan(bool force)
	{
		bool any = Config.KeyboardOn || Config.MouseOn;
		IReadOnlyCollection<string> present = any ? _env.PresentPaths() : Array.Empty<string>();
		bool same = !force && present.Count == _links.Count && present.All((string path) => _links.Any((HidppLink l) => l.Alive && l.Path == path));
		if (!same)
		{
			// Клавиатура остаётся в режиме PulseBar: новый объект настроит её заново при сверке.
			foreach (HidppLink l in _links)
			{
				l.Dispose();
			}
			_kb = null;
			List<HidppLink> links = any ? _env.OpenLinks() : new List<HidppLink>();
			List<LogiDevice> devices = new List<LogiDevice>();
			foreach (HidppLink link in links)
			{
				link.Notification += (byte dev, byte feat, byte fn, byte[] data) => OnNotification(link, dev, feat, fn, data);
				link.Foreign += (byte dev, byte feat, byte fn) => OnForeign(link, dev, feat, fn);
				if (_trace != null)
				{
					long t0 = Environment.TickCount64;
					link.Raw += delegate (bool inbound, byte[] r)
					{
						lock (_lock)
						{
							_trace?.WriteLine($"{Environment.TickCount64 - t0,8} {link.Pid:X4} {(inbound ? "IN " : "OUT")} {Convert.ToHexString(r)}");
						}
					};
				}
				link.Connection += (byte dev, bool linked) => Post(() => OnConnection(link, dev, linked));
				link.Died += () => DevicesChanged();
				if (link.IsReceiver)
				{
					LogiDevice.EnableReceiverNotifications(link);
					for (byte slot = 1; slot <= 6; slot++)
					{
						string name = LogiDevice.PairedName(link, slot);
						if (name.Length > 0)
						{
							devices.Add(LogiDevice.Open(link, slot, name) ?? LogiDevice.Offline(link, slot, name));
						}
					}
				}
				else
				{
					LogiDevice? d = LogiDevice.Open(link, 0xFF, "");
					if (d != null)
					{
						devices.Add(d);
					}
				}
			}
			lock (_lock)
			{
				_links = links;
				_devices = devices;
				_state.Clear();
				foreach (LogiDevice d in devices)
				{
					_state[d] = (-1, false, d.Features.Count > 0);
				}
			}
			foreach (LogiDevice d in devices)
			{
				Remember(d);
			}
			LogiDevice? kb = PickKeyboard(devices);
			_kb = (kb != null) ? new LogiKeyboard(kb) : null;
			Log.Write("logi: devices: " + (devices.Count == 0 ? (any ? "none" : "keyboard and mouse are off") : string.Join(", ", devices.Select((LogiDevice d) => $"{d.Name} ({(d.Features.Count > 0 ? "online" : "asleep")})"))));
		}
		List<PointingDevice> mice = Config.MouseOn ? _env.FindMice() : new List<PointingDevice>();
		List<PointingDevice> keyboards = Config.KeyboardOn ? _env.FindKeyboards() : new List<PointingDevice>();
		string names = string.Join(", ", mice.Select((PointingDevice m) => $"{m.Name} {m.Vid:X4}:{m.Pid:X4}{(m.RazerPath != null ? " (Razer)" : "")}"));
		if (names != _miceNames)
		{
			_miceNames = names;
			Log.Write("mice: " + ((names.Length > 0) ? names : "none"));
		}
		lock (_lock)
		{
			// Заряд, прочитанный раньше, остаётся: при новом чтении мышь могла спать.
			foreach (PointingDevice m in mice)
			{
				PointingDevice? old = _otherMice.FirstOrDefault((PointingDevice x) => x.Vid == m.Vid && x.Pid == m.Pid);
				if (old != null)
				{
					m.BatteryPercent = old.BatteryPercent;
					m.Charging = old.Charging;
					m.RazerTid = old.RazerTid;
					m.RazerStatus = old.RazerStatus;
				}
			}
			_otherMice = mice;
			_otherKeyboards = keyboards;
		}
		RefreshHeadsets();
		Interlocked.Increment(ref _scans);
		ReadBatteries();
		Kick();
	}

	// Клавиатура, которой управляет PulseBar: выбранная кнопкой «Определить», иначе первая клавиатура Logitech с профилями
	// в памяти. Выбранная спит или не умеет — не брать другую; выбрана клавиатура другой фирмы — никакой.
	private LogiDevice? PickKeyboard(List<LogiDevice> devices)
	{
		if (!Config.KeyboardOn)
		{
			return null;
		}
		string want = Config.KeyboardDevice;
		LogiDevice? chosen = devices.FirstOrDefault((LogiDevice d) => Id(d) == want);
		if (chosen != null)
		{
			return (chosen.IsKeyboard && chosen.Has(LogiDevice.OnboardProfiles)) ? chosen : null;
		}
		if (want.StartsWith("hid:", StringComparison.Ordinal))
		{
			return null;
		}
		return devices.FirstOrDefault((LogiDevice d) => d.IsKeyboard && d.Has(LogiDevice.OnboardProfiles));
	}

	internal static string Id(LogiDevice d)
	{
		return $"logi:{d.Link.Pid:X4}:{d.Index:X2}";
	}

	internal static string Id(PointingDevice d)
	{
		return $"hid:{d.Vid:X4}:{d.Pid:X4}";
	}

	// Функции HID++, по которым окно настроек решает, какие разделы показать.
	internal static string FeatureList(LogiDevice d)
	{
		List<string> list = new List<string> { "hidpp" };
		void Add(string name, params ushort[] ids)
		{
			if (ids.Any(d.Has))
			{
				list.Add(name);
			}
		}
		Add("lighting", LogiDevice.PerKeyLighting, 0x8080);
		Add("effects", LogiDevice.RgbEffects, 0x8070);
		Add("brightness", LogiDevice.Brightness);
		Add("gkeys", LogiDevice.GKeys);
		Add("mkeys", LogiDevice.MKeys);
		Add("macro", LogiDevice.MacroRecord);
		Add("memory", LogiDevice.OnboardProfiles);
		Add("battery", LogiDevice.BatteryStatus, LogiDevice.BatteryVoltage, LogiDevice.UnifiedBattery);
		Add("dpi", 0x2201);
		return string.Join(",", list);
	}

	private Dictionary<string, KnownDevice> Known
	{
		get
		{
			if (_known == null)
			{
				_known = new Dictionary<string, KnownDevice>();
				try
				{
					string path = Path.Combine(AppData, "devices.json");
					if (File.Exists(path))
					{
						_known = JsonSerializer.Deserialize(File.ReadAllText(path), LogiJson.Default.DictionaryStringKnownDevice) ?? _known;
					}
				}
				catch (Exception ex)
				{
					Log.Write("logi: devices.json: " + ex.Message);
				}
			}
			return _known;
		}
	}

	// Устройство на связи — запомнить имя, вид и функции (если изменились).
	private void Remember(LogiDevice d)
	{
		if (d.Features.Count == 0)
		{
			return;
		}
		KnownDevice k = new KnownDevice
		{
			Name = d.Name,
			Keyboard = d.IsKeyboard,
			Features = FeatureList(d)
		};
		lock (_lock)
		{
			if (Known.TryGetValue(Id(d), out KnownDevice? old) && old.Name == k.Name && old.Keyboard == k.Keyboard && old.Features == k.Features)
			{
				return;
			}
			Known[Id(d)] = k;
			try
			{
				Directory.CreateDirectory(AppData);
				File.WriteAllText(Path.Combine(AppData, "devices.json"), JsonSerializer.Serialize(Known, LogiJson.Default.DictionaryStringKnownDevice));
			}
			catch (Exception ex)
			{
				Log.Write("logi: devices.json: " + ex.Message);
			}
		}
	}

	// Клавиатура ли устройство Logitech: на связи — по его ответу, спящее — по devices.json.
	private bool IsKeyboardDevice(LogiDevice d)
	{
		if (d.Features.Count > 0)
		{
			return d.IsKeyboard;
		}
		lock (_lock)
		{
			return Known.TryGetValue(Id(d), out KnownDevice? k) && k.Keyboard;
		}
	}

	// Наушники Bluetooth: заряд и связь меняются без событий USB — перечитываются раз в минуту и при каждом поиске.
	private void RefreshHeadsets()
	{
		List<Headsets.Headset> headsets = new List<Headsets.Headset>();
		if (Config.HeadsetOn)
		{
			try
			{
				headsets = _env.FindHeadsets();
			}
			catch (Exception ex)
			{
				Log.Write("headsets: " + ex.Message);
			}
		}
		string names = string.Join(", ", headsets.Select((Headsets.Headset h) => $"{h.Name} ({(h.Online ? ((h.Battery >= 0) ? $"{h.Battery}%" : "no charge") : "off")})"));
		bool changed;
		lock (_lock)
		{
			changed = !headsets.Select((Headsets.Headset h) => h.Id + h.Battery + h.Online).SequenceEqual(_headsets.Select((Headsets.Headset h) => h.Id + h.Battery + h.Online));
			_headsets = headsets;
		}
		// В журнал — когда меняется состав или связь, не каждый процент.
		string online = string.Join(",", headsets.Select((Headsets.Headset h) => h.Id + h.Online));
		if (online != _headsetNames)
		{
			_headsetNames = online;
			Log.Write("headsets: " + ((names.Length > 0) ? names : "none"));
		}
		if (changed)
		{
			RaiseChanged();
		}
	}

	// Наушники для строки: выбранные кнопкой «Определить», иначе первые на связи с зарядом, иначе первые на связи,
	// иначе первые из известных Windows. null — нет или наушники выключены.
	private Headsets.Headset? PickHeadset()
	{
		if (!Config.HeadsetOn)
		{
			return null;
		}
		lock (_lock)
		{
			string want = Config.HeadsetDevice;
			return ((want.Length > 0) ? _headsets.FirstOrDefault((Headsets.Headset h) => h.Id == want) : null) ?? _headsets.FirstOrDefault((Headsets.Headset h) => h.Online && h.Battery >= 0) ?? _headsets.FirstOrDefault((Headsets.Headset h) => h.Online) ?? _headsets.FirstOrDefault();
		}
	}

	// Заряд — только когда наушники на связи (Windows помнит последний и после отключения).
	public (string Name, float Battery, bool Online) CurrentHeadset
	{
		get
		{
			Headsets.Headset? h = PickHeadset();
			if (h == null)
			{
				return ("", float.NaN, false);
			}
			return (h.Name, (h.Online && h.Battery >= 0) ? h.Battery : float.NaN, h.Online);
		}
	}

	public string HeadsetId => PickHeadset()?.Id ?? "";

	// «Определить»: найти все устройства заново.
	public void Detect()
	{
		Post(delegate
		{
			Log.Write("logi: detect");
			Rescan(force: true);
		});
	}

	public int Scans => Volatile.Read(ref _scans);

	// «Сохранить отчёт об устройствах»: всё, что известно о клавиатурах и мышах, — для поддержки новых моделей. Без путей
	// устройств и серийных номеров.
	public void WriteReport()
	{
		Post(delegate
		{
			try
			{
				string dir = Path.GetDirectoryName(Log.FilePath)!;
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, $"devices-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
				File.WriteAllText(path, Report(), new UTF8Encoding(false));
				LastReport = path;
				Log.Write("logi: device report -> " + path);
			}
			catch (Exception ex)
			{
				LastReport = "";
				Log.Write("logi: device report: " + ex.Message);
			}
			RaiseChanged();
		});
	}

	private string Report()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"PulseBar {Updater.Current} — devices, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		sb.AppendLine($"Keyboard {(Config.KeyboardOn ? "on" : "off")} ({(Config.KeyboardDevice.Length > 0 ? Config.KeyboardDevice : "auto")}), mouse {(Config.MouseOn ? "on" : "off")} ({(Config.MouseDevice.Length > 0 ? Config.MouseDevice : "auto")}), control {(Config.Enabled ? "on" : "off")}, G HUB {(_ghub ? "running" : "not running")}, mode {Mode}");
		sb.AppendLine($"Controlled keyboard: {KeyboardId} {KeyboardName}; mouse on the bar: {MouseId}");
		sb.AppendLine();
		sb.AppendLine("== Logitech (HID++)");
		List<HidppLink> links;
		List<LogiDevice> devices;
		lock (_lock)
		{
			links = _links;
			devices = _devices;
		}
		foreach (HidppLink link in links)
		{
			sb.AppendLine($"channel {link.Pid:X4} {(link.IsReceiver ? "receiver" : "device")} {(link.Alive ? "" : "(closed)")}");
		}
		foreach (LogiDevice d in devices)
		{
			_state.TryGetValue(d, out var s);
			sb.AppendLine();
			sb.AppendLine($"{Id(d)}  {d.Name}");
			sb.AppendLine($"  kind {d.Kind}, HID++ {d.Protocol}, {(d.Features.Count > 0 ? (s.Online ? "online" : "offline") : "asleep (features unknown)")}, battery {(s.Percent >= 0 ? $"{s.Percent}%" : "?")}{(s.Charging ? ", charging" : "")}");
			if (d.Features.Count > 0)
			{
				sb.AppendLine($"  sections: {FeatureList(d)}");
				foreach (var (id, f) in d.Features.OrderBy((KeyValuePair<ushort, (byte Index, byte Version)> x) => x.Value.Index))
				{
					sb.AppendLine($"  {f.Index,3}  {id:X4} v{f.Version}  {LogiDevice.FeatureName(id)}");
				}
			}
			else if (Known.TryGetValue(Id(d), out KnownDevice? k))
			{
				sb.AppendLine($"  last seen as {k.Name}, {(k.Keyboard ? "keyboard" : "not a keyboard")}, sections: {k.Features}");
			}
		}
		sb.AppendLine();
		sb.AppendLine("== Other keyboards and mice (HID)");
		try
		{
			foreach (IGrouping<(ushort Vid, ushort Pid), HidInfo> g in Hid.Enumerate((string p) => p.Contains("vid_", StringComparison.OrdinalIgnoreCase) && !p.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)).GroupBy((HidInfo h) => (h.Vid, h.Pid)))
			{
				if (!g.Any((HidInfo h) => h.UsagePage == 1 && (h.Usage == 2 || h.Usage == 6)))
				{
					continue;
				}
				string name = g.Select((HidInfo h) => h.Product).FirstOrDefault((string n) => n.Length > 0) ?? "";
				sb.AppendLine();
				sb.AppendLine($"hid:{g.Key.Vid:X4}:{g.Key.Pid:X4}  {name}  ({FoundDevice.BrandOf(g.Key.Vid)})");
				foreach (HidInfo h in g)
				{
					sb.AppendLine($"  usage {h.UsagePage:X4}/{h.Usage:X4}  in {h.InLen}  out {h.OutLen}  feature {h.FeatureLen}");
				}
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("  error: " + ex.Message);
		}
		List<PointingDevice> mice;
		lock (_lock)
		{
			mice = _otherMice;
		}
		foreach (PointingDevice m in mice.Where((PointingDevice x) => x.RazerPath != null))
		{
			sb.AppendLine($"razer {Id(m)} {m.Name}: battery {(m.BatteryPercent >= 0 ? $"{m.BatteryPercent}%" : "?")}, last status {OtherMice.Reason(m.RazerStatus)}");
		}
		sb.AppendLine();
		sb.AppendLine($"== Headsets (Bluetooth), {(Config.HeadsetOn ? "on" : "off")} ({(Config.HeadsetDevice.Length > 0 ? Config.HeadsetDevice : "auto")})");
		List<Headsets.Headset> headsets;
		lock (_lock)
		{
			headsets = _headsets;
		}
		foreach (Headsets.Headset h in headsets)
		{
			sb.AppendLine($"{h.Id}  {h.Name}  ({h.Brand})  {(h.Online ? "connected" : "not connected")}, battery {(h.Battery >= 0 ? $"{h.Battery}%" : "not reported")}");
		}
		return sb.ToString();
	}

	private void OnConnection(HidppLink link, byte dev, bool linked)
	{
		LogiDevice? d = _devices.FirstOrDefault((LogiDevice x) => x.Link == link && x.Index == dev);
		if (d == null)
		{
			if (linked)
			{
				DevicesChanged();
			}
			return;
		}
		// Спавшее при запуске устройство: функции читаются при первом подключении.
		if (linked && d.Features.Count == 0)
		{
			if (!d.Refresh())
			{
				return;
			}
			Remember(d);
		}
		lock (_lock)
		{
			_state[d] = (_state.TryGetValue(d, out var s) ? s.Percent : (-1), s.Charging, linked);
		}
		if (_kb == null && linked && PickKeyboard(_devices) == d)
		{
			_kb = new LogiKeyboard(d);
		}
		if (_kb != null && _kb.Device == d)
		{
			Log.Write($"logi: {d.Name} {(linked ? "connected" : "disconnected")}");
			_kb.Invalidate();
			if (linked)
			{
				Interlocked.Exchange(ref _darkSince, -1);
				_retries = 0;
				Kick();
			}
		}
		if (linked)
		{
			ReadBattery(d);
		}
		RaiseChanged();
	}

	// Уведомления клавиатуры (поток событий канала): нажатия G/MR выполняются сразу, M — через поток службы.
	private void OnNotification(HidppLink link, byte dev, byte feat, byte fn, byte[] data)
	{
		LogiKeyboard? kb = _kb;
		if (kb == null || kb.Device.Link != link || kb.Device.Index != dev)
		{
			return;
		}
		ushort id = kb.Device.FeatureAt(feat);
		switch (id)
		{
		case LogiDevice.GKeys:
		case LogiDevice.MKeys:
		case LogiDevice.MacroRecord:
		{
			Interlocked.Exchange(ref _lastKeyInput, Environment.TickCount64);
			if (!Controlling)
			{
				return;
			}
			List<string> pressed;
			lock (kb.Edges)
			{
				pressed = kb.Edges.Feed(id, fn, data);
			}
			foreach (string key in pressed)
			{
				OnPress(key);
			}
			if (_idle)
			{
				Kick();
			}
			break;
		}
		case LogiDevice.WirelessStatus:
			// Клавиатура проснулась или переподключилась и просит настроить её заново.
			Post(delegate
			{
				Log.Write($"logi: {kb.Device.Name} woke up");
				kb.Invalidate();
				_retries = 0;
				Kick();
			});
			break;
		case LogiDevice.OnboardProfiles:
			// В режиме PulseBar клавиатура сама профили не переключает; событие значит, что режим сбросился.
			Post(delegate
			{
				Log.Write($"logi: onboard event {Convert.ToHexString(data, 0, Math.Min(4, data.Length))}, checking mode");
				Check();
			});
			break;
		case LogiDevice.BatteryVoltage:
		case LogiDevice.UnifiedBattery:
		case LogiDevice.BatteryStatus:
			Post(delegate
			{
				ReadBattery(kb.Device);
				RaiseChanged();
			});
			break;
		case LogiDevice.Brightness:
			// Кнопка яркости на клавиатуре: fn0 [яркость BE16] (журнал G915: 25 → 12).
			if (fn == 0 && data.Length >= 2)
			{
				int value = (data[0] << 8) | data[1];
				Post(() => OnBrightnessKey(kb, value));
			}
			break;
		}
	}

	// Яркость, выбранная кнопкой на клавиатуре, становится яркостью текущего профиля. Не нажатия:
	// - отзвук своей записи: клавиатура сообщает о каждой записи яркости тем же значением (журнал 31.10.2026), даже
	//   когда ответ на саму запись опоздал;
	// - яркость, которая и должна быть сейчас (простой, экран выключен);
	// - 0 %, когда горело больше 12 %: кнопка гасит только с шага 12 % (100 → 50 → 25 → 12 → 0), значит, клавиатура
	//   погасла сама. Пользователь, 01.11.2026: после сна клавиатура зажигалась только кнопкой яркости — такой 0 %
	//   записывался в профиль. Теперь он в профиль не идёт, а подсветку зажигает любая клавиша.
	private void OnBrightnessKey(LogiKeyboard kb, int value)
	{
		if (!Controlling || !kb.SetUpDone || value < 0 || value > 100)
		{
			return;
		}
		int before = kb.LastBrightness;
		if (value == before || value == Desired().Brightness)
		{
			kb.AppliedBrightness = value;
			return;
		}
		if (value == 0 && !(before > 0 && before <= 12 && !_idle))
		{
			kb.AppliedBrightness = 0;
			Interlocked.Exchange(ref _darkSince, Environment.TickCount64);
			Log.Write($"logi: the keyboard turned its light off itself (was {before}%), any key turns it on");
			Kick();
			return;
		}
		Interlocked.Exchange(ref _lastKeyInput, Environment.TickCount64);
		Interlocked.Exchange(ref _darkSince, -1);
		kb.AppliedBrightness = value;
		Profile p = Config.Profiles[Current];
		if (p.Brightness != value)
		{
			Log.Write($"logi: brightness key: {value}% for profile {Current + 1} ({p.Name})");
			p.Brightness = value;
			LogiConfig c = LogiConfig.Load(_env.ConfigPath);
			c.Profiles[Current].Brightness = value;
			c.Save(_env.ConfigPath);
			_onboardDue = Environment.TickCount64 + 4000;
		}
		Kick();
		RaiseChanged();
	}

	// Ответ на чужую запись подсветки: служба Logitech LampArray перекрашивает клавиатуру в цвет «Динамического
	// освещения» Windows (журнал 31.10.2026: синий акцент #0078D4 после каждой смены эффекта). Через 0,5 с свой кадр
	// отправляется заново, но не чаще 5 раз в минуту: дальше не спорить, только сообщить в окне настроек.
	// Ответ на запись яркости (0x8040 fn2) без запроса — опоздавший ответ на свою же запись (журнал 01.11.2026, служба
	// LampArray выключена), не чужая программа; саму смену яркости разбирает OnBrightnessKey.
	private void OnForeign(HidppLink link, byte dev, byte feat, byte fn)
	{
		LogiKeyboard? kb = _kb;
		if (kb == null || kb.Device.Link != link || kb.Device.Index != dev)
		{
			return;
		}
		ushort id = kb.Device.FeatureAt(feat);
		bool lighting = (id == LogiDevice.PerKeyLighting && fn >= 1) || (id == LogiDevice.RgbEffects && (fn == 1 || fn == 5));
		if (!lighting)
		{
			return;
		}
		Post(delegate
		{
			if (!Controlling || !kb.SetUpDone)
			{
				return;
			}
			long now = Environment.TickCount64;
			bool first = _foreignSeen < 0 || now - _foreignSeen > 600000;
			_foreignSeen = now;
			if (first)
			{
				Log.Write($"logi: another program changes the lighting ({LogiDevice.FeatureName(id)} fn{fn}){(_env.DynamicLighting() ? ", Windows Dynamic Lighting is on" : "")}");
				RaiseChanged();
			}
			kb.InvalidateLighting();
			if (_repaintDue)
			{
				return;
			}
			_foreign.RemoveAll(x => now - x > 60000);
			if (_foreign.Count >= 5)
			{
				return;
			}
			_foreign.Add(now);
			_repaintDue = true;
			ThreadPool.QueueUserWorkItem(delegate
			{
				Thread.Sleep(500);
				Post(delegate
				{
					_repaintDue = false;
					Reconcile();
				});
			});
		});
	}

	// Другая программа меняла подсветку за последние 10 минут: "lamparray" — «Динамическое освещение» Windows через
	// службу Logitech LampArray, "other" — кто-то ещё, "" — нет.
	public string ForeignLighting
	{
		get
		{
			if (_foreignSeen < 0 || Environment.TickCount64 - _foreignSeen > 600000)
			{
				return "";
			}
			return _env.DynamicLighting() ? "lamparray" : "other";
		}
	}

	// «Динамическое освещение» Windows включено и служба Logitech LampArray запущена.
	public static bool DynamicLightingOn()
	{
		try
		{
			using RegistryKey? k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Lighting");
			if (k?.GetValue("AmbientLightingEnabled") is not int on || on == 0)
			{
				return false;
			}
		}
		catch (Exception)
		{
			return false;
		}
		foreach (string name in new string[3] { "logi_lamparray_service.AMD64", "logi_lamparray_service.ARM64", "logi_lamparray_service" })
		{
			Process[] p = Process.GetProcessesByName(name);
			bool found = p.Length != 0;
			foreach (Process x in p)
			{
				x.Dispose();
			}
			if (found)
			{
				return true;
			}
		}
		return false;
	}

	private void OnPress(string key)
	{
		if (key.Length == 2 && key[0] == 'M' && char.IsDigit(key[1]))
		{
			int n = key[1] - '1';
			Log.Write("logi: " + key);
			// M1–M3 — профиль, стоящий в слоте.
			Post(delegate
			{
				if (n < Config.Slots.Length)
				{
					Select(Config.Slots[n], manual: true);
				}
			});
			return;
		}
		Profile p = Config.Profiles[Current];
		KeyAction a = p.Key(key);
		Log.Write($"logi: {key} ({a.Type})");
		_env.RunAction(a);
	}

	// ---------- профили ----------

	// Выбор профиля вручную (M1–M3 или меню строки): действует, пока не сменится активная программа.
	public void SelectProfile(int n)
	{
		Post(() => Select(n, manual: true));
	}

	private void Select(int n, bool manual)
	{
		if (n < 0 || n >= Config.Profiles.Count)
		{
			return;
		}
		if (manual)
		{
			_overrideExe = (_auto >= 0) ? _exe : null;
			if (Config.Active != n)
			{
				Config.Active = n;
				SaveActive(n);
			}
		}
		Kick();
		RaiseChanged();
	}

	// В файле меняется только Active: остальное в logitech.json правит окно настроек.
	private void SaveActive(int n)
	{
		LogiConfig c = LogiConfig.Load(_env.ConfigPath);
		c.Active = n;
		c.Save(_env.ConfigPath);
	}

	public void OnForeground(nint hwnd)
	{
		Foreground(ExeName(hwnd));
	}

	internal void Foreground(string exe)
	{
		if (exe.Length == 0 || exe.Equals("PulseBar.exe", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		Post(delegate
		{
			if (exe == _exe)
			{
				return;
			}
			_exe = exe;
			_overrideExe = null;
			int auto = Config.Profiles.FindIndex((Profile p) => p.MatchesApp(exe));
			if (auto != _auto)
			{
				_auto = auto;
				if (auto >= 0)
				{
					Log.Write($"logi: {exe} -> profile {auto + 1}");
				}
			}
			Kick();
			RaiseChanged();
		});
	}

	// Окно настроек сохранило logitech.json. Выбранный вручную профиль строка помнит сама.
	public void ReloadConfig()
	{
		Post(delegate
		{
			bool was = Controlling;
			int active = Config.Active;
			LogiConfig old = Config;
			Config = LogiConfig.Load(_env.ConfigPath);
			Config.Active = active;
			bool devices = old.KeyboardOn != Config.KeyboardOn || old.MouseOn != Config.MouseOn || old.KeyboardDevice != Config.KeyboardDevice || old.MouseDevice != Config.MouseDevice;
			// Наушники — без пересканирования HID++: клавиатура остаётся как есть.
			if (!devices && (old.HeadsetOn != Config.HeadsetOn || old.HeadsetDevice != Config.HeadsetDevice))
			{
				Log.Write($"logi: headsets {(Config.HeadsetOn ? "on" : "off")} {Config.HeadsetDevice}");
				RefreshHeadsets();
			}
			if (was && (!Controlling || devices))
			{
				ReleaseKeyboard(Config.KeyboardOn ? "disabled" : "keyboard off");
			}
			if (devices)
			{
				Log.Write($"logi: keyboard {(Config.KeyboardOn ? "on" : "off")} {Config.KeyboardDevice}, mouse {(Config.MouseOn ? "on" : "off")} {Config.MouseDevice}");
				Rescan(force: true);
			}
			if (_exe.Length > 0)
			{
				_auto = Config.Profiles.FindIndex((Profile p) => p.MatchesApp(_exe));
			}
			// Память клавиатуры — не на каждое движение кисти: через 4 с после последнего изменения.
			_onboardDue = Environment.TickCount64 + 4000;
			Kick();
			RaiseChanged();
		});
	}

	// ---------- сверка ----------

	private KeyFrame Desired()
	{
		Profile p = Config.Profiles[Current];
		int b = p.Brightness;
		if (_screenOff || _locked || _suspended)
		{
			b = 0;
		}
		else if (_off || Interlocked.Read(ref _darkSince) >= 0)
		{
			b = 0;
		}
		else if (_idle)
		{
			b = Math.Min(b, Config.IdleBrightness);
		}
		// G915 при 10 % и ниже не светится.
		if (b > 0 && b < LogiConfig.MinBrightness)
		{
			b = LogiConfig.MinBrightness;
		}
		(int pct, bool charging) = KeyboardBatteryState();
		bool low = pct >= 0 && pct < LowBattery && !charging;
		if (low)
		{
			b = Math.Min(b, LowBatteryCap);
		}
		KeyFrame f = KeyFrame.From(p, b, MLeds());
		// Анимация — только когда её видно и есть заряд: при простое, низком заряде и погашенной подсветке — статичный цвет.
		return f with
		{
			Animate = f.HasAnimation && b > 0 && !_idle && !low
		};
	}

	// Лампочки M: слоты, в которых стоит текущий профиль (профиль только для программ — ни одной).
	private byte MLeds()
	{
		int current = Current;
		byte bits = 0;
		for (int i = 0; i < Config.Slots.Length; i++)
		{
			if (Config.Slots[i] == current)
			{
				bits |= (byte)(1 << i);
			}
		}
		return bits;
	}

	// Номер профиля встроенной памяти (1–3) для возврата клавиатуре: слот текущего профиля, иначе последний слот, который
	// был включён.
	private int OnboardSlot()
	{
		int slot = Config.SlotOf(Current);
		if (slot >= 0)
		{
			_lastSlot = slot;
		}
		return _lastSlot + 1;
	}

	private int _lastSlot;

	private void Reconcile()
	{
		LogiKeyboard? kb = _kb;
		if (kb == null || !Controlling || !IsOnline(kb.Device))
		{
			return;
		}
		string error;
		if (!kb.SetUpDone)
		{
			if (!kb.SetUp(out error))
			{
				Retry("set up: " + error);
				return;
			}
			Log.Write($"logi: {kb.Device.Name}: PulseBar mode, profile {Current + 1} ({Config.Profiles[Current].Name})");
		}
		KeyFrame frame = Desired();
		if (!kb.Apply(frame, _env.Now(), out error))
		{
			kb.Invalidate();
			Retry(error);
			return;
		}
		_retries = 0;
		SetAnimating(frame.Animate);
	}

	// Кадр анимации: дорисовать изменившиеся клавиши. Анимация кончилась (простой, экран, другой профиль) — таймер стоп.
	private void Animate()
	{
		LogiKeyboard? kb = _kb;
		if (kb == null || !Controlling || !kb.SetUpDone || !IsOnline(kb.Device))
		{
			SetAnimating(on: false);
			return;
		}
		KeyFrame frame = Desired();
		if (!frame.Animate)
		{
			SetAnimating(on: false);
			Kick();
			return;
		}
		if (!kb.Apply(frame, _env.Now(), out string error))
		{
			SetAnimating(on: false);
			kb.Invalidate();
			Retry(error);
		}
	}

	private void SetAnimating(bool on)
	{
		if (on == _animating)
		{
			return;
		}
		_animating = on;
		if (_env.Timers)
		{
			_anim.Change(on ? AnimationMs : (-1), on ? AnimationMs : (-1));
		}
	}

	// Для тестов: один кадр анимации в потоке службы.
	internal void AnimateNow()
	{
		Post(Animate);
	}

	// Ошибка связи: повторить через 1, 2, 4 с; дальше — только по событию или проверке раз в 20 с.
	private void Retry(string error)
	{
		Log.Write($"logi: {error} (attempt {_retries + 1})");
		if (_retries < 3)
		{
			int delay = 1000 << _retries;
			_retries++;
			ThreadPool.QueueUserWorkItem(delegate
			{
				Thread.Sleep(delay);
				Kick();
			});
		}
	}

	// Каждые 3 с: G HUB; каждые 20 с — режим клавиатуры (не сбросился ли); отложенные пересканирование и запись памяти.
	private int _checks;

	private void Check()
	{
		bool ghub = _env.GHubRunning();
		if (ghub != _ghub)
		{
			_ghub = ghub;
			Log.Write(ghub ? "logi: G HUB started, releasing the keyboard" : "logi: G HUB closed, taking the keyboard");
			if (ghub)
			{
				ReleaseKeyboard("G HUB");
			}
			else
			{
				_kb?.Invalidate();
				_retries = 0;
				Kick();
			}
			RaiseChanged();
		}
		long now = Environment.TickCount64;
		long rescan = Interlocked.Read(ref _rescanDue);
		if (rescan >= 0 && now >= rescan)
		{
			Interlocked.Exchange(ref _rescanDue, -1);
			Rescan();
		}
		if (_onboardDue >= 0 && now >= _onboardDue)
		{
			_onboardDue = -1;
			SyncOnboard();
		}
		if (++_checks % 7 == 0 && _kb != null && Controlling && _kb.SetUpDone)
		{
			int mode = _kb.ReadMode();
			if (mode == OnboardMemory.ModeOnboard)
			{
				Log.Write("logi: keyboard left PulseBar mode, setting it up again");
				_kb.Invalidate();
				Kick();
			}
		}
	}

	private void ReleaseKeyboard(string why)
	{
		LogiKeyboard? kb = _kb;
		if (kb == null || !kb.SetUpDone)
		{
			return;
		}
		SetAnimating(on: false);
		int slot = OnboardSlot();
		kb.Release(slot);
		Log.Write($"logi: {kb.Device.Name}: keyboard memory, profile {slot} ({why})");
	}

	// ---------- простой, экран, сон ----------

	private void Tick()
	{
		if (_readOnly)
		{
			return;
		}
		long sinceWin = _env.IdleMs();
		if (sinceWin < 0)
		{
			return;
		}
		long now = Environment.TickCount64;
		long sinceKeys = now - Interlocked.Read(ref _lastKeyInput);
		long since = Math.Min(sinceWin, sinceKeys);
		bool idle = Config.IdleSeconds > 0 && since >= Config.IdleSeconds * 1000L;
		bool off = Config.OffMinutes > 0 && since >= Config.OffMinutes * 60000L;
		// Ввод после того, как клавиатура погасла сама или по простою: зажечь и отправить кадр заново (прошивка могла
		// стереть его, пока подсветка была погашена).
		long dark = Interlocked.Read(ref _darkSince);
		bool wake = dark >= 0 && now - since > dark;
		if (wake)
		{
			Interlocked.CompareExchange(ref _darkSince, -1, dark);
		}
		if (idle != _idle || off != _off || wake)
		{
			bool relight = wake || (_off && !off);
			_idle = idle;
			_off = off;
			if (relight)
			{
				// Сверка — после «зажечь заново», в потоке службы: уже ждущая сверка могла бы пройти раньше.
				Post(delegate
				{
					_kb?.Relight();
					Kick();
				});
			}
			else
			{
				Kick();
			}
		}
	}

	// Время с последнего ввода в Windows (клавиатура, мышь), мс; -1 — неизвестно.
	internal static long WindowsIdleMs()
	{
		LASTINPUTINFO li = new LASTINPUTINFO
		{
			cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
		};
		return GetLastInputInfo(ref li) ? (uint)Environment.TickCount - li.dwTime : -1;
	}

	private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
	{
		bool? locked = e.Reason switch
		{
			SessionSwitchReason.SessionLock => true,
			SessionSwitchReason.SessionUnlock => false,
			_ => null
		};
		if (locked.HasValue)
		{
			Post(delegate
			{
				_locked = locked.Value;
				Kick();
			});
		}
	}

	private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
	{
		if (e.Mode == PowerModes.Suspend)
		{
			// Погасить до сна и дождаться: после сна приёмник может пропасть до того, как команда уйдёт.
			using ManualResetEventSlim done = new ManualResetEventSlim();
			Post(delegate
			{
				_suspended = true;
				Reconcile();
				done.Set();
			});
			done.Wait(2000);
		}
		else if (e.Mode == PowerModes.Resume)
		{
			Post(delegate
			{
				_suspended = false;
				_kb?.Invalidate();
				_retries = 0;
				Kick();
			});
			DevicesChanged();
		}
	}

	// Экран выключился / включился (WM_POWERBROADCAST, GUID_CONSOLE_DISPLAY_STATE — передаёт строка).
	public void DisplayChanged(bool on)
	{
		Post(delegate
		{
			if (_screenOff != !on)
			{
				_screenOff = !on;
				Kick();
			}
		});
	}

	// ---------- заряд и состояние для строки и окна настроек ----------

	private bool IsOnline(LogiDevice d)
	{
		lock (_lock)
		{
			return _state.TryGetValue(d, out var s) && s.Online;
		}
	}

	private void ReadBatteries()
	{
		foreach (LogiDevice d in _devices)
		{
			if (IsOnline(d) && (IsKeyboardDevice(d) ? Config.KeyboardOn : Config.MouseOn))
			{
				ReadBattery(d);
			}
		}
		List<PointingDevice> mice;
		lock (_lock)
		{
			mice = _otherMice;
		}
		foreach (PointingDevice m in mice)
		{
			OtherMice.ReadBattery(m);
		}
		// Заряд мыши ещё не прочитан ни разу — снова через 30 с, а не через 5 минут.
		if (mice.Any((PointingDevice m) => m.RazerPath != null && m.BatteryPercent < 0) && Interlocked.Exchange(ref _miceRetry, 1) == 0)
		{
			ThreadPool.QueueUserWorkItem(delegate
			{
				Thread.Sleep(30000);
				Interlocked.Exchange(ref _miceRetry, 0);
				Post(ReadBatteries);
			});
		}
		Kick();
		RaiseChanged();
	}

	private void ReadBattery(LogiDevice d)
	{
		int pct = d.ReadBattery(out bool charging);
		lock (_lock)
		{
			bool online = !_state.TryGetValue(d, out var s) || s.Online;
			_state[d] = (pct, charging, online);
		}
	}

	// Клавиатура Logitech для строки и окна: та, которой управляет PulseBar, иначе выбранная или первая (только заряд).
	private LogiDevice? KeyboardDevice()
	{
		LogiKeyboard? kb = _kb;
		if (kb != null)
		{
			return kb.Device;
		}
		if (!Config.KeyboardOn)
		{
			return null;
		}
		List<LogiDevice> devices;
		lock (_lock)
		{
			devices = _devices;
		}
		string want = Config.KeyboardDevice;
		if (want.Length > 0)
		{
			return devices.FirstOrDefault((LogiDevice d) => Id(d) == want);
		}
		return devices.FirstOrDefault(IsKeyboardDevice);
	}

	private (int Percent, bool Charging) KeyboardBatteryState()
	{
		LogiDevice? kb = KeyboardDevice();
		lock (_lock)
		{
			return (kb != null && _state.TryGetValue(kb, out var s) && s.Online) ? (s.Percent, s.Charging) : (-1, false);
		}
	}

	public string KeyboardName => KeyboardDevice()?.Name ?? "";

	public bool KeyboardOnline => KeyboardDevice() is LogiDevice d && IsOnline(d);

	// Выбранные (или первые подходящие) клавиатура и мышь — Id из Found; "" — нет.
	public string KeyboardId
	{
		get
		{
			if (!Config.KeyboardOn)
			{
				return "";
			}
			LogiDevice? d = KeyboardDevice();
			if (d != null)
			{
				return Id(d);
			}
			lock (_lock)
			{
				PointingDevice? k = _otherKeyboards.FirstOrDefault((PointingDevice x) => Id(x) == Config.KeyboardDevice) ?? _otherKeyboards.FirstOrDefault();
				return (k != null) ? Id(k) : "";
			}
		}
	}

	public string MouseId => CurrentMouseId();

	// Все найденные клавиатуры и мыши (включённых видов) — для списка «Определить».
	public IReadOnlyList<FoundDevice> Found
	{
		get
		{
			List<FoundDevice> list = new List<FoundDevice>();
			lock (_lock)
			{
				foreach (LogiDevice d in _devices)
				{
					Known.TryGetValue(Id(d), out KnownDevice? k);
					bool live = d.Features.Count > 0;
					bool keyboard = live ? d.IsKeyboard : (k?.Keyboard ?? false);
					string kind = keyboard ? "keyboard" : ((live && d.Kind == LogiDevice.DeviceKind.Headset) ? "headset" : "mouse");
					if (!(kind switch
					{
						"keyboard" => Config.KeyboardOn,
						"mouse" => Config.MouseOn,
						_ => Config.HeadsetOn
					}))
					{
						continue;
					}
					string features = live ? FeatureList(d) : (k?.Features ?? "");
					string[] f = features.Split(',');
					string support = (features.Length == 0) ? "" : ((keyboard && f.Contains("memory")) ? "full" : (f.Contains("battery") ? "battery" : "windows"));
					_state.TryGetValue(d, out var s);
					bool online = live && s.Online;
					list.Add(new FoundDevice(Id(d), kind, live ? d.Name : (k?.Name ?? d.Name), "Logitech", online, online ? s.Percent : -1, online && s.Charging, support, features));
				}
				foreach (PointingDevice m in _otherMice)
				{
					bool razer = m.RazerPath != null;
					list.Add(new FoundDevice(Id(m), "mouse", m.Name, FoundDevice.BrandOf(m.Vid), Online: true, m.BatteryPercent, m.Charging, razer ? "battery" : "windows", razer ? "battery" : ""));
				}
				foreach (PointingDevice m in _otherKeyboards)
				{
					list.Add(new FoundDevice(Id(m), "keyboard", m.Name, FoundDevice.BrandOf(m.Vid), Online: true, -1, Charging: false, "windows", ""));
				}
				foreach (Headsets.Headset h in _headsets)
				{
					list.Add(new FoundDevice(h.Id, "headset", h.Name, h.Brand, h.Online, h.Battery, Charging: false, (h.Battery >= 0) ? "battery" : "nobattery", (h.Battery >= 0) ? "battery" : ""));
				}
			}
			return list;
		}
	}

	public float KeyboardBattery
	{
		get
		{
			int pct = KeyboardBatteryState().Percent;
			return (pct < 0) ? float.NaN : pct;
		}
	}

	public bool KeyboardCharging => KeyboardBatteryState().Charging;

	// Состояние для окна настроек: off, ghub, none, offline, host, starting.
	public string Mode
	{
		get
		{
			if (!Config.KeyboardOn)
			{
				return "disabled";
			}
			if (!Config.Enabled)
			{
				return "off";
			}
			if (_ghub)
			{
				return "ghub";
			}
			LogiKeyboard? kb = _kb;
			if (kb == null)
			{
				return "none";
			}
			if (!IsOnline(kb.Device))
			{
				return "offline";
			}
			return kb.SetUpDone ? "host" : "starting";
		}
	}

	// Эффекты, которые есть у зоны (0 — клавиши, 1 — логотип), по именам из настроек.
	public IReadOnlyList<string> Effects(int zone)
	{
		Dictionary<int, int>? map = _kb?.EffectsOf(logo: zone == 1);
		if (map == null)
		{
			return Array.Empty<string>();
		}
		return LogiKeyboard.EffectNames.Where(e => map.ContainsValue(e.Id)).Select(e => e.Name).ToArray();
	}

	public IReadOnlyList<byte> KeyIds => _kb?.KeyIds ?? Array.Empty<byte>();

	// Мышь для строки: Logitech на связи с известным зарядом, иначе другая мышь с зарядом (Razer).
	// Выбрана кнопкой «Определить» — она, даже без заряда.
	public (string Name, float Battery, bool Charging, bool Online) CurrentMouse
	{
		get
		{
			string id = CurrentMouseId();
			if (id.Length == 0)
			{
				return ("", float.NaN, false, false);
			}
			lock (_lock)
			{
				LogiDevice? d = _devices.FirstOrDefault((LogiDevice x) => Id(x) == id);
				if (d != null)
				{
					bool on = _state.TryGetValue(d, out var s) && s.Online && d.Features.Count > 0;
					return (d.Name, (on && s.Percent >= 0) ? s.Percent : float.NaN, on && s.Charging, on);
				}
				PointingDevice? m = _otherMice.FirstOrDefault((PointingDevice x) => Id(x) == id);
				if (m != null)
				{
					return (m.Name, (m.BatteryPercent >= 0) ? m.BatteryPercent : float.NaN, m.Charging, true);
				}
			}
			return ("", float.NaN, false, false);
		}
	}

	private string CurrentMouseId()
	{
		if (!Config.MouseOn)
		{
			return "";
		}
		List<LogiDevice> devices;
		lock (_lock)
		{
			devices = _devices;
		}
		LogiDevice? kb = _kb?.Device;
		List<LogiDevice> logi = devices.Where((LogiDevice x) => x != kb && !IsKeyboardDevice(x) && x.Kind != LogiDevice.DeviceKind.Headset).ToList();
		lock (_lock)
		{
			string want = Config.MouseDevice;
			if (want.Length > 0 && (logi.Any((LogiDevice x) => Id(x) == want) || _otherMice.Any((PointingDevice x) => Id(x) == want)))
			{
				return want;
			}
			foreach (LogiDevice d in logi)
			{
				if (_state.TryGetValue(d, out var s) && s.Online && s.Percent >= 0)
				{
					return Id(d);
				}
			}
			PointingDevice? m = _otherMice.FirstOrDefault((PointingDevice x) => x.BatteryPercent >= 0) ?? _otherMice.FirstOrDefault();
			if (m != null)
			{
				return Id(m);
			}
			return (logi.Count > 0) ? Id(logi[0]) : "";
		}
	}

	// Все устройства с зарядом — для подсказки: «G915 64%, Naga V3 Pro 80%⚡».
	public string ShortStatus()
	{
		List<string> list = new List<string>();
		lock (_lock)
		{
			foreach (LogiDevice d in _devices)
			{
				if (_state.TryGetValue(d, out var s) && s.Online && s.Percent >= 0 && (d.IsKeyboard ? Config.KeyboardOn : Config.MouseOn))
				{
					list.Add(Short(d.Name) + " " + $"{s.Percent}%{(s.Charging ? "⚡" : "")}");
				}
			}
			foreach (PointingDevice m in _otherMice)
			{
				if (m.BatteryPercent >= 0)
				{
					list.Add(Short(m.Name) + " " + $"{m.BatteryPercent}%{(m.Charging ? "⚡" : "")}");
				}
			}
			foreach (Headsets.Headset h in _headsets)
			{
				if (h.Online && h.Battery >= 0)
				{
					list.Add(Short(h.Name) + " " + $"{h.Battery}%");
				}
			}
		}
		return string.Join(", ", list);
		static string Short(string name)
		{
			if (name.StartsWith("Razer ", StringComparison.Ordinal))
			{
				return name.Substring(6);
			}
			return name.Split(' ')[0];
		}
	}

	// ---------- память клавиатуры ----------

	private string AppData => _env.AppData;

	private OnboardMemory? OpenMemory(out string error)
	{
		error = "";
		LogiKeyboard? kb = _kb;
		if (kb == null || !IsOnline(kb.Device))
		{
			error = L.T("The keyboard is asleep or off");
			return null;
		}
		if (_ghub || !Config.Enabled)
		{
			error = L.T("Logitech control is off");
			return null;
		}
		OnboardMemory? m = OnboardMemory.Open(kb.Device);
		if (m == null)
		{
			error = L.T("This keyboard has no supported profile memory");
		}
		return m;
	}

	// Перед первой записью — полная копия памяти (если копии этой клавиатуры ещё нет).
	private bool EnsureBackup(OnboardMemory m, out string error)
	{
		error = "";
		if (Directory.Exists(AppData) && Directory.EnumerateFiles(AppData, "logitech-backup-*.json").Any())
		{
			return true;
		}
		try
		{
			string path = Path.Combine(AppData, $"logitech-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
			Log.Write($"logi: backup {m.Backup(path)} -> {path}");
			return true;
		}
		catch (Exception ex)
		{
			error = L.T("Backup failed, nothing was written") + ": " + ex.Message;
			return false;
		}
	}

	// Профили в памяти клавиатуры = профили PulseBar (то, что клавиатура выполнит сама). Пишутся только изменённые секторы.
	private void SyncOnboard()
	{
		if (!Controlling || _kb == null || !IsOnline(_kb.Device))
		{
			return;
		}
		OnboardMemory? m = OpenMemory(out string error);
		if (m == null)
		{
			return;
		}
		byte[]? dir = m.ReadSector(0);
		if (dir == null)
		{
			Log.Write("logi: onboard: directory unreadable");
			return;
		}
		int count = Math.Min(Math.Min(LogiConfig.SlotCount, m.ProfileCount), m.SectorCount - 1);
		List<(int Sector, byte[] Data)> writes = new List<(int, byte[])>();
		for (int i = 0; i < count; i++)
		{
			byte[]? user = m.ReadSector(i + 1);
			if (user == null)
			{
				Log.Write($"logi: onboard: sector {i + 1} unreadable");
				return;
			}
			byte[]? rom = OnboardMemory.CrcOk(user) ? null : m.ReadSector(OnboardMemory.RomBase + 1 + Math.Min(i, m.RomProfileCount - 1));
			byte[] built = OnboardPlan.Build(OnboardPlan.Basis(user, rom), Config.Profiles[Config.Slots[i]], Config).Serialize();
			if (!built.AsSpan().SequenceEqual(user))
			{
				writes.Add((i + 1, built));
			}
		}
		List<(int Sector, bool Enabled)> list = OnboardMemory.ParseDirectory(dir);
		bool dirOk = OnboardMemory.CrcOk(dir) && Enumerable.Range(1, count).All(s => list.Contains((s, true)));
		if (writes.Count == 0 && dirOk)
		{
			return;
		}
		if (!EnsureBackup(m, out error))
		{
			LastResult = error;
			RaiseChanged();
			return;
		}
		foreach (var (sector, data) in writes)
		{
			if (!m.WriteSector(sector, data, out error))
			{
				LastResult = L.T("Keyboard memory was not updated") + $": {error}";
				Log.Write($"logi: onboard sector {sector}: {error}");
				_onboardDue = Environment.TickCount64 + 60000;
				RaiseChanged();
				return;
			}
		}
		if (!dirOk && !m.WriteSector(0, m.BuildDirectory(count), out error))
		{
			LastResult = L.T("Keyboard memory was not updated") + $": {error}";
			RaiseChanged();
			return;
		}
		LastResult = L.F("Keyboard memory updated at {0}", DateTime.Now.ToString("HH:mm"));
		Log.Write($"logi: onboard updated: sectors {string.Join(",", writes.Select(w => w.Sector))}{(dirOk ? "" : " + directory")}");
		// Запись не должна была трогать режим PulseBar — но проверить дешевле, чем гадать. Клавиатура после записи
		// ещё перечитывает профиль и гасит лампочки M (пользователь, 01.11.2026: после ползунка яркости пропадала M1) —
		// через 1,5 с всё отправляется ещё раз.
		_kb?.Invalidate();
		Kick();
		ThreadPool.QueueUserWorkItem(delegate
		{
			Thread.Sleep(1500);
			Post(delegate
			{
				_kb?.Invalidate();
				Kick();
			});
		});
		RaiseChanged();
	}

	// «Вернуть заводские»: три профиля памяти — заводские (до следующего изменения профилей в PulseBar).
	public void RestoreFactory()
	{
		Post(delegate
		{
			OnboardMemory? m = OpenMemory(out string error);
			if (m == null || !EnsureBackup(m, out error))
			{
				LastResult = error;
				RaiseChanged();
				return;
			}
			int count = Math.Min(m.ProfileCount, m.RomProfileCount);
			for (int i = 1; i <= count; i++)
			{
				byte[]? rom = m.ReadSector(OnboardMemory.RomBase + i);
				if (rom == null || !m.WriteSector(i, new OnboardSector(rom).Serialize(), out error))
				{
					LastResult = L.T("Keyboard memory was not updated") + $": {error}";
					RaiseChanged();
					return;
				}
			}
			m.WriteSector(0, m.BuildDirectory(count), out _);
			LastResult = L.T("Factory profiles restored");
			Log.Write("logi: factory profiles restored");
			_kb?.Invalidate();
			Kick();
			RaiseChanged();
		});
	}

	public void BackupNow()
	{
		Post(delegate
		{
			OnboardMemory? m = OpenMemory(out string error);
			if (m == null)
			{
				LastResult = error;
				RaiseChanged();
				return;
			}
			try
			{
				string path = Path.Combine(AppData, $"logitech-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
				m.Backup(path);
				LastResult = L.F("Backup saved: {0}", path);
			}
			catch (Exception ex)
			{
				LastResult = L.T("Backup failed") + ": " + ex.Message;
			}
			RaiseChanged();
		});
	}

	// ---------- для тестов: выполнить сразу то, что иначе делает таймер ----------

	internal void Flush()
	{
		using ManualResetEventSlim done = new ManualResetEventSlim();
		Post(done.Set);
		done.Wait(10000);
	}

	internal void CheckNow()
	{
		Post(Check);
		Flush();
	}

	internal void TickNow()
	{
		Tick();
		Flush();
	}

	internal void SyncOnboardNow()
	{
		Post(SyncOnboard);
		Flush();
	}

	// ---------- Win32 ----------

	private struct LASTINPUTINFO
	{
		public uint cbSize;

		public uint dwTime;
	}

	[DllImport("user32.dll")]
	private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageNameW(nint h, uint flags, StringBuilder name, ref uint size);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	private static string ExeName(nint hwnd)
	{
		if (hwnd == 0)
		{
			return "";
		}
		GetWindowThreadProcessId(hwnd, out uint pid);
		if (pid == 0)
		{
			return "";
		}
		nint h = OpenProcess(0x1000, inherit: false, pid);
		if (h == 0)
		{
			return "";
		}
		try
		{
			StringBuilder sb = new StringBuilder(1024);
			uint size = (uint)sb.Capacity;
			return QueryFullProcessImageNameW(h, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : "";
		}
		finally
		{
			CloseHandle(h);
		}
	}
}

// Окружение службы: железо и система. В тестах — модель клавиатуры, свои папки, без таймеров и системных событий.
internal sealed class LogiEnv
{
	public Func<List<HidppLink>> OpenLinks = () => HidppLink.OpenAll();

	public Func<IReadOnlyCollection<string>> PresentPaths = () => Hid.Enumerate(0x046D).Where((HidInfo h) => h.UsagePage == 0xFF00 && h.OutLen == 20).Select((HidInfo h) => h.Path).ToList();

	public Func<bool> GHubRunning = LogiService.GHubRunning;

	// Время с последнего ввода в Windows, мс.
	public Func<long> IdleMs = LogiService.WindowsIdleMs;

	public Func<List<PointingDevice>> FindMice = OtherMice.Find;

	public Func<List<PointingDevice>> FindKeyboards = OtherMice.FindKeyboards;

	public Func<List<Headsets.Headset>> FindHeadsets = Headsets.Find;

	public Action<KeyAction> RunAction = Macro.Run;

	public string ConfigPath = LogiConfig.FilePath;

	public string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar");

	public bool SystemEvents = true;

	public bool Timers = true;

	public int FirstSyncMs = 15000;

	public Func<bool> DynamicLighting = LogiService.DynamicLightingOn;

	// Часы анимации, мс.
	public Func<long> Now = () => Environment.TickCount64;
}
