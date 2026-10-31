using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PulseBar;

// Диагностика: PulseBar.exe --shot-settings <файл.png> <вкладка 0..6> [expand|-] [язык] — рисует окно настроек в PNG,
// не показывая его на экране (окно за пределами экрана). Значения датчиков — примерные.
internal static class SettingsShot
{
	private sealed class Host : ISettingsHost
	{
		public Settings Settings { get; init; } = null!;

		public Readings Readings { get; init; }

		public bool LightTaskbar => false;

		public ClaudeUsage.Snapshot? Claude { get; init; }

		public string ClaudeApiStatus => "";

		public string ClaudeAccount { get; init; } = "";

		public Updater Updater { get; } = new Updater();

		public IReadOnlyList<GpuSensor.Adapter> Adapters { get; init; } = Array.Empty<GpuSensor.Adapter>();

		public void Apply()
		{
		}

		public void SelectGpu(string key)
		{
		}

		public void OpenLogitech()
		{
		}

		public void CheckForUpdates()
		{
		}

		public void InstallUpdate()
		{
		}

		public void ReopenSettings(int page)
		{
		}

		public bool EditMode => false;

		public void SetEditMode(bool on)
		{
		}

		public void ResetLayout()
		{
		}

		public void AddSeparator()
		{
		}
	}

	public static int Run(string png, int page, bool expand, string? language)
	{
		Settings s = Settings.Load();
		L.Set(language ?? s.Language);
		using ClaudeUsage claude = new ClaudeUsage(s.ClaudeIntervalMin, api: false);
		Thread.Sleep(4500);
		Host host = new Host
		{
			Settings = s,
			Claude = claude.Current,
			ClaudeAccount = claude.AccountLabel,
			Readings = new Readings
			{
				CpuLoad = 7f,
				CpuTemp = 63f,
				GpuLoad = 1f,
				GpuTemp = 48f,
				VramLoad = 39f,
				VramUsedGb = 6.1f,
				RamLoad = 72f,
				RamUsedGb = 33.9f,
				KbBattery = 64f,
				MouseBattery = float.NaN,
				ClaudeFiveHour = claude.Current?.FiveHour ?? float.NaN,
				ClaudeSevenDay = claude.Current?.SevenDay ?? float.NaN,
				ClaudeStale = claude.Current?.Stale ?? false
			},
			Adapters = new GpuSensor.Adapter[2]
			{
				new GpuSensor.Adapter("VEN_10DE&DEV_2717", "NVIDIA GeForce RTX 4090 Laptop GPU", 17179869184uL),
				new GpuSensor.Adapter("VEN_1002&DEV_164E", "AMD Radeon(TM) 610M", 536870912uL)
			}
		};
		SettingsWindow w = new SettingsWindow(host, page)
		{
			WindowStartupLocation = WindowStartupLocation.Manual,
			Left = -20000.0,
			Top = 0.0,
			ShowActivated = false
		};
		// Настоящее окно показывает фон Mica от Windows; при отрисовке в файл его нет — подложить тёмный.
		w.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
		w.Show();
		Pump();
		if (expand)
		{
			foreach (Expander e in Find<Expander>(w))
			{
				e.IsExpanded = true;
			}
			w.Height = 1100.0;
			Pump();
		}
		double dpi = VisualTreeHelper.GetDpi(w).PixelsPerDip;
		RenderTargetBitmap bmp = new RenderTargetBitmap((int)(w.ActualWidth * dpi), (int)(w.ActualHeight * dpi), 96.0 * dpi, 96.0 * dpi, PixelFormats.Pbgra32);
		bmp.Render(w);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using (FileStream f = File.Create(png))
		{
			enc.Save(f);
		}
		w.Close();
		return 0;
	}

	private static void Pump()
	{
		for (int i = 0; i < 20; i++)
		{
			Dispatcher.CurrentDispatcher.Invoke(delegate
			{
			}, DispatcherPriority.ApplicationIdle);
			Thread.Sleep(50);
		}
	}

	private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is T t)
			{
				yield return t;
			}
			foreach (T item in Find<T>(child))
			{
				yield return item;
			}
		}
	}
}
