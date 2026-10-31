using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace PulseBar;

// Окно настроек работает в отдельном процессе (PulseBar.exe --settings <вкладка> <окно строки>): WPF занимает ~100 МБ и не выгружается,
// а строка должна оставаться лёгкой. Связь со строкой:
//  - окно сохраняет settings.json и посылает окну строки SettingsChanged — строка перечитывает настройки
//    (рассылка HWND_BROADCAST не подходит: скрытое окно WinForms принадлежит служебному и её не получает);
//  - пока окно открыто, строка раз в секунду пишет status.json (показания, данные Claude, видеокарты) — для превью;
//  - OpenLogitech — открыть окно Logitech (оно живёт в строке до 2.2).
internal static class SettingsProcess
{
	public static readonly uint MsgSettingsChanged = RegisterWindowMessageW("PulseBar.SettingsChanged");

	public static readonly uint MsgOpenLogitech = RegisterWindowMessageW("PulseBar.OpenLogitech");

	// Режим правки (wParam 1/0), сброс расположения, новый разделитель — делает строка: расположением владеет она.
	public static readonly uint MsgEditMode = RegisterWindowMessageW("PulseBar.EditMode");

	public static readonly uint MsgResetLayout = RegisterWindowMessageW("PulseBar.ResetLayout");

	public static readonly uint MsgAddSeparator = RegisterWindowMessageW("PulseBar.AddSeparator");

	// Logitech: перечитать logitech.json, записать профили в клавиатуру, вернуть заводские, сделать копию памяти.
	public static readonly uint MsgLogiReload = RegisterWindowMessageW("PulseBar.LogiReload");

	public static readonly uint MsgLogiWrite = RegisterWindowMessageW("PulseBar.LogiWrite");

	public static readonly uint MsgLogiRestore = RegisterWindowMessageW("PulseBar.LogiRestore");

	public static readonly uint MsgLogiBackup = RegisterWindowMessageW("PulseBar.LogiBackup");

	public static string StatusPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", "status.json");

	private static nint _bar;

	private static void Notify(uint msg, nint wParam = 0)
	{
		if (_bar != IntPtr.Zero)
		{
			PostMessageW(_bar, msg, wParam, IntPtr.Zero);
		}
	}

	// ---------- сторона строки ----------

	public static void WriteStatus(in Readings r, ClaudeUsage claude, bool light, IReadOnlyList<GpuSensor.Adapter> adapters, bool edit, Logi.LogiService logi)
	{
		try
		{
			using MemoryStream ms = new MemoryStream();
			using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
			{
				w.WriteStartObject();
				w.WriteBoolean("light", light);
				w.WriteBoolean("edit", edit);
				Num(w, "cpuLoad", r.CpuLoad);
				Num(w, "cpuTemp", r.CpuTemp);
				Num(w, "gpuLoad", r.GpuLoad);
				Num(w, "gpuTemp", r.GpuTemp);
				w.WriteBoolean("gpuAsleep", r.GpuAsleep);
				Num(w, "vramLoad", r.VramLoad);
				Num(w, "vramGb", r.VramUsedGb);
				Num(w, "ramLoad", r.RamLoad);
				Num(w, "ramGb", r.RamUsedGb);
				Num(w, "kb", r.KbBattery);
				Num(w, "mouse", r.MouseBattery);
				w.WriteBoolean("kbCharging", r.KbCharging);
				w.WriteBoolean("mouseCharging", r.MouseCharging);
				ClaudeUsage.Snapshot c = claude.Current;
				if (c != null)
				{
					w.WriteStartObject("claude");
					Num(w, "fiveHour", c.FiveHour);
					Num(w, "sevenDay", c.SevenDay);
					if (c.FiveHourReset.HasValue)
					{
						w.WriteNumber("fiveHourReset", c.FiveHourReset.Value.ToUnixTimeSeconds());
					}
					if (c.SevenDayReset.HasValue)
					{
						w.WriteNumber("sevenDayReset", c.SevenDayReset.Value.ToUnixTimeSeconds());
					}
					w.WriteNumber("updated", c.Updated.ToUnixTimeSeconds());
					w.WriteString("source", c.Source);
					w.WriteEndObject();
				}
				w.WriteString("claudeApi", claude.ApiStatus);
				w.WriteString("claudeAccount", claude.AccountLabel);
				Logi.HidppDevice kb = logi.Keyboard ?? logi.Devices.FirstOrDefault((Logi.HidppDevice d) => d.IsKeyboard);
				w.WriteStartObject("logi");
				w.WriteString("device", kb?.Name ?? "");
				w.WriteBoolean("online", kb?.Online ?? false);
				w.WriteNumber("battery", float.IsNaN(logi.KeyboardBattery) ? (-1) : ((int)logi.KeyboardBattery));
				w.WriteString("mode", (kb != null) ? logi.ModeText(kb) : "");
				w.WriteBoolean("ghub", logi.GHubActive);
				w.WriteString("result", logi.LastResult);
				w.WriteEndObject();
				w.WriteStartArray("adapters");
				foreach (GpuSensor.Adapter a in adapters)
				{
					w.WriteStartObject();
					w.WriteString("key", a.Key);
					w.WriteString("name", a.Name);
					w.WriteNumber("memory", a.DedicatedMemory);
					w.WriteEndObject();
				}
				w.WriteEndArray();
				w.WriteEndObject();
			}
			Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!);
			File.WriteAllBytes(StatusPath, ms.ToArray());
		}
		catch (Exception ex)
		{
			Log.Write("status: " + ex.Message);
		}
	}

	private static void Num(Utf8JsonWriter w, string name, float v)
	{
		if (float.IsNaN(v))
		{
			w.WriteNull(name);
		}
		else
		{
			w.WriteNumber(name, MathF.Round(v, 2));
		}
	}

	// ---------- сторона окна настроек ----------

	// test — самопроверка: окно за пределами экрана и без фокуса, через 3 с применить настройки (строка их перечитает),
	// записать в журнал память и закрыться.
	public static int Run(int page, nint bar, bool test = false)
	{
		_bar = bar;
		Settings s = Settings.Load();
		L.Set(s.Language);
		RemoteHost host = new RemoteHost(s);
		System.Windows.Application app = new System.Windows.Application
		{
			ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
		};
		host.Show(page, test);
		host.AllClosed += app.Shutdown;
		if (test)
		{
			System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(3.0)
			};
			timer.Tick += delegate
			{
				timer.Stop();
				host.Apply();
				using Process process = Process.GetCurrentProcess();
				Log.Write($"settings selftest: window {(host.Visible ? "visible" : "missing")}, status {(host.Readings.CpuLoad >= 0f ? "read" : "missing")}, private {process.PrivateMemorySize64 >> 20} MB");
				app.Shutdown();
			};
			timer.Start();
		}
		app.Run();
		return 0;
	}

	private sealed class RemoteHost : ISettingsHost
	{
		private SettingsWindow? _window;

		private Readings _readings;

		private ClaudeUsage.Snapshot? _claude;

		private string _claudeApi = "";

		private string _claudeAccount = "";

		private bool _edit;

		private bool _light;

		private List<GpuSensor.Adapter> _adapters = new List<GpuSensor.Adapter>();

		private DateTime _statusTime;

		public event Action? AllClosed;

		public RemoteHost(Settings s)
		{
			Settings = s;
			_light = ReadLightTaskbar();
			ReadStatus();
			_ = Updater.CheckAsync(s.Beta);
		}

		public Settings Settings { get; }

		public Readings Readings
		{
			get
			{
				ReadStatus();
				return _readings;
			}
		}

		public bool LightTaskbar => _light;

		public ClaudeUsage.Snapshot? Claude
		{
			get
			{
				ReadStatus();
				return _claude;
			}
		}

		public string ClaudeApiStatus => _claudeApi;

		public string ClaudeAccount => _claudeAccount;

		public bool EditMode
		{
			get
			{
				ReadStatus();
				return _edit;
			}
		}

		public void SetEditMode(bool on)
		{
			_edit = on;
			Notify(MsgEditMode, on ? 1 : 0);
		}

		public void ResetLayout()
		{
			Notify(MsgResetLayout);
		}

		public void AddSeparator()
		{
			Notify(MsgAddSeparator);
		}

		private ISettingsHost.Logitech _logi = new ISettingsHost.Logitech("", Online: false, -1, "", GHub: false, "");

		public ISettingsHost.Logitech LogiStatus
		{
			get
			{
				ReadStatus();
				return _logi;
			}
		}

		public void LogiReload()
		{
			Notify(MsgLogiReload);
		}

		public void LogiWrite()
		{
			Notify(MsgLogiWrite);
		}

		public void LogiRestore()
		{
			Notify(MsgLogiRestore);
		}

		public void LogiBackup()
		{
			Notify(MsgLogiBackup);
		}

		public Updater Updater { get; } = new Updater();

		public IReadOnlyList<GpuSensor.Adapter> Adapters => _adapters;

		public bool Visible => _window?.IsVisible ?? false;

		public void Show(int page, bool test = false)
		{
			_window = new SettingsWindow(this, page);
			if (test)
			{
				_window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
				_window.Left = -20000.0;
				_window.ShowActivated = false;
			}
			_window.Closed += delegate
			{
				if (_window == null || !_window.IsVisible)
				{
					AllClosed?.Invoke();
				}
			};
			_window.Show();
			if (!test)
			{
				_window.Activate();
			}
		}

		public void Apply()
		{
			// Расположение меняет только строка (перетаскиванием) — не затирать его своей устаревшей копией.
			Settings.Groups = Settings.Load().Groups;
			Settings.Save();
			Notify(MsgSettingsChanged);
		}

		public void SelectGpu(string key)
		{
			Settings.Gpu = key;
			Apply();
		}

		public void OpenLogitech()
		{
			Notify(MsgOpenLogitech);
		}

		public async void CheckForUpdates()
		{
			await Updater.CheckAsync(Settings.Beta);
		}

		public async void InstallUpdate()
		{
			// Установщик сам закроет строку и это окно и запустит новую версию.
			await Updater.InstallAsync();
		}

		public void ReopenSettings(int page)
		{
			SettingsWindow old = _window;
			_window = null;
			Show(page);
			old?.Close();
		}

		private void ReadStatus()
		{
			try
			{
				if (!File.Exists(StatusPath))
				{
					return;
				}
				DateTime t = File.GetLastWriteTimeUtc(StatusPath);
				if (t == _statusTime)
				{
					return;
				}
				_statusTime = t;
				using FileStream f = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using JsonDocument doc = JsonDocument.Parse(f);
				JsonElement e = doc.RootElement;
				_light = e.TryGetProperty("light", out JsonElement l) && l.GetBoolean();
				_edit = e.TryGetProperty("edit", out JsonElement ed) && ed.GetBoolean();
				_readings = new Readings
				{
					CpuLoad = F(e, "cpuLoad"),
					CpuTemp = F(e, "cpuTemp"),
					GpuLoad = F(e, "gpuLoad"),
					GpuTemp = F(e, "gpuTemp"),
					GpuAsleep = e.TryGetProperty("gpuAsleep", out JsonElement ga) && ga.GetBoolean(),
					VramLoad = F(e, "vramLoad"),
					VramUsedGb = F(e, "vramGb"),
					RamLoad = F(e, "ramLoad"),
					RamUsedGb = F(e, "ramGb"),
					KbBattery = F(e, "kb"),
					MouseBattery = F(e, "mouse"),
					KbCharging = e.TryGetProperty("kbCharging", out JsonElement kc) && kc.GetBoolean(),
					MouseCharging = e.TryGetProperty("mouseCharging", out JsonElement mc) && mc.GetBoolean()
				};
				_claude = null;
				if (e.TryGetProperty("claude", out JsonElement c) && c.ValueKind == JsonValueKind.Object)
				{
					_claude = new ClaudeUsage.Snapshot(F(c, "fiveHour"), F(c, "sevenDay"), Time(c, "fiveHourReset"), Time(c, "sevenDayReset"), Time(c, "updated") ?? DateTimeOffset.Now, c.TryGetProperty("source", out JsonElement src) ? (src.GetString() ?? "") : "");
					_readings.ClaudeShown = true;
					_readings.ClaudeFiveHour = _claude.FiveHour;
					_readings.ClaudeSevenDay = _claude.SevenDay;
					_readings.ClaudeStale = _claude.Stale;
				}
				_claudeApi = e.TryGetProperty("claudeApi", out JsonElement api) ? (api.GetString() ?? "") : "";
				if (e.TryGetProperty("logi", out JsonElement lg) && lg.ValueKind == JsonValueKind.Object)
				{
					_logi = new ISettingsHost.Logitech(lg.GetProperty("device").GetString() ?? "", lg.GetProperty("online").GetBoolean(), lg.GetProperty("battery").GetInt32(), lg.GetProperty("mode").GetString() ?? "", lg.GetProperty("ghub").GetBoolean(), lg.GetProperty("result").GetString() ?? "");
				}
				_claudeAccount = e.TryGetProperty("claudeAccount", out JsonElement acc) ? (acc.GetString() ?? "") : "";
				_adapters = new List<GpuSensor.Adapter>();
				if (e.TryGetProperty("adapters", out JsonElement ad) && ad.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement a in ad.EnumerateArray())
					{
						_adapters.Add(new GpuSensor.Adapter(a.GetProperty("key").GetString() ?? "", a.GetProperty("name").GetString() ?? "", a.GetProperty("memory").GetUInt64()));
					}
				}
			}
			catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidOperationException || ex is KeyNotFoundException)
			{
			}
		}

		private static float F(JsonElement e, string name)
		{
			if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number)
			{
				return float.NaN;
			}
			return (float)v.GetDouble();
		}

		private static DateTimeOffset? Time(JsonElement e, string name)
		{
			if (!e.TryGetProperty(name, out JsonElement v) || !v.TryGetInt64(out long unix))
			{
				return null;
			}
			return DateTimeOffset.FromUnixTimeSeconds(unix);
		}

		private static bool ReadLightTaskbar()
		{
			try
			{
				using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
				return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
			}
			catch
			{
				return false;
			}
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint RegisterWindowMessageW(string name);

	[DllImport("user32.dll")]
	private static extern int PostMessageW(nint h, uint msg, nint wParam, nint lParam);
}
