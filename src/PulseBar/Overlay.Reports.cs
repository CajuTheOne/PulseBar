using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PulseBar;

// Отчёты для отладки со стороны строки (см. DebugReports): раз в минуту — отчёт о падении (при следующем запуске), об
// ошибке (не чаще раза в 6 часов), раз в сутки и отправка очереди на NAS (нет связи — снова через 30 минут); по кнопке
// «Отправить» в окне настроек — отчёт с описанием и снимком панели задач. Сведения о состоянии строки собираются в её
// потоке, файлы и ZIP — в пуле.
internal sealed partial class Overlay
{
	private System.Threading.Timer? _reportTimer;

	private int _reportBusy;

	private long _reportsStarted;

	private long _sendAt;

	// PULSEBAR_DEBUG_REPORTS=1 — отчёты включены только для этого запуска (проверка, настройки не меняются).
	private bool ReportsOn => _s.DebugReports || Environment.GetEnvironmentVariable("PULSEBAR_DEBUG_REPORTS") == "1";

	private void StartReports()
	{
		DebugReports.Enabled = ReportsOn;
		if (_reportTimer == null)
		{
			_reportsStarted = Environment.TickCount64;
			_reportTimer = new System.Threading.Timer(delegate
			{
				ReportTick();
			}, null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));
		}
	}

	private void StopReports()
	{
		_reportTimer?.Dispose();
		_reportTimer = null;
	}

	private void ReportTick()
	{
		if (!DebugReports.Enabled || Interlocked.Exchange(ref _reportBusy, 1) == 1)
		{
			return;
		}
		try
		{
			if (File.Exists(DebugReports.CrashFile))
			{
				string crash = File.ReadAllText(DebugReports.CrashFile);
				File.Delete(DebugReports.CrashFile);
				MakeReport("crash", "", crash, StateOnUiThread(), null);
				_sendAt = 0L;
				SendNow();
				BeginInvoke(ShowCrashNotice);
			}
			string? error = DebugReports.TakeError(60000);
			if (error != null && (!(DebugReports.Last("error") is DateTime lastError) || DateTime.Now - lastError > TimeSpan.FromHours(6)))
			{
				MakeReport("error", "", error, StateOnUiThread(), null);
				_sendAt = 0L;
			}
			if (Environment.TickCount64 - _reportsStarted > 5 * 60000 && (!(DebugReports.Last("daily") is DateTime lastDaily) || DateTime.Now - lastDaily > TimeSpan.FromHours(24)))
			{
				MakeReport("daily", "", "", StateOnUiThread(), null);
				_sendAt = 0L;
			}
			if (Environment.TickCount64 >= _sendAt)
			{
				SendNow();
			}
		}
		catch (Exception ex)
		{
			Log.Write("reports: " + ex.Message);
		}
		finally
		{
			Volatile.Write(ref _reportBusy, 0);
		}
	}

	// Отправить очередь; что не ушло — снова через 30 минут.
	private void SendNow()
	{
		DebugReports.SendPending();
		_sendAt = DebugReports.List().Any((DebugReports.Entry e) => !e.Sent) ? (Environment.TickCount64 + 30 * 60000) : long.MaxValue / 2;
	}

	// Кнопка «Отправить» в окне настроек: описание — в файле запроса.
	private void SendManualReport()
	{
		if (!DebugReports.Enabled)
		{
			return;
		}
		string note = "";
		try
		{
			if (File.Exists(DebugReports.RequestFile))
			{
				note = File.ReadAllText(DebugReports.RequestFile);
				File.Delete(DebugReports.RequestFile);
			}
		}
		catch (Exception)
		{
		}
		string state = ReportState();
		byte[]? shot = TaskbarShot();
		Task.Run(delegate
		{
			// Ждать, пока закончит отчёт по расписанию.
			while (Interlocked.Exchange(ref _reportBusy, 1) == 1)
			{
				Thread.Sleep(200);
			}
			try
			{
				MakeReport("manual", note, "", state, shot);
				SendNow();
			}
			catch (Exception ex)
			{
				Log.Write("reports: " + ex.Message);
			}
			finally
			{
				Volatile.Write(ref _reportBusy, 0);
			}
		});
	}

	private string StateOnUiThread()
	{
		try
		{
			return (string)Invoke(new Func<string>(ReportState));
		}
		catch (Exception ex)
		{
			return "state: " + ex.Message;
		}
	}

	private void MakeReport(string reason, string note, string extra, string state, byte[]? shot)
	{
		List<(string, byte[])> parts = new List<(string, byte[])>();
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"PulseBar {Updater.Current} — debug report");
		sb.AppendLine($"Reason: {reason}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {DebugReports.PcId}");
		if (note.Trim().Length > 0)
		{
			sb.AppendLine();
			sb.AppendLine("== What happened (user)");
			sb.AppendLine(note.Trim());
		}
		if (extra.Length > 0)
		{
			sb.AppendLine();
			sb.AppendLine((reason == "crash") ? "== Crash" : "== Error");
			sb.AppendLine(extra);
		}
		sb.AppendLine();
		sb.Append(state);
		parts.Add(("report.txt", Encoding.UTF8.GetBytes(sb.ToString())));
		string local = Path.GetDirectoryName(Log.FilePath)!;
		string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar");
		string sensor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulseBar");
		AddFile(parts, "pulsebar.log", Log.FilePath);
		AddFile(parts, "pulsebar.old.log", Log.OldPath);
		AddFile(parts, "sensor.log", Path.Combine(sensor, "sensor.log"));
		if (ReadShared(Path.Combine(roaming, "settings.json")) is byte[] settings)
		{
			parts.Add(("settings.json", Encoding.UTF8.GetBytes(DebugReports.SanitizeSettings(Encoding.UTF8.GetString(settings)))));
		}
		AddFile(parts, "logitech.json", Path.Combine(roaming, "logitech.json"));
		AddFile(parts, "devices.json", Path.Combine(roaming, "devices.json"));
		parts.Add(("devices-report.txt", Encoding.UTF8.GetBytes(_logi.ReportNow())));
		if (shot != null)
		{
			parts.Add(("taskbar.png", shot));
		}
		string[] secrets = new string[1] { _claude.Account };
		DebugReports.Create(reason, note.Trim(), parts, (string text) => DebugReports.Anonymize(text, secrets));
	}

	private static void AddFile(List<(string, byte[])> parts, string name, string path)
	{
		if (ReadShared(path) is byte[] data)
		{
			parts.Add((name, data));
		}
	}

	private static byte[]? ReadShared(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			using FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using MemoryStream ms = new MemoryStream();
			f.CopyTo(ms);
			return ms.ToArray();
		}
		catch (Exception)
		{
			return null;
		}
	}

	// Снимок панели задач со строкой (только по кнопке «Отправить»).
	private byte[]? TaskbarShot()
	{
		try
		{
			if (_tray == IntPtr.Zero || Native.GetWindowRect(_tray, out var r) == 0 || r.Width <= 0 || r.Height <= 0)
			{
				return null;
			}
			using Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage(bmp))
			{
				g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
			}
			using MemoryStream ms = new MemoryStream();
			bmp.Save(ms, ImageFormat.Png);
			return ms.ToArray();
		}
		catch (Exception ex)
		{
			Log.Write("reports: taskbar shot: " + ex.Message);
			return null;
		}
	}

	// Система и состояние строки — в потоке строки.
	private string ReportState()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("== System");
		using (RegistryKey? nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
		{
			string build = $"{nt?.GetValue("CurrentBuild")}.{nt?.GetValue("UBR")}";
			int.TryParse(nt?.GetValue("CurrentBuild") as string, out int b);
			sb.AppendLine($"Windows: {nt?.GetValue("ProductName")} {nt?.GetValue("DisplayVersion")} build {build}{(b >= 22000 ? " (Windows 11)" : "")}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
		}
		sb.AppendLine($"Language: PulseBar {L.Code}, Windows {System.Globalization.CultureInfo.InstalledUICulture.Name}; up {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm}; admin {Environment.IsPrivilegedProcess}");
		using (RegistryKey? cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
		{
			sb.AppendLine($"CPU: {(cpu?.GetValue("ProcessorNameString") as string)?.Trim()}, {Environment.ProcessorCount} threads; RAM {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024:0.0} GB");
		}
		foreach (GpuSensor.Adapter a in _sensors.Gpu.Adapters)
		{
			sb.AppendLine($"GPU: {a.Name} ({a.Key}), {a.DedicatedMemory / 1024.0 / 1024 / 1024:0.0} GB{(a.Key == _sensors.Gpu.Current?.Key ? ", shown" : "")}");
		}
		foreach (Screen sc in Screen.AllScreens)
		{
			uint dpi = 96;
			try
			{
				nint mon = MonitorFromPoint(new Point(sc.Bounds.Left + sc.Bounds.Width / 2, sc.Bounds.Top + sc.Bounds.Height / 2), 2);
				GetDpiForMonitor(mon, 0, out dpi, out _);
			}
			catch (Exception)
			{
			}
			sb.AppendLine($"Monitor: {sc.DeviceName} {sc.Bounds.Width}x{sc.Bounds.Height} at {sc.Bounds.X},{sc.Bounds.Y}, work area {sc.WorkingArea.Width}x{sc.WorkingArea.Height}, {dpi * 100 / 96}%{(sc.Primary ? ", primary" : "")}");
		}
		sb.AppendLine("Sensor service: " + (ServiceExists("PulseBarSensor") ? "installed" : "not installed") + ", " + (Process.GetProcessesByName("PulseBar.Sensor").Length > 0 ? "running" : "not running") + "; PawnIO " + (ServiceExists("PawnIO") ? "installed" : "not installed"));
		sb.AppendLine();
		sb.AppendLine("== Taskbar and bar");
		if (_tray != IntPtr.Zero && Native.GetWindowRect(_tray, out var tr) != 0)
		{
			Rectangle mon = Screen.FromHandle(_tray).Bounds;
			string edge = (tr.Top <= mon.Top && tr.Height < mon.Height / 2) ? "top" : ((tr.Left <= mon.Left && tr.Width < mon.Width / 2) ? "left" : ((tr.Width < mon.Width / 2) ? "right" : "bottom"));
			APPBARDATA ab = new APPBARDATA
			{
				cbSize = Marshal.SizeOf<APPBARDATA>()
			};
			uint state = SHAppBarMessage(4, ref ab);
			object? al = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")?.GetValue("TaskbarAl");
			sb.AppendLine($"Taskbar: {tr.Left},{tr.Top} {tr.Width}x{tr.Height}, {edge}, auto-hide {((state & 1) != 0 ? "on" : "off")}, alignment {((al is int a0 && a0 == 0) ? "left" : "center")}, secondary taskbars {CountWindows("Shell_SecondaryTrayWnd")}");
		}
		else
		{
			sb.AppendLine("Taskbar: not found");
		}
		sb.AppendLine($"Bar: {(_bar.Exists ? (_bar.Embedded ? "inside the taskbar" : "over the taskbar") : "not created")}, shown {_visible}, edit {_edit}, scale {_scale:0.##}, tray edge {_trayEdge}, layout {_s.Layout}, text {_s.TextSize}");
		sb.AppendLine("Elements: " + string.Join(", ", _arranged.Select(((int G, int I, string Id, int L, int R) e) => $"{e.Id} {e.L}..{e.R}")));
		sb.AppendLine($"Readings: CPU {_r.CpuLoad:0}% {_r.CpuTemp:0}°C, GPU {_r.GpuLoad:0}% {_r.GpuTemp:0}°C{(_r.GpuAsleep ? " asleep" : "")}, VRAM {_r.VramLoad:0}% {_r.VramUsedGb:0.0} GB, RAM {_r.RamLoad:0}% {_r.RamUsedGb:0.0} GB, battery kb {_r.KbBattery:0} mouse {_r.MouseBattery:0} headset {_r.HeadsetBattery:0} (kinds {_r.BatteryKinds})");
		sb.AppendLine();
		sb.AppendLine("== Devices");
		sb.AppendLine($"Logitech: mode {_logi.Mode}, keyboard {_logi.KeyboardName}, G HUB {(_logi.GHubActive ? "running" : "no")}, keyboard {(_logi.Config.KeyboardOn ? "on" : "off")}, mouse {(_logi.Config.MouseOn ? "on" : "off")}, headset {(_logi.Config.HeadsetOn ? "on" : "off")}, foreign lighting '{_logi.ForeignLighting}'");
		sb.AppendLine("Charge: " + _logi.ShortStatus());
		sb.AppendLine();
		sb.AppendLine("== Claude and AI");
		ClaudeUsage.Snapshot? c = _claude.Current;
		sb.AppendLine((c == null) ? "Claude: no data" : $"Claude: 5h {c.FiveHour:0}% 7d {c.SevenDay:0}%, from {c.Source}, updated {c.Updated:HH:mm}, resets {(c.ResetsEstimated ? "estimated" : "exact")}");
		sb.AppendLine($"Claude API '{_claude.ApiStatus}', app '{_claude.AppStatus}'");
		foreach (var (id, a) in _ai.Current)
		{
			sb.AppendLine($"{id}: {a.First:0}/{a.Second:0} from '{a.Source}', status '{a.Status}', updated {a.Updated:HH:mm}");
		}
		sb.AppendLine($"Update: available {_updater.Available?.Version ?? "none"}, beta {_s.Beta}");
		return sb.ToString();
	}

	private static bool ServiceExists(string name)
	{
		using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
		return k != null;
	}

	private static int CountWindows(string cls)
	{
		int n = 0;
		nint h = IntPtr.Zero;
		while ((h = Native.FindWindowExW(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
		{
			n++;
		}
		return n;
	}

	// После падения: уведомление; щелчок — «О программе» (последняя вкладка) с полем «Что случилось?».
	private void ShowCrashNotice()
	{
		NotifyIcon icon = _trayIcon ?? new NotifyIcon
		{
			Icon = _trayIco ??= MakeTrayIcon(),
			Text = "PulseBar",
			Visible = true
		};
		bool temporary = icon != _trayIcon;
		void Clicked(object? sender, EventArgs e)
		{
			icon.BalloonTipClicked -= Clicked;
			OpenSettings(99);
		}
		icon.BalloonTipClicked += Clicked;
		icon.ShowBalloonTip(15000, "PulseBar", L.T("PulseBar closed with an error last time. A debug report was made — click to tell what you were doing."), ToolTipIcon.Info);
		if (temporary)
		{
			System.Windows.Forms.Timer t = new System.Windows.Forms.Timer
			{
				Interval = 60000
			};
			t.Tick += delegate
			{
				t.Dispose();
				icon.Dispose();
			};
			t.Start();
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct APPBARDATA
	{
		public int cbSize;

		public nint hWnd;

		public uint uCallbackMessage;

		public uint uEdge;

		public Native.RECT rc;

		public nint lParam;
	}

	[DllImport("shell32.dll")]
	private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(Point pt, uint flags);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}
