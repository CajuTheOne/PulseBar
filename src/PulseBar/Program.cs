using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PulseBar.Logi;

namespace PulseBar;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		if (args != null && args.Length == 2 && args[0] == "--autostart")
		{
			return (!((args[1] == "on") ? Autostart.Enable(Environment.ProcessPath) : Autostart.Disable())) ? 1 : 0;
		}
		if (args != null && args.Length == 1 && args[0] == "--dump")
		{
			return Dump();
		}
		if (args != null && args.Length == 3 && args[0] == "--shot-logi" && args[2] == "thr")
		{
			Settings settings = Settings.Load();
			L.Set(settings.Language);
			ApplicationConfiguration.Initialize();
			using ThresholdsForm thresholdsForm = new ThresholdsForm(settings, dark: true)
			{
				StartPosition = FormStartPosition.Manual,
				Location = new Point(-5000, -5000)
			};
			thresholdsForm.Show();
			Application.DoEvents();
			using Bitmap bitmap = new Bitmap(thresholdsForm.Width, thresholdsForm.Height);
			thresholdsForm.DrawToBitmap(bitmap, new Rectangle(0, 0, thresholdsForm.Width, thresholdsForm.Height));
			bitmap.Save(args[1]);
			return 0;
		}
		if (args != null && args.Length == 2 && args[0] == "--logi-probe")
		{
			return LogiProbe.Run(args[1]);
		}
		if (args != null && args.Length >= 1 && args[0] == "--settings")
		{
			return SettingsProcess.Run((args.Length > 1 && int.TryParse(args[1], out var page)) ? page : 0, (args.Length > 2 && long.TryParse(args[2], out var bar)) ? new IntPtr(bar) : IntPtr.Zero, args.Length > 3 && args[3] == "test");
		}
		if (args != null && args.Length >= 3 && args[0] == "--shot-settings")
		{
			return SettingsShot.Run(args[1], int.Parse(args[2]), args.Length > 3 && args[3] == "expand", (args.Length > 4) ? args[4] : null);
		}
		if (args != null && args.Length == 3 && args[0] == "--shot-logi")
		{
			return ShotLogi(args[1], args[2] == "mouse");
		}
		using Mutex mutex = AcquireSingleInstance();
		if (mutex == null)
		{
			return 0;
		}
		Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		Application.ThreadException += delegate(object _, ThreadExceptionEventArgs e)
		{
			Log.Write("error: " + e.Exception);
		};
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs e)
		{
			Log.Write("fatal: " + e.ExceptionObject);
		};
		Native.EnableEcoQoS();
		ApplicationConfiguration.Initialize();
		using (Overlay mainForm = new Overlay(Settings.Load()))
		{
			Application.Run(mainForm);
		}
		mutex.ReleaseMutex();
		return 0;
	}

	private static Mutex? AcquireSingleInstance()
	{
		Mutex mutex;
		try
		{
			mutex = new Mutex(initiallyOwned: false, "Local\\PulseBar.SingleInstance");
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
		try
		{
			if (mutex.WaitOne(3000))
			{
				return mutex;
			}
		}
		catch (AbandonedMutexException)
		{
			return mutex;
		}
		mutex.Dispose();
		return null;
	}

	private static int ShotLogi(string png, bool mouseTab)
	{
		try
		{
			return ShotLogiCore(png, mouseTab);
		}
		catch (Exception ex)
		{
			Log.Write("shot: " + ex);
			return 1;
		}
	}

	private static int ShotLogiCore(string png, bool mouseTab)
	{
		L.Set(Settings.Load().Language);
		ApplicationConfiguration.Initialize();
		using LogiService logiService = new LogiService(LogiConfig.Load())
		{
			ReadOnly = true
		};
		logiService.Start();
		Thread.Sleep(5000);
		using LogiForm logiForm = new LogiForm(logiService, dark: true);
		logiForm.StartPosition = FormStartPosition.Manual;
		logiForm.Location = new Point(-5000, -5000);
		logiForm.Show();
		if (mouseTab)
		{
			logiForm.ShowTab(1);
		}
		Application.DoEvents();
		Thread.Sleep(300);
		Application.DoEvents();
		using Bitmap bitmap = new Bitmap(logiForm.Width, logiForm.Height);
		logiForm.DrawToBitmap(bitmap, new Rectangle(0, 0, logiForm.Width, logiForm.Height));
		bitmap.Save(png);
		return 0;
	}

	private static int Dump()
	{
		Settings settings = Settings.Load();
		L.Set(settings.Language);
		using Sensors sensors = new Sensors(settings.Gpu);
		Thread.Sleep(1000);
		Readings readings = sensors.Read();
		string value = string.Join("; ", sensors.Gpu.Adapters.Select((GpuSensor.Adapter a) => $"{a.Name} [{a.Key}, {a.DedicatedMemory >> 20} MB]"));
		File.WriteAllText(Path.Combine(Path.GetTempPath(), "PulseBar-dump.txt"), $"version: {Updater.Current}\nadmin: {Environment.IsPrivilegedProcess}\nCPU temp source: {sensors.CpuTemp.Status}\nGPU: {sensors.Gpu.Current?.Name} | all: {value}\nCPU {readings.CpuLoad:0.0}% {readings.CpuTemp:0.0}°C | GPU {readings.GpuLoad:0.0}% {readings.GpuTemp:0.0}°C asleep={readings.GpuAsleep} | VRAM {readings.VramLoad:0.0}% {readings.VramUsedGb:0.0} GB | RAM {readings.RamLoad:0.0}% {readings.RamUsedGb:0.0} GB\n");
		return 0;
	}
}
