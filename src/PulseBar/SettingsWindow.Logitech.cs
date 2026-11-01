#pragma warning disable WPF0001
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PulseBar.Logi;
using Color = System.Windows.Media.Color;

namespace PulseBar;

// Вкладка «Клавиатура» (2.3.0-beta.5): над тремя вкладками — профиль и меню «…» (создать, копировать, переименовать,
// удалить, поставить на M1–M3, программы для автопереключения, цвета из другого профиля); «Общие» — повтор клавиш
// Windows, управление Logitech и G HUB, приглушение, память клавиатуры; «Подсветка» — эффекты клавиш и логотипа, яркость,
// редактор цветов; «Клавиши G» — G1–G5 и MR. Всё помещается в окно без прокрутки. Профили сохраняются в logitech.json,
// строка их перечитывает (клавиатурой владеет она): цвета — через 0,2 с, чтобы кисть сразу было видно на клавиатуре,
// остальное — через 0,5 с.
internal sealed partial class SettingsWindow
{
	private static readonly string[] EffectIds = new string[5] { "off", "static", "breathing", "cycle", "wave" };

	private LogiConfig? _logi;

	private int _logiProfile = -1;

	private static int _keyboardTab = 1;

	// Для снимков: какая вкладка «Клавиатуры» открыта.
	internal static int KeyboardTab
	{
		set
		{
			_keyboardTab = value;
		}
	}

	private DispatcherTimer? _logiSave;

	// Время файла logitech.json после нашей записи: другое время — файл поменяла строка (кнопка яркости, M1–M3).
	private DateTime _logiStamp;

	private Action? _brightnessRefresh;

	private static DateTime LogiFileTime()
	{
		try
		{
			return System.IO.File.GetLastWriteTimeUtc(LogiConfig.FilePath);
		}
		catch (Exception)
		{
			return default;
		}
	}

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
				_logiStamp = LogiFileTime();
				_host.LogiReload();
			};
		}
		_logiSave.Stop();
		_logiSave.Interval = TimeSpan.FromMilliseconds(delayMs);
		_logiSave.Start();
	}

	// Сохранить сразу (смена профилей, слотов) и перестроить вкладку.
	private void LogiSaveNow()
	{
		_logiSave?.Stop();
		_logi?.Normalize();
		_logi?.Save();
		_logiStamp = LogiFileTime();
		_host.LogiReload();
		ShowPage(_nav.SelectedIndex);
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

	private UIElement KeyboardPage()
	{
		if (_logi == null)
		{
			_logi = LogiConfig.Load();
			_logiStamp = LogiFileTime();
		}
		LogiConfig c = _logi;
		ISettingsHost.Logitech st = _host.LogiStatus;
		if (_logiProfile < 0 || _logiProfile >= c.Profiles.Count)
		{
			_logiProfile = Math.Clamp(c.Active, 0, c.Profiles.Count - 1);
		}
		Profile pr = c.Profiles[_logiProfile];
		StackPanel p = Page(L.T("Keyboard"));
		p.MaxWidth = 660.0;
		p.Children[0].SetValue(FrameworkElement.MarginProperty, new Thickness(0.0, 0.0, 0.0, 8.0));
		// Профиль и меню «…».
		ComboBox profile = new ComboBox
		{
			ItemsSource = c.Profiles.Select((Profile x, int i) => ProfileTitle(c, i)).ToArray(),
			SelectedIndex = _logiProfile,
			Width = 260.0
		};
		profile.SelectionChanged += delegate
		{
			if (profile.SelectedIndex >= 0 && profile.SelectedIndex != _logiProfile)
			{
				_logiProfile = profile.SelectedIndex;
				ShowPage(_nav.SelectedIndex);
			}
		};
		Button more = new Button
		{
			Content = "…",
			MinWidth = 36.0,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			ToolTip = L.T("Profiles")
		};
		more.Click += delegate
		{
			ContextMenu m = ProfileMenu(c, pr);
			m.PlacementTarget = more;
			m.Placement = PlacementMode.Bottom;
			m.IsOpen = true;
		};
		TextBlock status = new TextBlock
		{
			Margin = new Thickness(14.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 12.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		DockPanel top = new DockPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0),
			LastChildFill = true
		};
		top.Children.Add(new TextBlock
		{
			Text = L.T("Profile"),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		});
		top.Children.Add(profile);
		top.Children.Add(more);
		top.Children.Add(status);
		p.Children.Add(top);
		UIElement[] pages = new UIElement[3]
		{
			GeneralTab(c, out Action refreshGeneral),
			LightingTab(c, pr, st),
			KeysTab(pr)
		};
		p.Children.Add(Segmented(new string[3] { L.T("General"), L.T("Lighting"), L.T("G-keys") }, pages, _keyboardTab, delegate(int i)
		{
			_keyboardTab = i;
		}));
		_refresh = Refresh;
		Refresh();
		return p;
		void Refresh()
		{
			ISettingsHost.Logitech s = _host.LogiStatus;
			string device = (s.Device.Length > 0) ? (ShortName(s.Device) + " · " + (s.Online ? ((s.Battery >= 0) ? $"{s.Battery}%" : L.T("connected")) : L.T("asleep or off"))) : L.T("No Logitech G keyboard found");
			string mode = s.Mode switch
			{
				"ghub" => "G HUB",
				"off" => L.T("Logitech control is off"),
				"host" => L.T("PulseBar controls the keyboard"),
				"starting" => L.T("Connecting…"),
				_ => ""
			};
			status.Text = device + ((mode.Length > 0) ? (" · " + mode) : "");
			refreshGeneral();
			// Строка поменяла файл (кнопка яркости на клавиатуре, выбор M1–M3): яркость профилей — оттуда, ползунок — за ней.
			// Иначе окно при следующем сохранении вернуло бы старую яркость.
			DateTime stamp = LogiFileTime();
			if (stamp != _logiStamp && !(_logiSave?.IsEnabled ?? false))
			{
				_logiStamp = stamp;
				LogiConfig fresh = LogiConfig.Load();
				for (int i = 0; i < Math.Min(fresh.Profiles.Count, c.Profiles.Count); i++)
				{
					c.Profiles[i].Brightness = fresh.Profiles[i].Brightness;
				}
				c.Active = fresh.Active;
				_brightnessRefresh?.Invoke();
			}
		}
	}

	private static string ShortName(string device)
	{
		string[] w = device.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return (w.Length > 0) ? w[0] : device;
	}

	// «Имя · M1, M3» — профиль на слотах; «Имя · программы» — только для программ.
	private static string ProfileTitle(LogiConfig c, int i)
	{
		List<string> slots = new List<string>();
		for (int s = 0; s < c.Slots.Length; s++)
		{
			if (c.Slots[s] == i)
			{
				slots.Add("M" + (s + 1));
			}
		}
		string tail = (slots.Count > 0) ? string.Join(", ", slots) : ((c.Profiles[i].Apps.Length > 0) ? L.T("programs") : "");
		return c.Profiles[i].Name + ((tail.Length > 0) ? (" · " + tail) : "");
	}

	private ContextMenu ProfileMenu(LogiConfig c, Profile pr)
	{
		ContextMenu m = new ContextMenu();
		void Item(ItemCollection to, string text, Action click, bool enabled = true, bool check = false)
		{
			MenuItem it = new MenuItem
			{
				Header = text,
				IsEnabled = enabled,
				IsCheckable = check,
				IsChecked = check
			};
			it.Click += delegate
			{
				click();
			};
			to.Add(it);
		}
		Item(m.Items, L.T("New profile"), delegate
		{
			c.Profiles.Add(new Profile
			{
				Name = L.F("Profile {0}", c.Profiles.Count + 1),
				Keyboard = new Lighting
				{
					Color = pr.Keyboard.Color
				},
				Logo = new Lighting
				{
					Color = pr.Logo.Color
				}
			});
			_logiProfile = c.Profiles.Count - 1;
			LogiSaveNow();
		});
		Item(m.Items, L.T("Copy profile"), delegate
		{
			Profile copy = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(pr, LogiJson.Default.Profile), LogiJson.Default.Profile)!;
			copy.Name = pr.Name + " " + L.T("(copy)");
			c.Profiles.Add(copy);
			_logiProfile = c.Profiles.Count - 1;
			LogiSaveNow();
		});
		Item(m.Items, L.T("Rename…"), delegate
		{
			string? name = AskText(L.T("Rename profile"), L.T("Name"), pr.Name);
			if (!string.IsNullOrWhiteSpace(name))
			{
				pr.Name = name;
				LogiSaveNow();
			}
		});
		Item(m.Items, L.T("Delete"), delegate
		{
			if (MessageBox.Show(this, L.F("Delete profile «{0}»?", pr.Name), "PulseBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
			{
				c.RemoveProfile(_logiProfile);
				_logiProfile = Math.Clamp(_logiProfile, 0, c.Profiles.Count - 1);
				LogiSaveNow();
			}
		}, c.Profiles.Count > 1);
		m.Items.Add(new Separator());
		int self = c.Profiles.IndexOf(pr);
		for (int s = 0; s < c.Slots.Length; s++)
		{
			int slot = s;
			Item(m.Items, L.F("On M{0}", s + 1), delegate
			{
				c.Slots[slot] = self;
				LogiSaveNow();
			}, c.Slots[s] != self, c.Slots[s] == self);
		}
		Item(m.Items, L.T("Programs…"), delegate
		{
			string? apps = AskText(L.T("Programs for automatic switching"), L.T("For example: game.exe, obs64. When the program is in front this profile turns on; when it is not — the profile chosen last with M1–M3."), pr.Apps);
			if (apps != null)
			{
				pr.Apps = apps;
				LogiSaveNow();
			}
		});
		MenuItem from = new MenuItem
		{
			Header = L.T("Copy colours from"),
			IsEnabled = c.Profiles.Count > 1
		};
		foreach (Profile other in c.Profiles.Where(x => x != pr))
		{
			Profile o = other;
			Item(from.Items, o.Name, delegate
			{
				pr.KeyColors = new Dictionary<string, string>(o.KeyColors);
				pr.Keyboard.Color = o.Keyboard.Color;
				pr.Logo.Color = o.Logo.Color;
				LogiSaveNow();
			});
		}
		m.Items.Add(from);
		return m;
	}

	// Простой диалог с одним полем; null — отмена.
	private string? AskText(string title, string label, string value)
	{
		Window w = new Window
		{
			Title = title,
			Owner = this,
			ThemeMode = ThemeMode.System,
			SizeToContent = SizeToContent.Height,
			Width = 460.0,
			ResizeMode = ResizeMode.NoResize,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ShowInTaskbar = false
		};
		StackPanel p = new StackPanel
		{
			Margin = new Thickness(16.0)
		};
		p.Children.Add(new TextBlock
		{
			Text = label,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		TextBox box = new TextBox
		{
			Text = value
		};
		p.Children.Add(box);
		StackPanel buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		};
		Button ok = new Button
		{
			Content = "OK",
			IsDefault = true,
			MinWidth = 80.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		Button cancel = new Button
		{
			Content = L.T("Cancel"),
			IsCancel = true,
			MinWidth = 80.0
		};
		ok.Click += delegate
		{
			w.DialogResult = true;
		};
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);
		p.Children.Add(buttons);
		w.Content = p;
		w.Loaded += delegate
		{
			box.Focus();
			box.SelectAll();
		};
		return (w.ShowDialog() == true) ? box.Text.Trim() : null;
	}

	// ---------- Общие ----------

	private UIElement GeneralTab(LogiConfig c, out Action refresh)
	{
		StackPanel p = new StackPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
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
		p.Children.Add(Caption("Logitech G"));
		p.Children.Add(Check(L.T("Control Logitech devices (instead of G HUB)"), () => c.Enabled, delegate(bool v)
		{
			c.Enabled = v;
			LogiChanged();
		}, apply: false));
		p.Children.Add(Note(L.T("While G HUB is running PulseBar only reads the battery.")));
		// Другая программа перекрашивает клавиатуру — обычно «Динамическое освещение» Windows через службу Logitech LampArray.
		TextBlock foreignText = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12.0,
			Margin = new Thickness(0.0, 6.0, 0.0, 4.0)
		};
		foreignText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
		Button lightingSettings = new Button
		{
			Content = L.T("Open Dynamic Lighting settings"),
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
		lightingSettings.Click += delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting")
				{
					UseShellExecute = true
				});
			}
			catch (Exception)
			{
			}
		};
		StackPanel foreign = new StackPanel
		{
			Visibility = Visibility.Collapsed
		};
		foreign.Children.Add(foreignText);
		foreign.Children.Add(lightingSettings);
		p.Children.Add(foreign);
		// Простой: коротко — приглушить, долго — погасить.
		int[] dimSteps = new int[11] { 0, 15, 30, 45, 60, 90, 120, 180, 300, 600, 900 };
		p.Children.Add(SliderRow(L.T("Dim after"), 0.0, dimSteps.Length - 1, 1.0, () => Math.Max(0, Array.FindIndex(dimSteps, (int x) => x >= c.IdleSeconds)), delegate(int v)
		{
			c.IdleSeconds = dimSteps[v];
			LogiChanged();
		}, (int v) => (dimSteps[v] == 0) ? L.T("never") : ((dimSteps[v] < 60) ? L.F("{0} s", dimSteps[v]) : L.F("{0} min", dimSteps[v] / 60.0)), apply: false));
		p.Children.Add(SliderRow(L.T("Dimmed brightness"), LogiConfig.MinBrightness - 1, 100.0, 1.0, () => (c.IdleBrightness == 0) ? (LogiConfig.MinBrightness - 1) : c.IdleBrightness, delegate(int v)
		{
			c.IdleBrightness = (v < LogiConfig.MinBrightness) ? 0 : v;
			LogiChanged();
		}, (int v) => (v < LogiConfig.MinBrightness) ? L.T("off") : $"{v}%", apply: false));
		p.Children.Add(SliderRow(L.T("Turn off after"), 0.0, 60.0, 1.0, () => c.OffMinutes, delegate(int v)
		{
			c.OffMinutes = v;
			LogiChanged();
		}, (int v) => (v == 0) ? L.T("never") : L.F("{0} min", v), apply: false));
		TextBlock memory = Note("");
		memory.Margin = new Thickness(0.0, 4.0, 0.0, 6.0);
		p.Children.Add(memory);
		WrapPanel buttons = new WrapPanel();
		Button factory = new Button
		{
			Content = L.T("Restore factory profiles"),
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
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
			Content = L.T("Back up keyboard memory")
		};
		backup.Click += delegate
		{
			_host.LogiBackup();
		};
		buttons.Children.Add(factory);
		buttons.Children.Add(backup);
		p.Children.Add(buttons);
		refresh = delegate
		{
			ISettingsHost.Logitech s = _host.LogiStatus;
			factory.IsEnabled = backup.IsEnabled = s.Mode == "host";
			foreign.Visibility = (s.Foreign.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
			foreignText.Text = (s.Foreign == "lamparray") ? L.T("Windows Dynamic Lighting repaints the keyboard (Logitech LampArray Service), and its colours can stay on top of PulseBar until the keyboard is switched off. Turn the service off (services.msc → Logitech LampArray Service → Disabled) or Dynamic Lighting entirely, then switch the keyboard off and on. Turning it off for this keyboard alone does not last: after each reconnect Windows sees a new device.") : L.T("Another program changes the keyboard lighting. PulseBar paints it back.");
			lightingSettings.Visibility = (s.Foreign == "lamparray") ? Visibility.Visible : Visibility.Collapsed;
			memory.Text = L.T("Keyboard memory (profiles on M1–M3) is updated by itself and works without PulseBar.") + ((s.Result.Length > 0) ? (" " + s.Result + ".") : "");
		};
		return p;
	}

	// ---------- Клавиши G ----------

	private UIElement KeysTab(Profile pr)
	{
		StackPanel p = new StackPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
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
				Width = 180.0,
				Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
			};
			TextBox value = new TextBox
			{
				Text = a.Value,
				Width = 250.0
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
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0.0, 3.0, 0.0, 3.0)
			};
			row.Children.Add(new TextBlock
			{
				Text = key,
				Width = 44.0,
				VerticalAlignment = VerticalAlignment.Center,
				FontWeight = FontWeights.SemiBold
			});
			row.Children.Add(type);
			row.Children.Add(value);
			row.Children.Add(where);
			p.Children.Add(row);
			Where();
			void Where()
			{
				// MR в памяти клавиатуры не хранится: без PulseBar не работает.
				bool hostOnly = key == "MR" || OnboardPlan.HostOnly(a);
				where.Text = (a.Type == "none" || string.IsNullOrWhiteSpace(a.Value)) ? "" : (hostOnly ? L.T("with PulseBar") : L.T("also without PulseBar"));
			}
		}
		TextBlock note = Note(L.T("Key combination: e.g. Ctrl+Shift+F5 or PlayPause. Macro: steps separated by ';' — wait 100; text Hello; Enter; run notepad."));
		note.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
		p.Children.Add(note);
		return p;
	}

	// ---------- Подсветка ----------

	private UIElement LightingTab(LogiConfig c, Profile pr, ISettingsHost.Logitech st)
	{
		StackPanel p = new StackPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		_lightingRefresh.Clear();
		StackPanel editorPanel = new StackPanel();
		string[] keyEffects = (st.KeyEffects.Length > 0) ? st.KeyEffects : EffectIds;
		p.Children.Add(LightingRow(L.T("Keys"), pr.Keyboard, keyEffects, delegate
		{
			editorPanel.Visibility = pr.PerKey ? Visibility.Visible : Visibility.Collapsed;
		}));
		p.Children.Add(LightingRow(L.T("Logo"), pr.Logo, (st.LogoEffects.Length > 0) ? st.LogoEffects : EffectIds, null));
		// Яркость 11–100 % (10 % и ниже G915 не светится); ползунок следует за кнопкой яркости на клавиатуре.
		Slider bright = new Slider
		{
			Minimum = LogiConfig.MinBrightness,
			Maximum = 100.0,
			TickFrequency = 1.0,
			IsSnapToTickEnabled = true,
			Value = Math.Max(LogiConfig.MinBrightness, pr.Brightness),
			Width = 220.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock brightText = new TextBlock
		{
			Width = 120.0,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		bool syncing = false;
		void BrightText()
		{
			brightText.Text = (pr.Brightness <= 0) ? L.T("off") : $"{pr.Brightness}%";
		}
		bright.ValueChanged += delegate
		{
			if (!syncing)
			{
				pr.Brightness = (int)Math.Round(bright.Value);
				BrightText();
				LogiChanged(200);
			}
		};
		_brightnessRefresh = delegate
		{
			syncing = true;
			bright.Value = Math.Max(LogiConfig.MinBrightness, pr.Brightness);
			BrightText();
			syncing = false;
		};
		BrightText();
		StackPanel brightRow = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		brightRow.Children.Add(bright);
		brightRow.Children.Add(brightText);
		p.Children.Add(LabeledRow(L.T("Brightness"), brightRow));
		editorPanel.Children.Add(KeyEditor(pr, st.KeyIds));
		editorPanel.Visibility = pr.PerKey ? Visibility.Visible : Visibility.Collapsed;
		p.Children.Add(editorPanel);
		return p;
	}

	// Подсветка зоны: эффект, цвет (у статичного — базовый цвет клавиш, у дыхания — цвет дыхания), период для анимаций.
	private UIElement LightingRow(string label, Lighting l, string[] effects, Action? effectChanged)
	{
		List<string> ids = effects.Where(e => EffectIds.Contains(e)).ToList();
		List<string> items = ids.Select(EffectName).ToList();
		int Selected()
		{
			int i = ids.IndexOf(l.Effect);
			return (i >= 0) ? i : Math.Max(0, ids.IndexOf("static"));
		}
		ComboBox effect = new ComboBox
		{
			ItemsSource = items,
			SelectedIndex = Selected(),
			Width = 160.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		TextBox color = new TextBox
		{
			Text = l.Color,
			Width = 86.0
		};
		Border swatch = new Border
		{
			Width = 24.0,
			Height = 24.0,
			CornerRadius = new CornerRadius(4.0),
			Margin = new Thickness(6.0, 0.0, 6.0, 0.0),
			BorderThickness = new Thickness(1.0),
			BorderBrush = Brushes.Gray,
			Cursor = Cursors.Hand,
			ToolTip = L.T("Choose colour")
		};
		StackPanel colorRow = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		colorRow.Children.Add(color);
		colorRow.Children.Add(swatch);
		Slider period = new Slider
		{
			Minimum = 1.0,
			Maximum = 20.0,
			TickFrequency = 0.5,
			IsSnapToTickEnabled = true,
			Width = 130.0,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			ToolTip = L.T("Effect period, seconds")
		};
		TextBlock periodText = new TextBlock
		{
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Width = 40.0
		};
		bool loading = true;
		void Visible()
		{
			string e = l.Effect;
			colorRow.Visibility = (e == "static" || e == "breathing") ? Visibility.Visible : Visibility.Collapsed;
			period.Visibility = periodText.Visibility = (e == "breathing" || e == "cycle" || e == "wave") ? Visibility.Visible : Visibility.Collapsed;
			int ms = (l.Period > 0) ? l.Period : e switch
			{
				"cycle" => OnboardPlan.DefaultCyclePeriod,
				"wave" => OnboardPlan.DefaultWavePeriod,
				_ => OnboardPlan.DefaultBreathePeriod
			};
			loading = true;
			period.Value = ms / 1000.0;
			periodText.Text = $"{ms / 1000.0:0.#} s";
			loading = false;
		}
		effect.SelectionChanged += delegate
		{
			l.Effect = ids[Math.Max(0, effect.SelectedIndex)];
			Visible();
			effectChanged?.Invoke();
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
		swatch.MouseLeftButtonDown += delegate
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
		_lightingRefresh.Add(delegate
		{
			color.Text = l.Color;
		});
		return LabeledRow(label, row);
		void Paint()
		{
			swatch.Background = Brush(Rgb.Parse(l.Color));
		}
	}

	private readonly List<Action> _lightingRefresh = new List<Action>();

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

	// Редактор цветов клавиш: кисть — левая кнопка (можно вести по клавишам), правая — вернуть базовый цвет; пипетка
	// берёт цвет с клавиши; логотип — квадратик над G1 (его цвет — цвет зоны логотипа). Отменить/повторить — по штрихам.
	private UIElement KeyEditor(Profile pr, byte[] keyIds)
	{
		const double U = 34.0;
		KeyDef[] layout = KeyLayout.For(keyIds);
		StackPanel p = new StackPanel
		{
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		// Инструменты: кисть, выбор цвета, пипетка, все клавиши, сброс, отменить/повторить, последние цвета.
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
			Cursor = Cursors.Hand,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
		};
		ToggleButton dropper = new ToggleButton
		{
			Content = L.T("Eyedropper"),
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			ToolTip = L.T("Take the colour of a key")
		};
		Button all = ToolButton(L.T("All keys"));
		Button clear = ToolButton(L.T("Clear key colours"));
		Button undo = ToolButton("↶");
		undo.ToolTip = L.T("Undo") + " (Ctrl+Z)";
		Button redo = ToolButton("↷");
		redo.ToolTip = L.T("Redo") + " (Ctrl+Y)";
		WrapPanel recent = new WrapPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0)
		};
		tools.Children.Add(brush);
		tools.Children.Add(dropper);
		tools.Children.Add(all);
		tools.Children.Add(clear);
		tools.Children.Add(undo);
		tools.Children.Add(redo);
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
			Border b = KeyBox(k.Label, k.W, k.H);
			Canvas.SetLeft(b, k.X * U);
			Canvas.SetTop(b, (k.Y + 1.25) * U);
			canvas.Children.Add(b);
			keys[k.Id] = b;
		}
		// Логотип — над G1, в ряду F-клавиш.
		KeyDef logoDef = new KeyDef(KeyLayout.Logo, "G", 0f, 0f);
		Border logo = KeyBox("G", 1f, 1f);
		logo.CornerRadius = new CornerRadius(8.0);
		logo.ToolTip = L.T("Logo");
		Canvas.SetLeft(logo, 0.0);
		Canvas.SetTop(logo, 1.25 * U);
		canvas.Children.Add(logo);
		p.Children.Add(new Viewbox
		{
			Child = canvas,
			Stretch = Stretch.Uniform,
			StretchDirection = StretchDirection.DownOnly,
			HorizontalAlignment = HorizontalAlignment.Left
		});
		TextBlock help = Note(L.T("Left button paints (you can drag across keys), right button returns the base colour. Changes are visible on the keyboard at once."));
		help.Margin = new Thickness(0.0, 4.0, 0.0, 0.0);
		p.Children.Add(help);
		// Отменить/повторить: снимки цветов клавиш и логотипа.
		Stack<(Dictionary<string, string> Keys, string Logo)> undoStack = new Stack<(Dictionary<string, string>, string)>();
		Stack<(Dictionary<string, string> Keys, string Logo)> redoStack = new Stack<(Dictionary<string, string>, string)>();
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
		brush.MouseLeftButtonDown += delegate
		{
			if (PickColor(_brush, out Rgb v))
			{
				SetBrush(v);
			}
		};
		all.Click += delegate
		{
			Snapshot();
			Remember(_brush);
			foreach (byte id in keys.Keys)
			{
				pr.KeyColors[id.ToString("X2")] = _brush.ToString();
			}
			Refresh();
			LogiChanged(200);
		};
		clear.Click += delegate
		{
			Snapshot();
			pr.KeyColors.Clear();
			Refresh();
			LogiChanged(200);
		};
		undo.Click += delegate
		{
			Step(undoStack, redoStack);
		};
		redo.Click += delegate
		{
			Step(redoStack, undoStack);
		};
		// Ctrl+Z / Ctrl+Y — пока редактор на экране (вкладка подсветки выгружается при переключении вкладок).
		p.Loaded += delegate
		{
			PreviewKeyDown -= OnKey;
			PreviewKeyDown += OnKey;
		};
		p.Unloaded += delegate
		{
			PreviewKeyDown -= OnKey;
		};
		_keyEditorRefresh = Refresh;
		Refresh();
		Recent();
		Buttons();
		return p;
		void OnKey(object s, KeyEventArgs e)
		{
			if (!p.IsVisible || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox)
			{
				return;
			}
			if (e.Key == Key.Z)
			{
				Step(undoStack, redoStack);
				e.Handled = true;
			}
			else if (e.Key == Key.Y)
			{
				Step(redoStack, undoStack);
				e.Handled = true;
			}
		}
		Button ToolButton(string text)
		{
			return new Button
			{
				Content = text,
				Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
				Padding = new Thickness(8.0, 2.0, 8.0, 2.0)
			};
		}
		Border KeyBox(string text, float w, float h)
		{
			return new Border
			{
				Width = w * U - 3.0,
				Height = h * U - 3.0,
				CornerRadius = new CornerRadius(4.0),
				BorderThickness = new Thickness(1.0),
				BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
				Child = new TextBlock
				{
					Text = text,
					FontSize = 10.0,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
					IsHitTestVisible = false
				}
			};
		}
		void Snapshot()
		{
			undoStack.Push((new Dictionary<string, string>(pr.KeyColors), pr.Logo.Color));
			redoStack.Clear();
			Buttons();
		}
		void Step(Stack<(Dictionary<string, string> Keys, string Logo)> from, Stack<(Dictionary<string, string> Keys, string Logo)> to)
		{
			if (from.Count == 0)
			{
				return;
			}
			to.Push((new Dictionary<string, string>(pr.KeyColors), pr.Logo.Color));
			var (k, l) = from.Pop();
			pr.KeyColors = k;
			pr.Logo.Color = l;
			Refresh();
			foreach (Action a in _lightingRefresh)
			{
				a();
			}
			Buttons();
			LogiChanged(200);
		}
		void Buttons()
		{
			undo.IsEnabled = undoStack.Count > 0;
			redo.IsEnabled = redoStack.Count > 0;
		}
		void Begin(MouseButtonEventArgs e, bool eraseMode)
		{
			Point pt = e.GetPosition(canvas);
			if (dropper.IsChecked == true)
			{
				if (ColorAt(pt) is Rgb picked)
				{
					SetBrush(picked);
				}
				dropper.IsChecked = false;
				e.Handled = true;
				return;
			}
			Snapshot();
			painting = true;
			erase = eraseMode;
			canvas.CaptureMouse();
			if (!erase)
			{
				Remember(_brush);
			}
			PaintAt(pt);
			e.Handled = true;
		}
		void End()
		{
			painting = false;
			canvas.ReleaseMouseCapture();
		}
		bool Inside(Point pt, KeyDef k)
		{
			return pt.X >= k.X * U && pt.X < (k.X + k.W) * U && pt.Y >= (k.Y + 1.25) * U && pt.Y < (k.Y + 1.25 + k.H) * U;
		}
		Rgb? ColorAt(Point pt)
		{
			if (Inside(pt, logoDef))
			{
				return Rgb.Parse(pr.Logo.Color);
			}
			foreach (KeyDef k in layout)
			{
				if (Inside(pt, k))
				{
					return pr.KeyColors.TryGetValue(k.Id.ToString("X2"), out string? v) ? Rgb.Parse(v) : Rgb.Parse(pr.Keyboard.Color);
				}
			}
			return null;
		}
		void PaintAt(Point pt)
		{
			if (Inside(pt, logoDef))
			{
				// Логотип: кисть задаёт цвет зоны логотипа (статичный), правая кнопка его не трогает.
				if (!erase && pr.Logo.Color != _brush.ToString())
				{
					pr.Logo.Color = _brush.ToString();
					pr.Logo.Effect = "static";
					ShowLogo();
					foreach (Action a in _lightingRefresh)
					{
						a();
					}
					LogiChanged(200);
				}
				return;
			}
			foreach (KeyDef k in layout)
			{
				if (Inside(pt, k))
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
		void Show(byte id)
		{
			if (keys.TryGetValue(id, out Border? b))
			{
				Fill(b, pr.KeyColors.TryGetValue(id.ToString("X2"), out string? v) ? Rgb.Parse(v) : Rgb.Parse(pr.Keyboard.Color));
			}
		}
		void ShowLogo()
		{
			Fill(logo, (pr.Logo.Effect == "off") ? Rgb.Black : Rgb.Parse(pr.Logo.Color));
		}
		void Fill(Border b, Rgb col)
		{
			b.Background = Brush(col);
			((TextBlock)b.Child).Foreground = (col.R * 299 + col.G * 587 + col.B * 114 > 140000) ? Brushes.Black : Brushes.White;
		}
		void Refresh()
		{
			foreach (byte id in keys.Keys)
			{
				Show(id);
			}
			ShowLogo();
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
