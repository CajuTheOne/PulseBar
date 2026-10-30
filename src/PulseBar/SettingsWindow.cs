#pragma warning disable WPF0001 // ThemeMode (тема Fluent) в .NET 9 помечен как экспериментальный
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PulseBar.Logi;
using Bitmap = System.Drawing.Bitmap;
using Graphics = System.Drawing.Graphics;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PulseBar;

// Что окну настроек нужно от строки (её реализует Overlay).
internal interface ISettingsHost
{
	Settings Settings { get; }

	Readings Readings { get; }

	bool LightTaskbar { get; }

	// Последние данные Claude (null — нет данных) и состояние запросов к claude.ai.
	ClaudeUsage.Snapshot? Claude { get; }

	string ClaudeApiStatus { get; }

	Updater Updater { get; }

	IReadOnlyList<GpuSensor.Adapter> Adapters { get; }

	// Сохранить настройки и перерисовать строку.
	void Apply();

	void SelectGpu(string key);

	void OpenLogitech();

	void CheckForUpdates();

	void InstallUpdate();

	// Пересоздать окно (после смены языка), открыв ту же вкладку.
	void ReopenSettings(int page);
}

// Окно настроек: вкладки слева, тема Fluent (как в Windows 11, светлая/тёмная — по системе).
// Изменения применяются сразу; перевод — через L.T, поэтому после смены языка окно пересоздаётся.
internal sealed class SettingsWindow : Window
{
	private readonly ISettingsHost _host;

	private readonly Settings _s;

	private readonly ListBox _nav = new ListBox();

	private readonly ContentControl _page = new ContentControl();

	private readonly List<(string Title, Func<UIElement> Build)> _pages = new List<(string, Func<UIElement>)>();

	private readonly DispatcherTimer _timer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(1.0)
	};

	private readonly BarRenderer _previewRenderer = new BarRenderer();

	private Image? _preview;

	private Action? _refresh;

	public SettingsWindow(ISettingsHost host, int page)
	{
		_host = host;
		_s = host.Settings;
		ThemeMode = ThemeMode.System;
		Title = L.T("PulseBar — settings");
		Width = 880.0;
		Height = 640.0;
		MinWidth = 640.0;
		MinHeight = 480.0;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		_pages.Add((L.T("Appearance"), AppearancePage));
		_pages.Add((L.T("Blocks"), BlocksPage));
		_pages.Add((L.T("Thresholds"), ThresholdsPage));
		_pages.Add((L.T("Keyboard"), KeyboardPage));
		_pages.Add((L.T("Mouse"), MousePage));
		_pages.Add(("Claude", ClaudePage));
		_pages.Add((L.T("About"), AboutPage));
		foreach (var (title, _) in _pages)
		{
			_nav.Items.Add(new ListBoxItem
			{
				Content = title,
				Padding = new Thickness(12.0, 8.0, 12.0, 8.0)
			});
		}
		_nav.BorderThickness = new Thickness(0.0);
		_nav.Background = Brushes.Transparent;
		_nav.Margin = new Thickness(8.0);
		_nav.SelectionChanged += delegate
		{
			ShowPage(_nav.SelectedIndex);
		};
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(190.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.Children.Add(_nav);
		ScrollViewer scroll = new ScrollViewer
		{
			Content = _page,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Padding = new Thickness(20.0, 16.0, 24.0, 24.0)
		};
		Grid.SetColumn(scroll, 1);
		grid.Children.Add(scroll);
		Content = grid;
		_nav.SelectedIndex = Math.Clamp(page, 0, _pages.Count - 1);
		_timer.Tick += delegate
		{
			_refresh?.Invoke();
			UpdatePreview();
		};
		_timer.Start();
		Closed += delegate
		{
			_timer.Stop();
			_previewRenderer.Dispose();
		};
	}

	public int PageIndex => _nav.SelectedIndex;

	private void ShowPage(int index)
	{
		if (index >= 0)
		{
			_refresh = null;
			_preview = null;
			_page.Content = _pages[index].Build();
			UpdatePreview();
		}
	}

	private void Changed()
	{
		_host.Apply();
		UpdatePreview();
	}

	// ---------- Вид ----------

	private UIElement AppearancePage()
	{
		StackPanel p = Page(L.T("Appearance"));
		p.Children.Add(Caption(L.T("Preview")));
		_preview = new Image
		{
			Stretch = Stretch.None,
			HorizontalAlignment = HorizontalAlignment.Left,
			SnapsToDevicePixels = true
		};
		RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
		p.Children.Add(new Border
		{
			Child = _preview,
			Background = new SolidColorBrush(_host.LightTaskbar ? Color.FromRgb(238, 238, 238) : Color.FromRgb(28, 28, 28)),
			CornerRadius = new CornerRadius(6.0),
			Padding = new Thickness(10.0, 4.0, 10.0, 4.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0),
			HorizontalAlignment = HorizontalAlignment.Stretch
		});
		p.Children.Add(Caption(L.T("Presets")));
		WrapPanel presets = new WrapPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		};
		presets.Children.Add(Preset(L.T("Standard"), L.T("two lines, all blocks"), delegate
		{
			_s.Layout = 1;
			_s.TextSize = 1;
			_s.ShowRamGb = true;
			ShowBlocks("cpu", "gpu", "vram", "ram", "claude", "battery");
		}));
		presets.Children.Add(Preset(L.T("Compact"), L.T("one line, no GB"), delegate
		{
			_s.Layout = 0;
			_s.TextSize = 1;
			_s.ShowRamGb = false;
			ShowBlocks("cpu", "gpu", "vram", "ram", "claude", "battery");
		}));
		presets.Children.Add(Preset(L.T("Minimal"), L.T("CPU and GPU only"), delegate
		{
			_s.Layout = 1;
			_s.TextSize = 1;
			ShowBlocks("cpu", "gpu");
		}));
		presets.Children.Add(Preset(L.T("Gaming"), L.T("large text, temperatures"), delegate
		{
			_s.Layout = 1;
			_s.TextSize = 2;
			ShowBlocks("cpu", "gpu", "claude");
		}));
		p.Children.Add(presets);
		StackPanel more = new StackPanel();
		more.Children.Add(Combo(L.T("Layout"), new string[2]
		{
			L.T("One line"),
			L.T("Two lines, vertical labels")
		}, () => _s.Layout, delegate(int v)
		{
			_s.Layout = v;
		}));
		more.Children.Add(Combo(L.T("Text size"), new string[3]
		{
			L.T("Small"),
			L.T("Normal"),
			L.T("Large")
		}, () => _s.TextSize, delegate(int v)
		{
			_s.TextSize = v;
		}));
		more.Children.Add(Combo(L.T("Normal color"), new string[2]
		{
			L.T("Green"),
			L.T("Blue")
		}, () => _s.BlueNormal ? 1 : 0, delegate(int v)
		{
			_s.BlueNormal = v == 1;
		}));
		int[] gaps = new int[7] { 6, 10, 14, 20, 26, 32, 40 };
		more.Children.Add(Combo(L.T("Spacing between blocks"), gaps.Select((int g) => $"{g} px").ToArray(), () => Math.Max(0, Array.IndexOf(gaps, _s.GroupGap)), delegate(int v)
		{
			_s.GroupGap = gaps[v];
		}));
		more.Children.Add(SliderRow(L.T("Spacing from tray"), 0.0, 100.0, 10.0, () => _s.TrayGap, delegate(int v)
		{
			_s.TrayGap = v;
		}, (int v) => $"{v} px"));
		int[] intervals = new int[4] { 500, 1000, 2000, 5000 };
		more.Children.Add(Combo(L.T("Update interval"), intervals.Select((int ms) => (ms < 1000) ? L.T("0.5 s") : L.F("{0} s", ms / 1000)).ToArray(), () => Math.Max(0, Array.IndexOf(intervals, _s.IntervalMs)), delegate(int v)
		{
			_s.IntervalMs = intervals[v];
		}));
		if (_host.Adapters.Count > 1)
		{
			List<GpuSensor.Adapter> adapters = _host.Adapters.ToList();
			string[] names = new string[1] { L.T("Auto (discrete)") }.Concat(adapters.Select((GpuSensor.Adapter a) => a.Name)).ToArray();
			more.Children.Add(Combo(L.T("Graphics card"), names, () => (_s.Gpu == "") ? 0 : (adapters.FindIndex((GpuSensor.Adapter a) => a.Key == _s.Gpu) + 1), delegate(int v)
			{
				_host.SelectGpu((v == 0) ? "" : adapters[v - 1].Key);
			}));
		}
		more.Children.Add(Check(L.T("Show memory amount (GB)"), () => _s.ShowRamGb, delegate(bool v)
		{
			_s.ShowRamGb = v;
		}));
		more.Children.Add(Check(L.T("Embed in the taskbar"), () => _s.Embed, delegate(bool v)
		{
			_s.Embed = v;
		}));
		more.Children.Add(Check(L.T("Hide in full-screen apps"), () => _s.HideFullscreen, delegate(bool v)
		{
			_s.HideFullscreen = v;
		}));
		more.Children.Add(Check(L.T("Show tray icon"), () => _s.TrayIcon, delegate(bool v)
		{
			_s.TrayIcon = v;
		}));
		more.Children.Add(Check(L.T("Start with Windows"), Autostart.IsEnabled, delegate(bool v)
		{
			if (v)
			{
				Autostart.Enable(Environment.ProcessPath);
			}
			else
			{
				Autostart.Disable();
			}
		}, apply: false));
		more.Children.Add(Combo("Language / Язык", L.Languages.Select(((string Code, string Name) l) => l.Name).ToArray(), () => Math.Max(0, Array.FindIndex(L.Languages, ((string Code, string Name) l) => l.Code == L.Code)), delegate(int v)
		{
			_s.Language = L.Languages[v].Code;
			L.Set(_s.Language);
			_host.Apply();
			int page = PageIndex;
			Dispatcher.BeginInvoke(delegate
			{
				_host.ReopenSettings(page);
			});
		}, apply: false));
		p.Children.Add(new Expander
		{
			Header = L.T("More settings"),
			Content = more,
			IsExpanded = false
		});
		return p;
	}

	private void ShowBlocks(params string[] blocks)
	{
		foreach (string b in Settings.AllBlocks)
		{
			_s.SetShown(b, blocks.Contains(b));
		}
	}

	private UIElement Preset(string title, string sub, Action apply)
	{
		StackPanel content = new StackPanel();
		content.Children.Add(new TextBlock
		{
			Text = title,
			FontWeight = FontWeights.SemiBold
		});
		TextBlock subText = new TextBlock
		{
			Text = sub,
			FontSize = 11.0,
			TextWrapping = TextWrapping.Wrap
		};
		subText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		content.Children.Add(subText);
		Button b = new Button
		{
			Content = content,
			Width = 140.0,
			Height = 58.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 8.0),
			HorizontalContentAlignment = HorizontalAlignment.Left,
			Padding = new Thickness(12.0, 6.0, 12.0, 6.0)
		};
		b.Click += delegate
		{
			apply();
			Changed();
		};
		return b;
	}

	private void UpdatePreview()
	{
		if (_preview == null)
		{
			return;
		}
		float scale = (float)VisualTreeHelper.GetDpi(this).DpiScaleX;
		int h = (int)MathF.Round(48f * scale);
		Readings r = _host.Readings;
		_previewRenderer.Configure(scale, h, _s, _host.Claude != null);
		int w = Math.Max(1, _previewRenderer.Width);
		using Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
		using (Graphics g = Graphics.FromImage(bmp))
		{
			g.Clear(System.Drawing.Color.Transparent);
			_previewRenderer.Draw(g, in r, _s, Palette.Create(_host.LightTaskbar, _s.BlueNormal));
		}
		BitmapData data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
		try
		{
			BitmapSource src = BitmapSource.Create(w, h, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * h, data.Stride);
			src.Freeze();
			_preview.Source = src;
		}
		finally
		{
			bmp.UnlockBits(data);
		}
	}

	// ---------- Блоки ----------

	private UIElement BlocksPage()
	{
		StackPanel p = Page(L.T("Blocks"));
		p.Children.Add(Note(L.T("Drag a block by its handle to change the order. Unticked blocks are hidden.")));
		ListBox list = new ListBox
		{
			AllowDrop = true,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		Fill();
		Point start = default(Point);
		string dragging = null;
		list.PreviewMouseMove += delegate(object _, MouseEventArgs e)
		{
			if (dragging != null && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(list) - start).Length > 4.0)
			{
				string key = dragging;
				dragging = null;
				DragDrop.DoDragDrop(list, key, DragDropEffects.Move);
			}
		};
		list.Drop += delegate(object _, DragEventArgs e)
		{
			if (e.Data.GetData(typeof(string)) is string key)
			{
				ListBoxItem target = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
				List<string> order = _s.Order().ToList();
				order.Remove(key);
				int index = (target?.Tag is string t) ? order.IndexOf(t) : order.Count;
				if (target != null && e.GetPosition(target).Y > target.ActualHeight / 2.0)
				{
					index++;
				}
				order.Insert(Math.Clamp(index, 0, order.Count), key);
				_s.BlockOrder = string.Join(",", order);
				Changed();
				Fill();
			}
		};
		p.Children.Add(list);
		return p;
		void Fill()
		{
			list.Items.Clear();
			foreach (string key2 in _s.Order())
			{
				string k = key2;
				TextBlock handle = new TextBlock
				{
					Text = "⠿",
					FontSize = 16.0,
					Cursor = Cursors.SizeAll,
					Margin = new Thickness(0.0, 0.0, 12.0, 0.0),
					VerticalAlignment = VerticalAlignment.Center
				};
				handle.PreviewMouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
				{
					start = e.GetPosition(list);
					dragging = k;
				};
				CheckBox check = new CheckBox
				{
					Content = BlockName(k),
					IsChecked = _s.IsShown(k)
				};
				check.Click += delegate
				{
					_s.SetShown(k, check.IsChecked == true);
					Changed();
				};
				StackPanel row = new StackPanel
				{
					Orientation = Orientation.Horizontal
				};
				row.Children.Add(handle);
				row.Children.Add(check);
				list.Items.Add(new ListBoxItem
				{
					Content = row,
					Tag = k
				});
			}
		}
	}

	private static string BlockName(string key)
	{
		return key switch
		{
			"cpu" => L.T("CPU — load and temperature"),
			"gpu" => L.T("GPU — load and temperature"),
			"vram" => L.T("VRAM — video memory"),
			"ram" => L.T("RAM — memory"),
			"claude" => L.T("Claude — 5-hour and 7-day limits"),
			"battery" => L.T("Keyboard and mouse battery"),
			_ => key,
		};
	}

	private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
	{
		while (d != null && d is not T)
		{
			d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
		}
		return d as T;
	}

	// ---------- Пороги ----------

	private UIElement ThresholdsPage()
	{
		StackPanel p = Page(L.T("Thresholds"));
		p.Children.Add(Note(L.T("Below yellow is normal, from red is overload.")));
		Grid g = new Grid
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(110.0)
		});
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(110.0)
		});
		AddHeader(L.T("Yellow from"), L.T("Red from"));
		Row(L.T("CPU load, %"), () => _s.CpuLoad, delegate(Limit l)
		{
			_s.CpuLoad = l;
		}, low: false);
		Row(L.T("CPU temperature, °C"), () => _s.CpuTemp, delegate(Limit l)
		{
			_s.CpuTemp = l;
		}, low: false);
		Row(L.T("GPU load, %"), () => _s.GpuLoad, delegate(Limit l)
		{
			_s.GpuLoad = l;
		}, low: false);
		Row(L.T("GPU temperature, °C"), () => _s.GpuTemp, delegate(Limit l)
		{
			_s.GpuTemp = l;
		}, low: false);
		Row(L.T("VRAM load, %"), () => _s.Vram, delegate(Limit l)
		{
			_s.Vram = l;
		}, low: false);
		Row(L.T("RAM load, %"), () => _s.Ram, delegate(Limit l)
		{
			_s.Ram = l;
		}, low: false);
		Row(L.T("Claude limits, %"), () => _s.Claude, delegate(Limit l)
		{
			_s.Claude = l;
		}, low: false);
		AddHeader(L.T("Yellow at or below"), L.T("Red at or below"));
		Row(L.T("Keyboard charge, %"), () => _s.KbBattery, delegate(Limit l)
		{
			_s.KbBattery = l;
		}, low: true);
		Row(L.T("Mouse charge, %"), () => _s.MouseBattery, delegate(Limit l)
		{
			_s.MouseBattery = l;
		}, low: true);
		p.Children.Add(g);
		Button defaults = new Button
		{
			Content = L.T("Defaults"),
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0),
			HorizontalAlignment = HorizontalAlignment.Left
		};
		defaults.Click += delegate
		{
			_s.CpuLoad = Settings.Defaults.CpuLoad;
			_s.CpuTemp = Settings.Defaults.CpuTemp;
			_s.GpuLoad = Settings.Defaults.GpuLoad;
			_s.GpuTemp = Settings.Defaults.GpuTemp;
			_s.Vram = Settings.Defaults.Vram;
			_s.Ram = Settings.Defaults.Ram;
			_s.Claude = Settings.Defaults.Claude;
			_s.KbBattery = Settings.Defaults.KbBattery;
			_s.MouseBattery = Settings.Defaults.MouseBattery;
			Changed();
			ShowPage(_nav.SelectedIndex);
		};
		p.Children.Add(defaults);
		return p;
		void AddHeader(string a, string b)
		{
			int row = g.RowDefinitions.Count;
			g.RowDefinitions.Add(new RowDefinition());
			TextBlock ta = Caption(a);
			TextBlock tb = Caption(b);
			ta.Margin = (tb.Margin = new Thickness(0.0, (row == 0) ? 0.0 : 14.0, 0.0, 4.0));
			Grid.SetRow(ta, row);
			Grid.SetColumn(ta, 1);
			Grid.SetRow(tb, row);
			Grid.SetColumn(tb, 2);
			g.Children.Add(ta);
			g.Children.Add(tb);
		}
		void Row(string label, Func<Limit> get, Action<Limit> set, bool low)
		{
			int row2 = g.RowDefinitions.Count;
			g.RowDefinitions.Add(new RowDefinition());
			TextBlock name = new TextBlock
			{
				Text = label,
				VerticalAlignment = VerticalAlignment.Center
			};
			TextBox warn = new TextBox
			{
				Text = get().Warn.ToString(),
				Margin = new Thickness(0.0, 3.0, 10.0, 3.0)
			};
			TextBox hot = new TextBox
			{
				Text = get().Hot.ToString(),
				Margin = new Thickness(0.0, 3.0, 10.0, 3.0)
			};
			Brush normal = warn.BorderBrush;
			warn.TextChanged += delegate
			{
				Validate();
			};
			hot.TextChanged += delegate
			{
				Validate();
			};
			Grid.SetRow(name, row2);
			Grid.SetRow(warn, row2);
			Grid.SetColumn(warn, 1);
			Grid.SetRow(hot, row2);
			Grid.SetColumn(hot, 2);
			g.Children.Add(name);
			g.Children.Add(warn);
			g.Children.Add(hot);
			void Validate()
			{
				bool ok = int.TryParse(warn.Text, out int w) && int.TryParse(hot.Text, out int h) && w >= 0 && h >= 0 && w <= 200 && h <= 200 && (low ? (h < w) : (w < h));
				warn.BorderBrush = (hot.BorderBrush = (ok ? normal : Brushes.IndianRed));
				if (ok)
				{
					set(new Limit(int.Parse(warn.Text), int.Parse(hot.Text)));
					Changed();
				}
			}
		}
	}

	// ---------- Клавиатура и мышь ----------

	private UIElement KeyboardPage()
	{
		StackPanel p = Page(L.T("Keyboard"));
		p.Children.Add(Caption(L.T("Windows keyboard settings (all keyboards)")));
		p.Children.Add(SliderRow(L.T("Repeat delay"), 0.0, 3.0, 1.0, () => KeyboardSys.RepeatDelay, delegate(int v)
		{
			KeyboardSys.RepeatDelay = v;
		}, (int v) => $"{250 * (v + 1)} ms", apply: false));
		p.Children.Add(SliderRow(L.T("Repeat rate"), 0.0, 31.0, 1.0, () => KeyboardSys.RepeatRate, delegate(int v)
		{
			KeyboardSys.RepeatRate = v;
		}, (int v) => L.F("{0} per second", (int)Math.Round(2.5 + v * 27.5 / 31.0)), apply: false));
		p.Children.Add(SliderRow(L.T("Cursor blink rate"), 0.0, 1200.0, 100.0, () => KeyboardSys.CaretBlinkMs, delegate(int v)
		{
			KeyboardSys.CaretBlinkMs = v;
		}, (int v) => (v == 0) ? L.T("no blinking") : $"{v} ms", apply: false));
		p.Children.Add(Caption(L.T("Try it here")));
		p.Children.Add(new TextBox
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		});
		p.Children.Add(LogitechButton());
		return p;
	}

	private UIElement MousePage()
	{
		StackPanel p = Page(L.T("Mouse"));
		p.Children.Add(Caption(L.T("Windows mouse settings (all mice)")));
		p.Children.Add(SliderRow(L.T("Pointer speed"), 1.0, 20.0, 1.0, () => MouseSys.Speed, delegate(int v)
		{
			MouseSys.Speed = v;
		}, (int v) => v.ToString(), apply: false));
		p.Children.Add(Check(L.T("Enhance pointer precision (acceleration)"), () => MouseSys.Acceleration, delegate(bool v)
		{
			MouseSys.Acceleration = v;
		}, apply: false));
		p.Children.Add(SliderRow(L.T("Double-click speed"), 200.0, 900.0, 50.0, () => MouseSys.DoubleClickMs, delegate(int v)
		{
			MouseSys.DoubleClickMs = v;
		}, (int v) => $"{v} ms", apply: false));
		p.Children.Add(SliderRow(L.T("Wheel scrolls lines"), 1.0, 20.0, 1.0, () => MouseSys.WheelLines, delegate(int v)
		{
			MouseSys.WheelLines = v;
		}, (int v) => v.ToString(), apply: false));
		p.Children.Add(Check(L.T("Swap left and right buttons"), () => MouseSys.Swapped, delegate(bool v)
		{
			MouseSys.Swapped = v;
		}, apply: false));
		p.Children.Add(LogitechButton());
		return p;
	}

	private UIElement LogitechButton()
	{
		StackPanel p = new StackPanel
		{
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0)
		};
		p.Children.Add(Note(L.T("Logitech G keys, macros, lighting and battery are in the Logitech window until version 2.2.")));
		Button b = new Button
		{
			Content = L.T("Logitech…"),
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		b.Click += delegate
		{
			_host.OpenLogitech();
		};
		p.Children.Add(b);
		return p;
	}

	// ---------- Claude ----------

	private UIElement ClaudePage()
	{
		StackPanel p = Page("Claude");
		p.Children.Add(Check(L.T("Show Claude limits"), () => _s.ShowClaude, delegate(bool v)
		{
			_s.ShowClaude = v;
		}));
		TextBlock status = new TextBlock
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 12.0),
			TextWrapping = TextWrapping.Wrap,
			LineHeight = 22.0
		};
		p.Children.Add(status);
		p.Children.Add(SliderRow(L.T("Ask claude.ai every"), 1.0, 15.0, 1.0, () => _s.ClaudeIntervalMin, delegate(int v)
		{
			_s.ClaudeIntervalMin = v;
		}, (int v) => L.F("{0} min", v)));
		p.Children.Add(Note(L.T("Usage comes from the Claude app history (about every 15 min, no limits) and from claude.ai with your Claude Code sign-in. claude.ai rejects frequent requests, so PulseBar asks it rarely and waits as long as it is told. The token is only read, never refreshed; no quota is spent.")));
		_refresh = Refresh;
		Refresh();
		return p;
		void Refresh()
		{
			ClaudeUsage.Snapshot c = _host.Claude;
			if (c == null)
			{
				status.Text = L.T("No data: Claude Code is not signed in on this PC.");
				return;
			}
			int ago = (int)Math.Max(0.0, (DateTimeOffset.Now - c.Updated).TotalMinutes);
			status.Text = L.T("5 hours") + ": " + Pct(c.FiveHour) + Reset(c.FiveHourReset) + "\n" + L.T("7 days") + ": " + Pct(c.SevenDay) + Reset(c.SevenDayReset) + "\n" + L.T("Updated") + ": " + L.F("{0} min ago", ago) + " (" + ((c.Source == "api") ? "claude.ai" : L.T("Claude app")) + ")" + (c.Stale ? (" — " + L.T("out of date")) : "") + ((_host.ClaudeApiStatus.Length > 0) ? ("\nclaude.ai: " + _host.ClaudeApiStatus) : "");
		}
	}

	private static string Pct(float v)
	{
		if (!float.IsNaN(v))
		{
			return $"{v:0}%";
		}
		return "—";
	}

	private static string Reset(DateTimeOffset? at)
	{
		if (at.HasValue && at.Value > DateTimeOffset.Now)
		{
			return " · " + L.F("reset at {0}", at.Value.ToLocalTime().ToString("ddd HH:mm"));
		}
		return "";
	}

	// ---------- О программе ----------

	private UIElement AboutPage()
	{
		StackPanel p = Page("PulseBar " + Updater.Current);
		TextBlock status = new TextBlock
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0),
			TextWrapping = TextWrapping.Wrap
		};
		p.Children.Add(status);
		Button action = new Button
		{
			HorizontalAlignment = HorizontalAlignment.Left
		};
		action.Click += delegate
		{
			if (_host.Updater.Available != null)
			{
				_host.InstallUpdate();
			}
			else
			{
				_host.CheckForUpdates();
			}
		};
		p.Children.Add(action);
		p.Children.Add(Check(L.T("Get beta versions"), () => _s.Beta, delegate(bool v)
		{
			_s.Beta = v;
			_host.CheckForUpdates();
		}));
		p.Children.Add(Link("github.com/CajuTheOne/PulseBar", "https://github.com/CajuTheOne/PulseBar"));
		p.Children.Add(Link(L.T("Open the log folder"), Path.GetDirectoryName(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", "x"))!));
		TextBlock license = Note(L.T("MIT License. CPU temperature: PawnIO driver and its modules (LGPL-2.1)."));
		license.Margin = new Thickness(0.0, 16.0, 0.0, 0.0);
		p.Children.Add(license);
		_refresh = Refresh;
		Refresh();
		return p;
		void Refresh()
		{
			Updater.Release r = _host.Updater.Available;
			status.Text = (r != null) ? L.F("Version {0} is available.", r.Version) : L.T("You have the latest version.");
			action.Content = (r != null) ? (_host.Updater.Busy ? L.F("Downloading {0}…", r.Version) : L.F("Update to {0}", r.Version)) : L.T("Check for updates");
			action.IsEnabled = !_host.Updater.Busy;
		}
	}

	private static UIElement Link(string text, string target)
	{
		Hyperlink h = new Hyperlink(new Run(text));
		h.Click += delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(target)
				{
					UseShellExecute = true
				})?.Dispose();
			}
			catch (Exception ex)
			{
				Log.Write("link: " + ex.Message);
			}
		};
		return new TextBlock(h)
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		};
	}

	// ---------- Строительные блоки ----------

	private static StackPanel Page(string title)
	{
		StackPanel p = new StackPanel
		{
			MaxWidth = 640.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			MinWidth = 420.0
		};
		p.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 24.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		});
		return p;
	}

	private static TextBlock Caption(string text)
	{
		TextBlock t = new TextBlock
		{
			Text = text,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		return t;
	}

	private static TextBlock Note(string text)
	{
		TextBlock t = new TextBlock
		{
			Text = text,
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12.0
		};
		t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		return t;
	}

	private UIElement Check(string text, Func<bool> get, Action<bool> set, bool apply = true)
	{
		CheckBox c = new CheckBox
		{
			Content = text,
			IsChecked = get(),
			Margin = new Thickness(0.0, 4.0, 0.0, 4.0)
		};
		c.Click += delegate
		{
			set(c.IsChecked == true);
			if (apply)
			{
				Changed();
			}
		};
		return c;
	}

	private UIElement Combo(string label, string[] items, Func<int> get, Action<int> set, bool apply = true)
	{
		ComboBox c = new ComboBox
		{
			ItemsSource = items,
			SelectedIndex = Math.Clamp(get(), 0, items.Length - 1),
			MinWidth = 220.0
		};
		c.SelectionChanged += delegate
		{
			if (c.SelectedIndex >= 0)
			{
				set(c.SelectedIndex);
				if (apply)
				{
					Changed();
				}
			}
		};
		return LabeledRow(label, c);
	}

	private UIElement SliderRow(string label, double min, double max, double step, Func<int> get, Action<int> set, Func<int, string> format, bool apply = true)
	{
		Slider s = new Slider
		{
			Minimum = min,
			Maximum = max,
			TickFrequency = step,
			IsSnapToTickEnabled = true,
			Value = Math.Clamp(get(), (int)min, (int)max),
			Width = 220.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock value = new TextBlock
		{
			Text = format((int)s.Value),
			Width = 120.0,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		s.ValueChanged += delegate
		{
			int v = (int)Math.Round(s.Value);
			value.Text = format(v);
			set(v);
			if (apply)
			{
				Changed();
			}
		};
		StackPanel row = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		row.Children.Add(s);
		row.Children.Add(value);
		return LabeledRow(label, row);
	}

	private static UIElement LabeledRow(string label, UIElement control)
	{
		Grid g = new Grid
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 4.0)
		};
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(200.0)
		});
		g.ColumnDefinitions.Add(new ColumnDefinition());
		TextBlock t = new TextBlock
		{
			Text = label,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 12.0, 0.0)
		};
		g.Children.Add(t);
		Grid.SetColumn(control, 1);
		g.Children.Add(control);
		return g;
	}
}
