using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PulseBar.Logi;

// Диагностика клавиатуры: PulseBar.exe --logi-diag [журнал.txt] (2.3).
// Окно ведёт пользователя по шагам («нажмите M1» …) и пишет в журнал все пакеты HID++ в обе стороны с временем:
// сначала сведения об устройстве, затем нажатия во встроенном режиме, затем в режиме PulseBar (G-клавиши перехвачены),
// затем отклик клавиатуры на наши же команды (лампочки M, смена профиля). Нажимает только пользователь.
// На время проверки работающая строка PulseBar отпускает клавиатуру (enabled=false в logitech.json + PulseBar.LogiReload),
// в конце всё возвращается: G-клавиши, встроенный режим, номер профиля, настройка строки.
internal sealed class LogiDiag : Form
{
	private sealed record StepDef(string Id, string Prompt, int MaxMs = 0, int QuietMs = 1200);

	private readonly string _path;

	private readonly Stopwatch _clock = Stopwatch.StartNew();

	private readonly StringBuilder _log = new StringBuilder();

	private readonly Label _title = new Label();

	private readonly Label _prompt = new Label();

	private readonly Label _hint = new Label();

	private readonly ListBox _events = new ListBox();

	private readonly Button _skip = new Button();

	private readonly Button _finish = new Button();

	private Thread? _worker;

	private volatile bool _closing;

	private volatile bool _skipStep;

	private long _lastEventMs = -1;

	private LogiDevice? _kb;

	private bool _done;

	private static readonly uint MsgLogiReload = RegisterWindowMessageW("PulseBar.LogiReload");

	// PulseBar.exe --logi-backup <файл.json> — копия памяти клавиатуры (только чтение; строка не мешает — запросы со своим id).
	public static int Backup(string path)
	{
		List<HidppLink> links = HidppLink.OpenAll();
		try
		{
			foreach (HidppLink l in links)
			{
				for (int slot = 1; slot <= (l.IsReceiver ? 6 : 1); slot++)
				{
					byte index = l.IsReceiver ? (byte)slot : (byte)0xFF;
					string name = l.IsReceiver ? LogiDevice.PairedName(l, index) : "";
					LogiDevice? d = (name.Length > 0 || !l.IsReceiver) ? LogiDevice.Open(l, index, name) : null;
					OnboardMemory? m = (d != null) ? OnboardMemory.Open(d) : null;
					if (m != null)
					{
						Console.WriteLine($"{d!.Name}: {m.Backup(path)} -> {path}");
						return 0;
					}
				}
			}
			return 1;
		}
		finally
		{
			foreach (HidppLink l in links)
			{
				l.Dispose();
			}
		}
	}

	// --logi-call <функция hex> <fn> [аргументы hex]: один запрос HID++ к клавиатуре Logitech (можно рядом с работающей
	// строкой); запрос и ответ дописываются в %LOCALAPPDATA%\PulseBar\logi-call.txt. Для разбора ошибок на устройстве.
	public static int CallOnce(string feature, string fn, string args)
	{
		string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", "logi-call.txt");
		ushort id = Convert.ToUInt16(feature, 16);
		byte f = byte.Parse(fn);
		byte[] a = Convert.FromHexString(args);
		string result = "no keyboard";
		List<HidppLink> links = HidppLink.OpenAll();
		try
		{
			foreach (HidppLink l in links)
			{
				for (int slot = 1; slot <= (l.IsReceiver ? 6 : 1) && result == "no keyboard"; slot++)
				{
					byte index = l.IsReceiver ? (byte)slot : (byte)0xFF;
					string name = l.IsReceiver ? LogiDevice.PairedName(l, index) : "";
					LogiDevice? d = (name.Length > 0 || !l.IsReceiver) ? LogiDevice.Open(l, index, name) : null;
					if (d != null && d.Has(LogiDevice.GKeys))
					{
						int e = 0;
						byte[]? r = d.Has(id) ? d.Call(id, f, a, out e, 1) : null;
						result = !d.Has(id) ? "no such feature" : (r != null) ? Convert.ToHexString(r) : $"error {e}";
					}
				}
			}
		}
		finally
		{
			foreach (HidppLink l in links)
			{
				l.Dispose();
			}
		}
		File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {id:X4} fn{f} {Convert.ToHexString(a)} -> {result}{Environment.NewLine}");
		return (result.StartsWith("error", StringComparison.Ordinal) || result.StartsWith("no ", StringComparison.Ordinal)) ? 1 : 0;
	}

	public static int Run(string? path)
	{
		ApplicationConfiguration.Initialize();
		string file = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", $"logi-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
		using LogiDiag f = new LogiDiag(file);
		Application.Run(f);
		return f._done ? 0 : 1;
	}

	private LogiDiag(string path)
	{
		_path = path;
		Text = "PulseBar — диагностика клавиатуры Logitech";
		Font = new Font("Segoe UI", 10f);
		ClientSize = new Size(620, 460);
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		StartPosition = FormStartPosition.CenterScreen;
		TopMost = true;
		_title.SetBounds(16, 12, 588, 24);
		_title.Text = "Подготовка…";
		_prompt.SetBounds(16, 40, 588, 64);
		_prompt.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
		_hint.SetBounds(16, 108, 588, 44);
		_events.SetBounds(16, 156, 588, 244);
		_events.Font = new Font("Consolas", 9.5f);
		_skip.SetBounds(16, 412, 180, 34);
		_skip.Text = "Пропустить шаг";
		_skip.Click += delegate
		{
			_skipStep = true;
		};
		_finish.SetBounds(424, 412, 180, 34);
		_finish.Text = "Закончить";
		_finish.Click += delegate
		{
			Close();
		};
		Controls.AddRange(new Control[6] { _title, _prompt, _hint, _events, _skip, _finish });
	}

	protected override void OnShown(EventArgs e)
	{
		base.OnShown(e);
		_worker = new Thread(Work)
		{
			IsBackground = true,
			Name = "logi-diag"
		};
		_worker.Start();
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		// Сначала вернуть клавиатуру и строку в прежнее состояние (это делает рабочий поток), потом закрыться.
		if (_worker != null && _worker.IsAlive)
		{
			_closing = true;
			e.Cancel = true;
			_prompt.Text = "Возвращаю настройки…";
			return;
		}
		base.OnFormClosing(e);
	}

	private void Ui(Action a)
	{
		if (IsHandleCreated && !IsDisposed)
		{
			try
			{
				BeginInvoke(a);
			}
			catch (InvalidOperationException)
			{
			}
		}
	}

	private void Show(string title, string prompt, string hint)
	{
		Ui(delegate
		{
			_title.Text = title;
			_prompt.Text = prompt;
			_hint.Text = hint;
		});
	}

	private void AddEvent(string s)
	{
		Ui(delegate
		{
			_events.Items.Add(s);
			_events.TopIndex = Math.Max(0, _events.Items.Count - 1);
		});
	}

	private void Line(string s)
	{
		lock (_log)
		{
			_log.AppendLine(s);
		}
	}

	private void Work()
	{
		bool? barEnabled = null;
		List<HidppLink> links = new List<HidppLink>();
		byte origMode = 0;
		int origProfile = -1;
		bool diverted = false;
		try
		{
			Line($"PulseBar {Updater.Current} logi-diag {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			// 1. G HUB должен быть закрыт: он сам переключает режимы и перехватывает клавиши.
			while (LogiService.GHubRunning() && !_closing)
			{
				Show("Шаг 0", "Закройте G HUB", "Выйдите из G HUB через значок в трее. Проверка начнётся сама.");
				Thread.Sleep(1000);
			}
			if (_closing)
			{
				return;
			}
			// 2. Работающая строка PulseBar отпускает клавиатуру.
			barEnabled = PauseBar();
			Line($"bar paused: {(barEnabled.HasValue ? $"yes (enabled was {barEnabled})" : "no bar running")}");
			if (barEnabled == true)
			{
				Thread.Sleep(1500);
			}
			// 3. Найти клавиатуру; спящую — разбудить нажатием.
			while (!_closing)
			{
				links = HidppLink.OpenAll();
				_kb = FindKeyboard(links);
				if (_kb != null)
				{
					break;
				}
				foreach (HidppLink l in links)
				{
					l.Dispose();
				}
				links.Clear();
				Show("Шаг 0", "Нажмите любую клавишу", "Клавиатура Logitech с G-клавишами не найдена или спит.");
				Thread.Sleep(1500);
			}
			if (_closing || _kb == null)
			{
				return;
			}
			LogiDevice kb = _kb;
			kb.Link.Raw += OnRaw;
			kb.Link.Notification += OnNotification;
			kb.Link.Connection += delegate(byte dev, bool linked)
			{
				Line($"{_clock.ElapsedMilliseconds,7} CONNECTION dev {dev} linked {linked}");
				AddEvent($"связь: устройство {dev} {(linked ? "на связи" : "отключилось")}");
			};
			Info(kb);
			origMode = (kb.Call(LogiDevice.OnboardProfiles, 2) is byte[] m) ? m[0] : (byte)0;
			origProfile = (kb.Call(LogiDevice.OnboardProfiles, 4) is byte[] p) ? ((p[0] << 8) | p[1]) : (-1);
			Line($"original onboard mode {origMode}, profile {origProfile}");
			// 4. Встроенный режим: что клавиатура сообщает сама.
			Command(kb, "set onboard mode 1", LogiDevice.OnboardProfiles, 1, 1);
			RunSteps("Встроенный режим (как без PulseBar)", new StepDef[5]
			{
				new StepDef("onboard:M2", "Нажмите M2"),
				new StepDef("onboard:M3", "Нажмите M3"),
				new StepDef("onboard:M1", "Нажмите M1"),
				new StepDef("onboard:MR", "Нажмите MR", 5000),
				new StepDef("onboard:G1", "Нажмите G1", 5000)
			});
			// 5. Режим PulseBar: встроенные профили выключены, G-клавиши перехвачены.
			Section("host setup");
			Command(kb, "set host mode 2", LogiDevice.OnboardProfiles, 1, 2);
			diverted = Command(kb, "divert G-keys", LogiDevice.GKeys, 2, 1);
			Command(kb, "M LEDs = M1", LogiDevice.MKeys, 1, 1);
			Quiet(800);
			RunSteps("Режим PulseBar", new StepDef[13]
			{
				new StepDef("host:M1", "Нажмите M1"),
				new StepDef("host:M2", "Нажмите M2"),
				new StepDef("host:M3", "Нажмите M3"),
				new StepDef("host:M1again", "Ещё раз M1"),
				new StepDef("host:MR", "Нажмите MR", 6000),
				new StepDef("host:G1", "Нажмите G1"),
				new StepDef("host:G2", "Нажмите G2"),
				new StepDef("host:G3", "Нажмите G3"),
				new StepDef("host:G4", "Нажмите G4"),
				new StepDef("host:G5", "Нажмите G5"),
				new StepDef("host:G1hold", "Зажмите G1 на 2 секунды", 0, 1500),
				new StepDef("host:brightness", "Нажмите кнопку яркости ☀", 6000),
				new StepDef("host:plainkey", "Нажмите пробел (событий быть не должно)", 4000)
			});
			// 6. Отклик на наши команды: не приходят ли от лампочек M и смены профиля «нажатия».
			Show("Автоматическая проверка", "Ничего не нажимайте", "Около 5 секунд: клавиатура отвечает на команды PulseBar.");
			Section("auto:mled");
			foreach (byte leds in new byte[4] { 2, 4, 1, 0 })
			{
				Command(kb, $"M LEDs = {leds:X2}", LogiDevice.MKeys, 1, leds);
				Quiet(700);
			}
			Section("auto:setprofile-host");
			Command(kb, "set current profile 2 (host mode)", LogiDevice.OnboardProfiles, 3, 0, 2);
			Quiet(800);
			Command(kb, "set current profile 1 (host mode)", LogiDevice.OnboardProfiles, 3, 0, 1);
			Quiet(800);
			_done = true;
		}
		catch (Exception ex)
		{
			Line("ERROR " + ex);
			AddEvent("ошибка: " + ex.Message);
		}
		finally
		{
			Restore(_kb, diverted, origMode, origProfile);
			if (barEnabled.HasValue)
			{
				ResumeBar(barEnabled.Value);
			}
			Save();
			foreach (HidppLink l in links)
			{
				l.Dispose();
			}
			Ui(delegate
			{
				_skip.Enabled = false;
				_finish.Text = "Закрыть";
				if (_closing)
				{
					Close();
				}
			});
			Show(_done ? "Готово" : "Остановлено", _done ? "Спасибо!" : "Проверка прервана", "Журнал: " + _path);
		}
	}

	private void Restore(LogiDevice? kb, bool diverted, byte origMode, int origProfile)
	{
		if (kb == null)
		{
			return;
		}
		Section("restore");
		if (diverted)
		{
			Command(kb, "undivert G-keys", LogiDevice.GKeys, 2, 0);
		}
		Command(kb, $"set onboard mode {(origMode == 2 ? 2 : 1)}", LogiDevice.OnboardProfiles, 1, (origMode == 2) ? (byte)2 : (byte)1);
		if (origProfile > 0)
		{
			Command(kb, $"set current profile {origProfile}", LogiDevice.OnboardProfiles, 3, (byte)(origProfile >> 8), (byte)origProfile);
		}
		Quiet(800);
	}

	private void RunSteps(string phase, StepDef[] steps)
	{
		for (int i = 0; i < steps.Length && !_closing; i++)
		{
			StepDef s = steps[i];
			Section("step " + s.Id);
			Show($"{phase} · шаг {i + 1} из {steps.Length}", s.Prompt, (s.MaxMs > 0) ? "Если ничего не происходит — шаг закончится сам или нажмите «Пропустить шаг»." : "После нажатия шаг сменится сам.");
			Interlocked.Exchange(ref _lastEventMs, -1);
			_skipStep = false;
			long start = _clock.ElapsedMilliseconds;
			while (!_closing)
			{
				long now = _clock.ElapsedMilliseconds;
				long last = Interlocked.Read(ref _lastEventMs);
				if (_skipStep)
				{
					Line("-- skipped");
					break;
				}
				if (last >= 0 && now - last >= s.QuietMs)
				{
					break;
				}
				if (last < 0 && s.MaxMs > 0 && now - start >= s.MaxMs)
				{
					Line("-- no events");
					AddEvent($"{s.Id}: событий нет");
					break;
				}
				Thread.Sleep(40);
			}
		}
	}

	private void Section(string name)
	{
		Line($"== {name}");
	}

	private void Quiet(int ms)
	{
		Thread.Sleep(ms);
	}

	private bool Command(LogiDevice kb, string what, ushort feature, byte fn, params byte[] args)
	{
		byte[]? r = kb.Call(feature, fn, args, out int err, 3);
		Line($"{_clock.ElapsedMilliseconds,7} CMD {what}: {((r != null) ? "ok" : $"error {err}")}");
		return r != null;
	}

	private void OnRaw(bool inbound, byte[] r)
	{
		Line($"{_clock.ElapsedMilliseconds,7} {(inbound ? "IN " : "OUT")} {Convert.ToHexString(r)}");
	}

	private void OnNotification(byte dev, byte feat, byte fn, byte[] data)
	{
		LogiDevice? kb = _kb;
		if (kb == null || dev != kb.Index)
		{
			return;
		}
		ushort id = kb.FeatureAt(feat);
		AddEvent($"{LogiDevice.FeatureName(id)} fn{fn}: {Convert.ToHexString(data, 0, Math.Min(6, data.Length))}");
		// Заряд и состояние связи приходят сами по себе — они не «нажатие».
		if (id != LogiDevice.BatteryVoltage && id != LogiDevice.BatteryStatus && id != LogiDevice.UnifiedBattery && id != LogiDevice.WirelessStatus)
		{
			Interlocked.Exchange(ref _lastEventMs, _clock.ElapsedMilliseconds);
		}
	}

	private LogiDevice? FindKeyboard(List<HidppLink> links)
	{
		foreach (HidppLink l in links)
		{
			Line($"link {l.Pid:X4} receiver {l.IsReceiver}");
			for (int slot = 1; slot <= (l.IsReceiver ? 6 : 1); slot++)
			{
				byte index = l.IsReceiver ? (byte)slot : (byte)0xFF;
				string name = l.IsReceiver ? LogiDevice.PairedName(l, index) : "";
				if (l.IsReceiver && name.Length == 0)
				{
					continue;
				}
				LogiDevice? d = LogiDevice.Open(l, index, name);
				Line($"  [{index}] {name}: {((d == null) ? "no answer" : $"{d.Name}, HID++ {d.Protocol}, {d.Kind}, {d.Features.Count} features")}");
				if (d != null && d.Has(LogiDevice.GKeys))
				{
					return d;
				}
			}
		}
		return null;
	}

	private void Info(LogiDevice kb)
	{
		Section("info");
		Line($"device {kb.Name}, index {kb.Index}, link {kb.Link.Pid:X4}, HID++ {kb.Protocol}, kind {kb.Kind}");
		foreach (var (id, f) in kb.Features.OrderBy(x => x.Value.Index))
		{
			Line($"  #{f.Index,2} 0x{id:X4} v{f.Version} {LogiDevice.FeatureName(id)}");
		}
		Dump(kb, LogiDevice.OnboardProfiles, 0, "onboard description");
		Dump(kb, LogiDevice.OnboardProfiles, 2, "onboard mode");
		Dump(kb, LogiDevice.OnboardProfiles, 4, "onboard current profile");
		Dump(kb, LogiDevice.GKeys, 0, "gkeys count");
		Dump(kb, LogiDevice.MKeys, 0, "mkeys count");
		Dump(kb, LogiDevice.MacroRecord, 0, "macrorecord fn0");
		Dump(kb, LogiDevice.Brightness, 0, "brightness info");
		Dump(kb, LogiDevice.Brightness, 1, "brightness get");
		Dump(kb, LogiDevice.KeyboardLayout, 0, "layout fn0");
		Dump(kb, LogiDevice.PerKeyLighting, 0, "perkey info 0", 0, 0);
		Dump(kb, LogiDevice.PerKeyLighting, 0, "perkey info 1", 0, 1);
		Dump(kb, LogiDevice.PerKeyLighting, 0, "perkey info 2", 0, 2);
		Dump(kb, LogiDevice.RgbEffects, 0, "rgb info ff ff", 0xFF, 0xFF);
		for (byte c = 0; c < 3; c++)
		{
			Dump(kb, LogiDevice.RgbEffects, 0, $"rgb cluster {c} info", c, 0xFF);
			for (byte e = 0; e < 12; e++)
			{
				if (!Dump(kb, LogiDevice.RgbEffects, 0, $"rgb cluster {c} effect {e}", c, e))
				{
					break;
				}
			}
		}
		Line($"battery {kb.ReadBattery(out bool charging)}%, charging {charging}");
	}

	private bool Dump(LogiDevice kb, ushort feature, byte fn, string what, params byte[] args)
	{
		if (!kb.Has(feature))
		{
			return false;
		}
		byte[]? r = kb.Call(feature, fn, args, out int err, 2);
		Line($"  {what}: {((r != null) ? Convert.ToHexString(r) : $"error {err}")}");
		return r != null;
	}

	private void Save()
	{
		try
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
			lock (_log)
			{
				File.WriteAllText(_path, _log.ToString());
			}
			Log.Write("logi: diagnostics saved to " + _path);
		}
		catch (Exception ex)
		{
			Log.Write("logi: diagnostics not saved: " + ex.Message);
		}
	}

	// Строка PulseBar (другой процесс) отпускает клавиатуру: enabled=false и команда перечитать logitech.json.
	// Возвращает прежнее значение enabled; null — строка не запущена.
	private static bool? PauseBar()
	{
		List<nint> bars = BarWindows();
		if (bars.Count == 0)
		{
			return null;
		}
		LogiConfig c = LogiConfig.Load();
		bool was = c.Enabled;
		if (was)
		{
			c.Enabled = false;
			c.Save();
			foreach (nint h in bars)
			{
				PostMessageW(h, MsgLogiReload, 0, 0);
			}
		}
		return was;
	}

	private static void ResumeBar(bool enabled)
	{
		if (!enabled)
		{
			return;
		}
		LogiConfig c = LogiConfig.Load();
		c.Enabled = true;
		c.Save();
		foreach (nint h in BarWindows())
		{
			PostMessageW(h, MsgLogiReload, 0, 0);
		}
	}

	// Окна «PulseBar» других процессов PulseBar (строка; окно настроек называется иначе).
	private static List<nint> BarWindows()
	{
		HashSet<uint> pids = new HashSet<uint>();
		int self = Environment.ProcessId;
		foreach (Process p in Process.GetProcessesByName("PulseBar"))
		{
			if (p.Id != self)
			{
				pids.Add((uint)p.Id);
			}
			p.Dispose();
		}
		List<nint> list = new List<nint>();
		if (pids.Count == 0)
		{
			return list;
		}
		EnumWindows(delegate(nint h, nint _)
		{
			GetWindowThreadProcessId(h, out uint pid);
			if (pids.Contains(pid))
			{
				StringBuilder sb = new StringBuilder(64);
				GetWindowTextW(h, sb, sb.Capacity);
				if (sb.ToString() == "PulseBar")
				{
					list.Add(h);
				}
			}
			return true;
		}, 0);
		return list;
	}

	private delegate bool EnumProc(nint h, nint lp);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc cb, nint lp);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextW(nint h, StringBuilder sb, int max);

	[DllImport("user32.dll")]
	private static extern bool PostMessageW(nint h, uint msg, nint wp, nint lp);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint RegisterWindowMessageW(string name);
}
