#pragma warning disable WPF0001
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulseBar.Logi;

namespace PulseBar;

// Верх вкладок «Клавиатура» и «Мышь» (2.4.0-beta.8, решения пользователя 02.11.2026): переключатель всего вида
// устройств (выключено — вкладка пустая, строка их не ищет и не показывает заряд), найденное устройство и кнопка
// «Определить» — список всех найденных клавиатур (мышей) с тем, что PulseBar с ними умеет, и выбор, каким управлять;
// «Сохранить отчёт об устройствах» — для поддержки новых моделей. Название вкладки — марка и модель устройства.
internal sealed partial class SettingsWindow
{
	private int _keyboardPage = -1;

	private int _mousePage = -1;

	// Ждём конца «Определить»: число поисков до нажатия; -1 — не ждём.
	private int _detectFrom = -1;

	private string _reportBefore = "";

	private bool _reportWanted;

	private LogiConfig LogiCfg()
	{
		if (_logi == null)
		{
			_logi = LogiConfig.Load();
			_logiStamp = LogiFileTime();
		}
		return _logi;
	}

	// Выбранное (или первое подходящее) устройство вида; null — не найдено или вид выключен.
	private FoundDevice? Chosen(bool keyboard)
	{
		ISettingsHost.Logitech st = _host.LogiStatus;
		string id = keyboard ? st.KeyboardId : st.MouseId;
		return (id.Length > 0) ? st.Devices?.FirstOrDefault((FoundDevice d) => d.Id == id) : null;
	}

	private string DeviceTitle(bool keyboard)
	{
		LogiConfig c = LogiCfg();
		FoundDevice? d = (keyboard ? c.KeyboardOn : c.MouseOn) ? Chosen(keyboard) : null;
		return d?.Title ?? (keyboard ? L.T("Keyboard") : L.T("Mouse"));
	}

	// Названия вкладок слева — по найденным устройствам (раз в секунду, вместе с остальным).
	private void UpdateDeviceTitles()
	{
		if (_keyboardPage >= 0 && _nav.Items[_keyboardPage] is ListBoxItem k)
		{
			k.Content = DeviceTitle(keyboard: true);
		}
		if (_mousePage >= 0 && _nav.Items[_mousePage] is ListBoxItem m)
		{
			m.Content = DeviceTitle(keyboard: false);
		}
	}

	private static string State(FoundDevice d)
	{
		if (!d.Online)
		{
			return L.T("asleep or off");
		}
		return (d.Battery >= 0) ? $"{d.Battery}%{(d.Charging ? " ⚡" : "")}" : L.T("connected");
	}

	// Что PulseBar умеет с устройством: всё (и чем именно), только заряд, только настройки Windows. brief — для строки
	// под названием: без «всё:» и заряда (он и так виден).
	private static string SupportText(FoundDevice d, bool brief = false)
	{
		switch (d.Support)
		{
		case "full":
		{
			string[] parts = new (string Id, string Text)[6]
			{
				("lighting", L.T("per-key lighting")),
				("effects", L.T("effects")),
				("brightness", L.T("brightness")),
				("gkeys", L.T("G-keys")),
				("memory", L.T("profiles in memory")),
				("battery", brief ? "" : L.T("charge"))
			}.Where(((string Id, string Text) f) => f.Text.Length > 0 && d.Has(f.Id)).Select(((string Id, string Text) f) => f.Text).ToArray();
			if (brief && parts.Length > 0)
			{
				return string.Join(", ", parts);
			}
			return L.T("everything") + ((parts.Length > 0) ? (": " + string.Join(", ", parts)) : "");
		}
		case "battery":
			return L.T("charge only");
		case "windows":
			return L.T("Windows settings only");
		default:
			return L.T("not known yet: it has not been on");
		}
	}

	// Переключатель вида, строка устройства с «Определить», список найденных и отчёт. refresh — раз в секунду.
	private UIElement DeviceHeader(bool keyboard, TextBlock title, out Action refresh)
	{
		LogiConfig c = LogiCfg();
		StackPanel root = new StackPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		root.Children.Add(Check(keyboard ? L.T("Keyboards in PulseBar") : L.T("Mice in PulseBar"), () => keyboard ? c.KeyboardOn : c.MouseOn, delegate(bool v)
		{
			if (keyboard)
			{
				c.KeyboardOn = v;
			}
			else
			{
				c.MouseOn = v;
			}
			LogiSaveNow();
		}, apply: false));
		bool on = keyboard ? c.KeyboardOn : c.MouseOn;
		if (!on)
		{
			root.Children.Add(Note(keyboard ? L.T("Off: PulseBar does not look for keyboards, shows no charge and changes nothing on them; the keyboard works from its own memory.") : L.T("Off: PulseBar does not look for mice and shows no charge.")));
			refresh = delegate
			{
				title.Text = DeviceTitle(keyboard);
			};
			return root;
		}
		TextBlock line = new TextBlock
		{
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		};
		Button detect = new Button
		{
			Content = L.T("Detect"),
			ToolTip = keyboard ? L.T("Look for keyboards again and choose which one PulseBar uses") : L.T("Look for mice again and choose which one PulseBar shows")
		};
		DockPanel row = new DockPanel
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 4.0)
		};
		DockPanel.SetDock(detect, Dock.Right);
		row.Children.Add(detect);
		row.Children.Add(line);
		root.Children.Add(row);
		// Список найденных — после «Определить».
		StackPanel list = new StackPanel
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 6.0)
		};
		Border box = new Border
		{
			Child = list,
			Padding = new Thickness(12.0, 8.0, 12.0, 10.0),
			CornerRadius = new CornerRadius(6.0),
			BorderThickness = new Thickness(1.0),
			Visibility = Visibility.Collapsed
		};
		box.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
		box.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
		root.Children.Add(box);
		string shown = "";
		detect.Click += delegate
		{
			_detectFrom = _host.LogiStatus.Scans;
			_host.LogiDetect();
			box.Visibility = Visibility.Visible;
			shown = "";
			Refresh();
		};
		Refresh();
		refresh = Refresh;
		return root;
		void Refresh()
		{
			ISettingsHost.Logitech st = _host.LogiStatus;
			FoundDevice[] all = (st.Devices ?? Array.Empty<FoundDevice>()).Where((FoundDevice d) => d.Keyboard == keyboard).ToArray();
			FoundDevice? cur = Chosen(keyboard);
			title.Text = DeviceTitle(keyboard);
			// Модель — в названии; здесь заряд или связь и что PulseBar умеет; полное имя — в подсказке.
			line.Text = (cur != null) ? (State(cur) + " · " + SupportText(cur, brief: true)) : (keyboard ? L.T("No keyboard found") : L.T("No mouse found"));
			line.ToolTip = cur?.Name;
			bool searching = _detectFrom >= 0 && st.Scans == _detectFrom;
			if (_detectFrom >= 0 && st.Scans != _detectFrom)
			{
				_detectFrom = -1;
			}
			detect.IsEnabled = !searching;
			// Перестраивать список, только когда он изменился: иначе выбор мышью сбрасывался бы раз в секунду.
			string key = searching + "|" + c.KeyboardDevice + c.MouseDevice + "|" + st.Report + "|" + string.Join(";", all.Select((FoundDevice d) => d.Id + d.Online + d.Battery + d.Support));
			if (key == shown || box.Visibility != Visibility.Visible)
			{
				return;
			}
			shown = key;
			list.Children.Clear();
			if (searching)
			{
				list.Children.Add(Note(L.T("Searching…")));
				return;
			}
			if (all.Length == 0)
			{
				list.Children.Add(Note(keyboard ? L.T("No keyboard found") : L.T("No mouse found")));
			}
			else
			{
				string group = keyboard ? "kb" : "mouse";
				string want = keyboard ? c.KeyboardDevice : c.MouseDevice;
				list.Children.Add(Choice(group, L.T("Automatically: the first suitable one"), "", want.Length == 0, ""));
				foreach (FoundDevice d in all)
				{
					string sub = string.Join(" · ", new string[3] { d.Brand, State(d), SupportText(d) }.Where((string x) => x.Length > 0));
					list.Children.Add(Choice(group, d.Name, sub, d.Id == want, d.Id));
				}
			}
			WrapPanel actions = new WrapPanel
			{
				Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
			};
			Button report = new Button
			{
				Content = L.T("Save device report"),
				ToolTip = L.T("A text file with what PulseBar knows about your keyboards and mice — send it to add support for a new model.")
			};
			report.Click += delegate
			{
				_reportBefore = _host.LogiStatus.Report;
				_reportWanted = true;
				_host.LogiReport();
			};
			actions.Children.Add(report);
			list.Children.Add(actions);
			if (st.Report.Length > 0)
			{
				TextBlock saved = Note(L.T("Report saved") + ": " + st.Report);
				saved.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
				saved.Cursor = System.Windows.Input.Cursors.Hand;
				saved.MouseLeftButtonUp += delegate
				{
					ShowInExplorer(st.Report);
				};
				list.Children.Add(saved);
			}
			// Новый отчёт готов — показать его в Проводнике.
			if (_reportWanted && st.Report.Length > 0 && st.Report != _reportBefore)
			{
				_reportWanted = false;
				ShowInExplorer(st.Report);
			}
		}
		UIElement Choice(string group, string name, string sub, bool isChecked, string id)
		{
			StackPanel text = new StackPanel();
			text.Children.Add(new TextBlock
			{
				Text = name,
				TextWrapping = TextWrapping.Wrap
			});
			if (sub.Length > 0)
			{
				text.Children.Add(Note(sub));
			}
			RadioButton r = new RadioButton
			{
				GroupName = group,
				Content = text,
				IsChecked = isChecked,
				Margin = new Thickness(0.0, 3.0, 0.0, 3.0)
			};
			r.Checked += delegate
			{
				if (keyboard)
				{
					c.KeyboardDevice = id;
				}
				else
				{
					c.MouseDevice = id;
				}
				LogiChanged(200);
			};
			return r;
		}
	}

	private static void ShowInExplorer(string path)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
			{
				UseShellExecute = true
			});
		}
		catch (Exception)
		{
		}
	}

	// Повтор клавиш и мигание курсора Windows — для любой клавиатуры.
	private void WindowsKeyboardSettings(StackPanel p)
	{
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
	}

	private static TextBlock PageTitle(StackPanel page)
	{
		return (TextBlock)page.Children[0];
	}
}
