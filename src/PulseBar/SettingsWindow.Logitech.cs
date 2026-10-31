#pragma warning disable WPF0001
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PulseBar.Logi;
using Color = System.Windows.Media.Color;

namespace PulseBar;

// Вкладка Logitech (2.3): состояние клавиатуры, профили M1–M3 (подсветка, цвета клавиш, G1–G5 и MR, программы),
// приглушение, память клавиатуры. Профили сохраняются в logitech.json, строка их перечитывает (клавиатурой владеет она):
// цвета клавиш — через 0,2 с, чтобы кисть сразу было видно на клавиатуре, остальное — через 0,5 с.
internal sealed partial class SettingsWindow
{
	private static readonly string[] EffectIds = new string[5] { "off", "static", "breathing", "cycle", "wave" };

	private LogiConfig? _logi;

	private int _logiProfile;

	private DispatcherTimer? _logiSave;

	private Rgb _brush = new Rgb(0xFF, 0x80, 0x00);

	private readonly List<Rgb> _recent = new List<Rgb>();

	private void LogiChanged(int delayMs = 500)
	{
		if (_logiSave == null)
		{
			_logiSave = new DispatcherTimer();
			_logiSave.Tick += delegate
			{
				_logiSave.Stop();
				_logi?.Save();
				_host.LogiReload();
			};
		}
		_logiSave.Stop();
		_logiSave.Interval = TimeSpan.FromMilliseconds(delayMs);
		_logiSave.Start();
	}

	private static string EffectName(string id)
	{
		return id switch
		{
			"off" => L.T("Off"),
			"static" => L.T("Color"),
			"breathing" => L.T("Breathing"),
			"cycle" => L.T("Color cycle"),
			"wave" => L.T("Wave"),
			_ => id
		};
	}

	private UIElement LogitechPage()
	{
		_logi ??= LogiConfig.Load();
		LogiConfig c = _logi;
		ISettingsHost.Logitech st = _host.LogiStatus;
		StackPanel p = Page("Logitech");
		TextBlock status = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			LineHeight = 20.0
		};
		p.Children.Add(status);
		p.Children.Add(Check(L.T("Control Logitech devices (instead of G HUB)"), () => c.Enabled, delegate(bool v)
		{
			c.Enabled = v;
			LogiChanged();
		}, apply: false));
		p.Children.Add(Note(L.T("While G HUB is running PulseBar only reads the battery. Close G HUB (and turn off its autostart) to let PulseBar control the keyboard.")));
		_logiProfile = Math.Clamp(_logiProfile, 0, c.Profiles.Count - 1);
		StackPanel profilePanel = new StackPanel();
		string[] names = c.Profiles.Select((Profile pr, int i) => $"M{i + 1} · {pr.Name}").ToArray();
		p.Children.Add(Combo(L.T("Profile"), names, () => _logiProfile, delegate(int v)
		{
			_logiProfile = v;
			profilePanel.Children.Clear();
			profilePanel.Children.Add(LogiProfileEditor(c, c.Profiles[v], st));
		}, apply: false));
		profilePanel.Children.Add(LogiProfileEditor(c, c.Profiles[_logiProfile], st));
		p.Children.Add(profilePanel);
		p.Children.Add(Caption(L.T("Dim when idle")));
		p.Children.Add(SliderRow(L.T("Dim after"), 0.0, 30.0, 1.0, () => c.IdleMinutes, delegate(int v)
		{
			c.IdleMinutes = v;
			LogiChanged();
		}, (int v) => (v == 0) ? L.T("never") : L.F("{0} min", v), apply: false));
		p.Children.Add(SliderRow(L.T("Dimmed brightness"), 0.0, 100.0, 1.0, () => c.IdleBrightness, delegate(int v)
		{
			c.IdleBrightness = v;
			LogiChanged();
		}, (int v) => $"{v}%", apply: false));
		p.Children.Add(Note(L.T("Any key press brings the light back. The light goes off while the screen is off, the PC is locked or asleep. Below 15% battery the brightness is at most 30%.")));
		p.Children.Add(Caption(L.T("Keyboard memory")));
		p.Children.Add(Note(L.T("PulseBar keeps the keyboard memory in step with the profiles by itself: colours and effects of the zones (for a profile with key colours — its base colour), dimming, G-keys with one key combination or a media key. They work when PulseBar is not running. A full backup is made before the first write.")));
		WrapPanel buttons = new WrapPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		Button factory = new Button
		{
			Content = L.T("Restore factory profiles"),
			Margin = new Thickness(0.0, 0.0, 8.0, 8.0)
		};
		factory.Click += delegate
		{
			if (MessageBox.Show(this, L.T("Replace the three profiles in the keyboard with the factory ones? A backup is made first. The next change of the profiles writes them again."), "PulseBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
			{
				_host.LogiRestore();
			}
		};
		Button backup = new Button
		{
			Content = L.T("Back up keyboard memory"),
			Margin = new Thickness(0.0, 0.0, 8.0, 8.0)
		};
		backup.Click += delegate
		{
			_host.LogiBackup();
		};
		buttons.Children.Add(factory);
		buttons.Children.Add(backup);
		p.Children.Add(buttons);
		_refresh = Refresh;
		Refresh();
		return p;
		void Refresh()
		{
			ISettingsHost.Logitech s = _host.LogiStatus;
			string device = (s.Device.Length > 0) ? (s.Device + ": " + (s.Online ? ((s.Battery >= 0) ? $"{L.T("battery")} {s.Battery}%" : L.T("connected")) : L.T("asleep or off"))) : L.T("No Logitech G keyboard found");
			string mode = s.Mode switch
			{
				"ghub" => L.T("G HUB is running — PulseBar does not touch the keyboard"),
				"off" => L.T("Logitech control is off"),
				"host" => L.T("PulseBar controls the keyboard"),
				"starting" => L.T("Connecting…"),
				_ => ""
			};
			status.Text = device + ((mode.Length > 0) ? ("\n" + mode) : "") + ((s.Result.Length > 0) ? ("\n" + s.Result) : "");
			factory.IsEnabled = backup.IsEnabled = s.Mode == "host";
		}
	}

	private UIElement LogiProfileEditor(LogiConfig c, Profile pr, ISettingsHost.Logitech st)
	{
		StackPanel p = new StackPanel();
		TextBox name = new TextBox
		{
			Text = pr.Name,
			MinWidth = 220.0
		};
		name.TextChanged += delegate
		{
			pr.Name = name.Text.Trim();
			LogiChanged();
		};
		p.Children.Add(LabeledRow(L.T("Name"), name));
		// Клавиши: эффекты, которые есть у клавиатуры, и «По клавишам».
		string[] keyEffects = (st.KeyEffects.Length > 0) ? st.KeyEffects : EffectIds;
		StackPanel keysPanel = new StackPanel();
		p.Children.Add(LightingRow(L.T("Keys"), pr.Keyboard, keyEffects, perKey: true, pr, delegate
		{
			keysPanel.Children.Clear();
			if (pr.PerKey)
			{
				keysPanel.Children.Add(KeyEditor(c, pr, st.KeyIds));
			}
		}));
		p.Children.Add(keysPanel);
		if (pr.PerKey)
		{
			keysPanel.Children.Add(KeyEditor(c, pr, st.KeyIds));
		}
		p.Children.Add(LightingRow(L.T("Logo"), pr.Logo, (st.LogoEffects.Length > 0) ? st.LogoEffects : EffectIds, perKey: false, pr, null));
		p.Children.Add(SliderRow(L.T("Brightness"), 1.0, 100.0, 1.0, () => pr.Brightness, delegate(int v)
		{
			pr.Brightness = v;
			LogiChanged(200);
		}, (int v) => $"{v}%", apply: false));
		p.Children.Add(Caption(L.T("G-keys")));
		string[] types = new string[5] { "none", "keys", "text", "run", "macro" };
		string[] typeNames = new string[5]
		{
			L.T("Nothing"),
			L.T("Key combination"),
			L.T("Type text"),
			L.T("Run program / open"),
			L.T("Macro")
		};
		foreach (string key in LogiConfig.KeyNames)
		{
			KeyAction a = pr.Key(key);
			ComboBox type = new ComboBox
			{
				ItemsSource = typeNames,
				SelectedIndex = Math.Max(0, Array.IndexOf(types, a.Type)),
				Width = 170.0,
				Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
			};
			TextBox value = new TextBox
			{
				Text = a.Value,
				Width = 240.0
			};
			TextBlock where = new TextBlock
			{
				Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center,
				FontSize = 11.0
			};
			where.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
			type.SelectionChanged += delegate
			{
				a.Type = types[Math.Max(0, type.SelectedIndex)];
				Where();
				LogiChanged();
			};
			value.TextChanged += delegate
			{
				a.Value = value.Text;
				Where();
				LogiChanged();
			};
			StackPanel row = new StackPanel
			{
				Orientation = Orientation.Horizontal
			};
			row.Children.Add(type);
			row.Children.Add(value);
			row.Children.Add(where);
			p.Children.Add(LabeledRow(key, row));
			Where();
			void Where()
			{
				// MR в памяти клавиатуры не хранится: без PulseBar не работает.
				bool hostOnly = key == "MR" || OnboardPlan.HostOnly(a);
				where.Text = (a.Type == "none" || string.IsNullOrWhiteSpace(a.Value)) ? "" : (hostOnly ? L.T("with PulseBar") : L.T("also without PulseBar"));
			}
		}
		p.Children.Add(Note(L.T("Key combination: e.g. Ctrl+Shift+F5 or PlayPause. Macro: steps separated by ';' — wait 100; text Hello; Enter; run notepad.")));
		TextBox apps = new TextBox
		{
			Text = pr.Apps,
			MinWidth = 220.0
		};
		apps.TextChanged += delegate
		{
			pr.Apps = apps.Text;
			LogiChanged();
		};
		p.Children.Add(LabeledRow(L.T("Switch automatically for programs:"), apps));
		p.Children.Add(Note(L.T("For example: game.exe, obs64. When the program is in front this profile turns on; when it is not — the profile chosen last with M1–M3.")));
		return p;
	}

	// Подсветка зоны: эффект (для клавиш ещё «По клавишам»), цвет #RRGGBB и период для дыхания и перелива.
	private UIElement LightingRow(string label, Lighting l, string[] effects, bool perKey, Profile pr, Action? perKeyChanged)
	{
		List<string> ids = effects.Where(e => EffectIds.Contains(e)).ToList();
		List<string> items = ids.Select(EffectName).ToList();
		if (perKey)
		{
			items.Add(L.T("By key"));
		}
		int Selected()
		{
			if (perKey && pr.PerKey)
			{
				return items.Count - 1;
			}
			int i = ids.IndexOf(l.Effect);
			return (i >= 0) ? i : Math.Max(0, ids.IndexOf("static"));
		}
		ComboBox effect = new ComboBox
		{
			ItemsSource = items,
			SelectedIndex = Selected(),
			Width = 170.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		TextBox color = new TextBox
		{
			Text = l.Color,
			Width = 90.0
		};
		Border swatch = new Border
		{
			Width = 24.0,
			Height = 24.0,
			CornerRadius = new CornerRadius(4.0),
			Margin = new Thickness(8.0, 0.0, 8.0, 0.0),
			BorderThickness = new Thickness(1.0),
			BorderBrush = Brushes.Gray
		};
		Button pick = new Button
		{
			Content = "…",
			MinWidth = 36.0
		};
		StackPanel colorRow = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		colorRow.Children.Add(color);
		colorRow.Children.Add(swatch);
		colorRow.Children.Add(pick);
		Slider period = new Slider
		{
			Minimum = 1.0,
			Maximum = 20.0,
			TickFrequency = 0.5,
			IsSnapToTickEnabled = true,
			Width = 140.0,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			ToolTip = L.T("Effect period, seconds")
		};
		TextBlock periodText = new TextBlock
		{
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Width = 44.0
		};
		bool loading = true;
		void Visible()
		{
			string e = (perKey && pr.PerKey) ? "static" : l.Effect;
			colorRow.Visibility = (e == "static" || e == "breathing") ? Visibility.Visible : Visibility.Collapsed;
			period.Visibility = periodText.Visibility = (e == "breathing" || e == "cycle") ? Visibility.Visible : Visibility.Collapsed;
			int ms = (l.Period > 0) ? l.Period : ((e == "cycle") ? OnboardPlan.DefaultCyclePeriod : OnboardPlan.DefaultBreathePeriod);
			loading = true;
			period.Value = ms / 1000.0;
			periodText.Text = $"{ms / 1000.0:0.#} s";
			loading = false;
		}
		effect.SelectionChanged += delegate
		{
			int i = Math.Max(0, effect.SelectedIndex);
			bool wasPerKey = pr.PerKey;
			if (perKey && i == items.Count - 1)
			{
				pr.PerKey = true;
				l.Effect = "static";
			}
			else
			{
				if (perKey)
				{
					pr.PerKey = false;
				}
				l.Effect = ids[i];
			}
			Visible();
			if (perKey && wasPerKey != pr.PerKey)
			{
				perKeyChanged?.Invoke();
			}
			LogiChanged(200);
		};
		color.TextChanged += delegate
		{
			Rgb v = Rgb.Parse(color.Text, new Rgb(1, 2, 3));
			if (v != new Rgb(1, 2, 3))
			{
				l.Color = v.ToString();
				Paint();
				_keyEditorRefresh?.Invoke();
				LogiChanged(200);
			}
		};
		pick.Click += delegate
		{
			if (PickColor(Rgb.Parse(l.Color), out Rgb v))
			{
				color.Text = v.ToString();
			}
		};
		period.ValueChanged += delegate
		{
			if (!loading)
			{
				l.Period = (int)Math.Round(period.Value * 1000.0);
				periodText.Text = $"{period.Value:0.#} s";
				LogiChanged();
			}
		};
		Paint();
		Visible();
		StackPanel row = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		row.Children.Add(effect);
		row.Children.Add(colorRow);
		row.Children.Add(period);
		row.Children.Add(periodText);
		return LabeledRow(label, row);
		void Paint()
		{
			swatch.Background = Brush(Rgb.Parse(l.Color));
		}
	}

	private static SolidColorBrush Brush(Rgb c)
	{
		return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
	}

	private static bool PickColor(Rgb start, out Rgb result)
	{
		using System.Windows.Forms.ColorDialog d = new System.Windows.Forms.ColorDialog
		{
			Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B),
			FullOpen = true
		};
		if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			result = new Rgb(d.Color.R, d.Color.G, d.Color.B);
			return true;
		}
		result = start;
		return false;
	}

	private Action? _keyEditorRefresh;

	// Редактор цветов клавиш: кисть — левая кнопка (можно вести по клавишам), правая — вернуть базовый цвет.
	private UIElement KeyEditor(LogiConfig c, Profile pr, byte[] keyIds)
	{
		const double U = 34.0;
		KeyDef[] layout = KeyLayout.For(keyIds);
		StackPanel p = new StackPanel
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 8.0)
		};
		// Панель кисти: текущий цвет, выбор цвета, последние цвета.
		WrapPanel tools = new WrapPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		Border brush = new Border
		{
			Width = 28.0,
			Height = 28.0,
			CornerRadius = new CornerRadius(4.0),
			BorderThickness = new Thickness(2.0),
			BorderBrush = Brushes.Gray,
			Background = Brush(_brush),
			ToolTip = L.T("Brush colour"),
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
		};
		Button pick = new Button
		{
			Content = L.T("Colour…"),
			Margin = new Thickness(0.0, 0.0, 12.0, 0.0)
		};
		WrapPanel recent = new WrapPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		tools.Children.Add(brush);
		tools.Children.Add(pick);
		tools.Children.Add(recent);
		p.Children.Add(tools);
		Canvas canvas = new Canvas
		{
			Width = 24.0 * U,
			Height = 7.5 * U,
			Background = Brushes.Transparent
		};
		Dictionary<byte, Border> keys = new Dictionary<byte, Border>();
		foreach (KeyDef k in layout)
		{
			TextBlock label = new TextBlock
			{
				Text = k.Label,
				FontSize = 10.0,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				IsHitTestVisible = false
			};
			Border b = new Border
			{
				Width = k.W * U - 3.0,
				Height = k.H * U - 3.0,
				CornerRadius = new CornerRadius(4.0),
				BorderThickness = new Thickness(1.0),
				BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
				Child = label,
				Tag = k.Id
			};
			Canvas.SetLeft(b, k.X * U);
			Canvas.SetTop(b, (k.Y + 1.25) * U);
			canvas.Children.Add(b);
			keys[k.Id] = b;
		}
		p.Children.Add(new Viewbox
		{
			Child = canvas,
			Stretch = Stretch.Uniform,
			StretchDirection = StretchDirection.DownOnly,
			HorizontalAlignment = HorizontalAlignment.Left
		});
		// Быстрые группы, сброс, копирование из другого профиля.
		WrapPanel groups = new WrapPanel
		{
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		void GroupButton(string text, Action click)
		{
			Button b = new Button
			{
				Content = text,
				Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
				Padding = new Thickness(8.0, 2.0, 8.0, 2.0)
			};
			b.Click += delegate
			{
				click();
			};
			groups.Children.Add(b);
		}
		GroupButton(L.T("All keys"), delegate
		{
			PaintMany(keys.Keys);
		});
		foreach (var (gname, gids) in KeyLayout.Groups)
		{
			byte[] ids = gids;
			GroupButton(L.T(gname), delegate
			{
				PaintMany(ids.Where(keys.ContainsKey));
			});
		}
		GroupButton(L.T("Clear key colours"), delegate
		{
			pr.KeyColors.Clear();
			Refresh();
			LogiChanged(200);
		});
		p.Children.Add(groups);
		WrapPanel copyRow = new WrapPanel();
		int self = c.Profiles.IndexOf(pr);
		foreach (int i in Enumerable.Range(0, c.Profiles.Count).Where(i => i != self))
		{
			Profile from = c.Profiles[i];
			Button b = new Button
			{
				Content = L.F("Copy colours from M{0}", i + 1),
				Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
				Padding = new Thickness(8.0, 2.0, 8.0, 2.0)
			};
			b.Click += delegate
			{
				pr.KeyColors = new Dictionary<string, string>(from.KeyColors);
				pr.Keyboard.Color = from.Keyboard.Color;
				Refresh();
				LogiChanged(200);
			};
			copyRow.Children.Add(b);
		}
		p.Children.Add(copyRow);
		p.Children.Add(Note(L.T("Left button paints (you can drag across keys), right button returns the base colour. Changes are visible on the keyboard at once.")));
		bool painting = false;
		bool erase = false;
		canvas.MouseLeftButtonDown += (object s, MouseButtonEventArgs e) => Begin(e, eraseMode: false);
		canvas.MouseRightButtonDown += (object s, MouseButtonEventArgs e) => Begin(e, eraseMode: true);
		canvas.MouseMove += (object s, MouseEventArgs e) =>
		{
			if (painting)
			{
				PaintAt(e.GetPosition(canvas));
			}
		};
		canvas.MouseLeftButtonUp += delegate
		{
			End();
		};
		canvas.MouseRightButtonUp += delegate
		{
			End();
		};
		canvas.LostMouseCapture += delegate
		{
			painting = false;
		};
		pick.Click += delegate
		{
			if (PickColor(_brush, out Rgb v))
			{
				SetBrush(v);
			}
		};
		_keyEditorRefresh = Refresh;
		Refresh();
		Recent();
		return p;
		void Begin(MouseButtonEventArgs e, bool eraseMode)
		{
			painting = true;
			erase = eraseMode;
			canvas.CaptureMouse();
			if (!erase)
			{
				Remember(_brush);
			}
			PaintAt(e.GetPosition(canvas));
			e.Handled = true;
		}
		void End()
		{
			painting = false;
			canvas.ReleaseMouseCapture();
		}
		void PaintAt(Point pt)
		{
			foreach (KeyDef k in layout)
			{
				if (pt.X >= k.X * U && pt.X < (k.X + k.W) * U && pt.Y >= (k.Y + 1.25) * U && pt.Y < (k.Y + 1.25 + k.H) * U)
				{
					string id = k.Id.ToString("X2");
					if (erase)
					{
						if (!pr.KeyColors.Remove(id))
						{
							return;
						}
					}
					else
					{
						if (pr.KeyColors.TryGetValue(id, out string? was) && was == _brush.ToString())
						{
							return;
						}
						pr.KeyColors[id] = _brush.ToString();
					}
					Show(k.Id);
					LogiChanged(200);
					return;
				}
			}
		}
		void PaintMany(IEnumerable<byte> ids)
		{
			Remember(_brush);
			foreach (byte id in ids)
			{
				pr.KeyColors[id.ToString("X2")] = _brush.ToString();
				Show(id);
			}
			LogiChanged(200);
		}
		void Show(byte id)
		{
			if (!keys.TryGetValue(id, out Border? b))
			{
				return;
			}
			Rgb col = pr.KeyColors.TryGetValue(id.ToString("X2"), out string? v) ? Rgb.Parse(v) : Rgb.Parse(pr.Keyboard.Color);
			b.Background = Brush(col);
			((TextBlock)b.Child).Foreground = (col.R * 299 + col.G * 587 + col.B * 114 > 140000) ? Brushes.Black : Brushes.White;
		}
		void Refresh()
		{
			foreach (byte id in keys.Keys)
			{
				Show(id);
			}
		}
		void SetBrush(Rgb v)
		{
			_brush = v;
			brush.Background = Brush(v);
			Remember(v);
		}
		void Remember(Rgb v)
		{
			_recent.Remove(v);
			_recent.Insert(0, v);
			if (_recent.Count > 10)
			{
				_recent.RemoveAt(_recent.Count - 1);
			}
			Recent();
		}
		void Recent()
		{
			recent.Children.Clear();
			foreach (Rgb r in _recent)
			{
				Border sw = new Border
				{
					Width = 20.0,
					Height = 20.0,
					CornerRadius = new CornerRadius(3.0),
					Margin = new Thickness(0.0, 0.0, 4.0, 0.0),
					BorderThickness = new Thickness(1.0),
					BorderBrush = Brushes.Gray,
					Background = Brush(r),
					ToolTip = r.ToString(),
					Cursor = Cursors.Hand
				};
				Rgb chosen = r;
				sw.MouseLeftButtonDown += delegate
				{
					_brush = chosen;
					brush.Background = Brush(chosen);
				};
				recent.Children.Add(sw);
			}
		}
	}
}
