#pragma warning disable WPF0001
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulseBar.Logi;

namespace PulseBar;

// Верх вкладок «Клавиатура», «Мышь» и «Наушники» (2.4.0-beta.8, наушники — beta.10; решения пользователя 02.11.2026):
// переключатель всего вида устройств (выключено — вкладка пустая, строка их не ищет и не показывает заряд), найденное
// устройство и кнопка «Определить» — список всех найденных устройств вида с тем, что PulseBar с ними умеет, и выбор,
// каким управлять (чей заряд показывать); «Сохранить отчёт об устройствах» — для поддержки новых моделей. Название
// вкладки — марка и модель устройства. Вид: keyboard, mouse, headset (FoundDevice.Kind).
internal sealed partial class SettingsWindow
{
	private int _keyboardPage = -1;

	private int _mousePage = -1;

	private int _headsetPage = -1;

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

	private static bool KindOn(LogiConfig c, string kind)
	{
		return kind switch
		{
			"keyboard" => c.KeyboardOn,
			"mouse" => c.MouseOn,
			_ => c.HeadsetOn
		};
	}

	private static string KindWant(LogiConfig c, string kind)
	{
		return kind switch
		{
			"keyboard" => c.KeyboardDevice,
			"mouse" => c.MouseDevice,
			_ => c.HeadsetDevice
		};
	}

	// Выбранное (или первое подходящее) устройство вида; null — не найдено или вид выключен.
	private FoundDevice? Chosen(string kind)
	{
		ISettingsHost.Logitech st = _host.LogiStatus;
		string id = kind switch
		{
			"keyboard" => st.KeyboardId,
			"mouse" => st.MouseId,
			_ => st.HeadsetId
		};
		return (id.Length > 0) ? st.Devices?.FirstOrDefault((FoundDevice d) => d.Id == id) : null;
	}

	private string DeviceTitle(string kind)
	{
		FoundDevice? d = KindOn(LogiCfg(), kind) ? Chosen(kind) : null;
		return d?.Title ?? kind switch
		{
			"keyboard" => L.T("Keyboard"),
			"mouse" => L.T("Mouse"),
			_ => L.T("Headset")
		};
	}

	// Названия вкладок слева — по найденным устройствам (раз в секунду, вместе с остальным).
	private void UpdateDeviceTitles()
	{
		foreach (var (index, kind) in new (int, string)[3] { (_keyboardPage, "keyboard"), (_mousePage, "mouse"), (_headsetPage, "headset") })
		{
			if (index >= 0 && _nav.Items[index] is ListBoxItem item)
			{
				item.Content = DeviceTitle(kind);
			}
		}
	}

	private static string State(FoundDevice d)
	{
		if (!d.Online)
		{
			// Наушники Bluetooth: Windows помнит последний заряд и после отключения.
			return L.T("asleep or off") + ((d.Kind == "headset" && d.Battery >= 0) ? (" · " + L.F("last {0}%", d.Battery)) : "");
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
		case "nobattery":
			return L.T("does not report its charge");
		default:
			return L.T("not known yet: it has not been on");
		}
	}

	// Переключатель вида, строка устройства с «Определить», список найденных и отчёт. refresh — раз в секунду.
	private UIElement DeviceHeader(string kind, TextBlock title, out Action refresh)
	{
		LogiConfig c = LogiCfg();
		StackPanel root = new StackPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		string toggle = kind switch
		{
			"keyboard" => L.T("Keyboards in PulseBar"),
			"mouse" => L.T("Mice in PulseBar"),
			_ => L.T("Headsets in PulseBar")
		};
		root.Children.Add(Check(toggle, () => KindOn(c, kind), delegate(bool v)
		{
			switch (kind)
			{
			case "keyboard":
				c.KeyboardOn = v;
				break;
			case "mouse":
				c.MouseOn = v;
				break;
			default:
				c.HeadsetOn = v;
				break;
			}
			LogiSaveNow();
		}, apply: false));
		if (!KindOn(c, kind))
		{
			root.Children.Add(Note(kind switch
			{
				"keyboard" => L.T("Off: PulseBar does not look for keyboards, shows no charge and changes nothing on them; the keyboard works from its own memory."),
				"mouse" => L.T("Off: PulseBar does not look for mice and shows no charge."),
				_ => L.T("Off: PulseBar does not look for headsets and shows no charge.")
			}));
			refresh = delegate
			{
				title.Text = DeviceTitle(kind);
			};
			return root;
		}
		string none = kind switch
		{
			"keyboard" => L.T("No keyboard found"),
			"mouse" => L.T("No mouse found"),
			_ => L.T("No headset found")
		};
		TextBlock line = new TextBlock
		{
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		};
		Button detect = new Button
		{
			Content = L.T("Detect"),
			ToolTip = kind switch
			{
				"keyboard" => L.T("Look for keyboards again and choose which one PulseBar uses"),
				"mouse" => L.T("Look for mice again and choose which one PulseBar shows"),
				_ => L.T("Look for headsets again and choose which one PulseBar shows")
			}
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
			FoundDevice[] all = (st.Devices ?? Array.Empty<FoundDevice>()).Where((FoundDevice d) => d.Kind == kind).ToArray();
			FoundDevice? cur = Chosen(kind);
			title.Text = DeviceTitle(kind);
			// Модель — в названии; здесь заряд или связь и что PulseBar умеет; полное имя — в подсказке.
			line.Text = (cur != null) ? (State(cur) + " · " + SupportText(cur, brief: true)) : none;
			line.ToolTip = cur?.Name;
			bool searching = _detectFrom >= 0 && st.Scans == _detectFrom;
			if (_detectFrom >= 0 && st.Scans != _detectFrom)
			{
				_detectFrom = -1;
			}
			detect.IsEnabled = !searching;
			// Перестраивать список, только когда он изменился: иначе выбор мышью сбрасывался бы раз в секунду.
			string key = searching + "|" + KindWant(c, kind) + "|" + st.Report + "|" + string.Join(";", all.Select((FoundDevice d) => d.Id + d.Online + d.Battery + d.Support));
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
				list.Children.Add(Note(none));
			}
			else
			{
				string want = KindWant(c, kind);
				list.Children.Add(Choice(L.T("Automatically: the first suitable one"), "", want.Length == 0, ""));
				foreach (FoundDevice d in all)
				{
					string sub = string.Join(" · ", new string[3] { d.Brand, State(d), SupportText(d) }.Where((string x) => x.Length > 0));
					list.Children.Add(Choice(d.Name, sub, d.Id == want, d.Id));
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
		UIElement Choice(string name, string sub, bool isChecked, string id)
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
				GroupName = kind,
				Content = text,
				IsChecked = isChecked,
				Margin = new Thickness(0.0, 3.0, 0.0, 3.0)
			};
			r.Checked += delegate
			{
				switch (kind)
				{
				case "keyboard":
					c.KeyboardDevice = id;
					break;
				case "mouse":
					c.MouseDevice = id;
					break;
				default:
					c.HeadsetDevice = id;
					break;
				}
				LogiChanged(200);
			};
			return r;
		}
	}

	// Вкладка «Наушники»: переключатель, найденные наушники и «Определить». Заряд — тот, что видит Windows.
	private UIElement HeadsetPage()
	{
		StackPanel p = Page(DeviceTitle("headset"));
		p.Children.Add(DeviceHeader("headset", PageTitle(p), out Action device));
		_refresh = device;
		if (LogiCfg().HeadsetOn)
		{
			p.Children.Add(Note(L.T("Bluetooth headsets: the charge is the one Windows shows in Bluetooth & devices; it is read about once a minute. A headset that is not listed there with a charge does not report it.")));
		}
		return p;
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
