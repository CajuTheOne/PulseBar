#pragma warning disable WPF0001
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseBar;

// «О программе» → «Отчёты для отладки» (2.4.0-beta.12, решения пользователя 05.11.2026): галочка «Собирать данные для
// отладки»; при ней — ID этого ПК, поле «Что случилось?» и «Отправить», последние отчёты (дата, повод, размер,
// отправлен или ждёт связи с NAS) с «Показать» — содержимое ZIP открывается здесь же, без файлов на рабочем столе.
// Отчёт собирает и отправляет строка (DebugReports, Overlay.Reports).
internal sealed partial class SettingsWindow
{
	private const string ChecklistUrl = "https://github.com/CajuTheOne/PulseBar/blob/main/docs/TESTING.md";

	private void DebugSection(StackPanel p, out Action refresh)
	{
		p.Children.Add(Caption(L.T("Debug reports")));
		p.Children.Add(Check(L.T("Collect data for debugging"), () => _s.DebugReports, delegate(bool v)
		{
			_s.DebugReports = v;
			Changed();
			Dispatcher.BeginInvoke((Action)delegate
			{
				ShowPage(_nav.SelectedIndex);
			});
		}, apply: false));
		p.Children.Add(Note(L.T("Reports go to the NAS Leo folder by themselves: after a crash or an error, once a day, and with the Send button. Names of the PC and the user, e-mail and device addresses are replaced; keys and tokens are never included. When this is off, nothing is collected or sent; the log stays on this PC.")));
		p.Children.Add(Link(L.T("Checklist for testers"), ChecklistUrl));
		if (!_s.DebugReports)
		{
			refresh = delegate
			{
			};
			return;
		}
		TextBlock id = new TextBlock
		{
			Text = L.F("ID of this PC: {0}", DebugReports.PcId),
			Margin = new Thickness(0.0, 8.0, 0.0, 4.0)
		};
		p.Children.Add(id);
		p.Children.Add(new TextBlock
		{
			Text = L.T("What happened?"),
			Margin = new Thickness(0.0, 6.0, 0.0, 4.0)
		});
		TextBox note = new TextBox
		{
			AcceptsReturn = true,
			TextWrapping = TextWrapping.Wrap,
			Height = 64.0,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		p.Children.Add(note);
		TextBlock sendState = Note("");
		sendState.VerticalAlignment = VerticalAlignment.Center;
		sendState.Margin = new Thickness(10.0, 0.0, 0.0, 0.0);
		Button send = new Button
		{
			Content = L.T("Send"),
			ToolTip = L.T("Make a report with your description and a picture of the taskbar and send it to the NAS")
		};
		StackPanel sendRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 6.0, 0.0, 6.0)
		};
		sendRow.Children.Add(send);
		sendRow.Children.Add(sendState);
		p.Children.Add(sendRow);
		StackPanel list = new StackPanel();
		p.Children.Add(list);
		int countBefore = -1;
		send.Click += delegate
		{
			countBefore = DebugReports.List().Count;
			_host.DebugSend(note.Text);
			note.Text = "";
			sendState.Text = L.T("Making the report…");
		};
		string shown = "";
		refresh = Refresh;
		Refresh();
		void Refresh()
		{
			var entries = DebugReports.List();
			if (countBefore >= 0 && (entries.Count != countBefore || entries.Count >= DebugReports.Keep))
			{
				DebugReports.Entry? last = entries.LastOrDefault();
				if (last != null && last.Reason == "manual" && DateTime.Now - last.Time < TimeSpan.FromMinutes(2))
				{
					countBefore = -1;
					sendState.Text = last.Sent ? L.T("Sent.") : L.T("Saved; it will be sent when the NAS can be reached.");
				}
			}
			string key = string.Join(";", entries.Select((DebugReports.Entry e) => e.File + e.Sent));
			if (key == shown)
			{
				return;
			}
			shown = key;
			list.Children.Clear();
			if (entries.Count == 0)
			{
				return;
			}
			list.Children.Add(Caption(L.T("Recent reports")));
			foreach (DebugReports.Entry e in entries.AsEnumerable().Reverse().Take(6))
			{
				string reason = e.Reason switch
				{
					"crash" => L.T("after a crash"),
					"error" => L.T("after an error"),
					"daily" => L.T("daily"),
					_ => L.T("sent by you")
				};
				DockPanel row = new DockPanel
				{
					Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
				};
				Button show = new Button
				{
					Content = L.T("Show"),
					Padding = new Thickness(10.0, 2.0, 10.0, 2.0)
				};
				string file = e.File;
				show.Click += delegate
				{
					ShowReport(file);
				};
				DockPanel.SetDock(show, Dock.Right);
				row.Children.Add(show);
				row.Children.Add(new TextBlock
				{
					Text = $"{e.Time:dd.MM HH:mm} · {reason} · {DebugReports.Size(e.Size)} · {(e.Sent ? L.T("sent") : L.T("waiting for the NAS"))}" + ((e.Note.Length > 0) ? (" · " + e.Note) : ""),
					VerticalAlignment = VerticalAlignment.Center,
					TextTrimming = TextTrimming.CharacterEllipsis,
					Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
				});
				list.Children.Add(row);
			}
		}
	}

	// Содержимое отчёта: части ZIP по выбору, текст — как есть, снимок — картинкой.
	private void ShowReport(string file)
	{
		string path = Path.Combine(DebugReports.LocalDir, file);
		if (!File.Exists(path))
		{
			MessageBox.Show(this, L.T("The report is no longer on this PC."), "PulseBar", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		Window w = new Window
		{
			Title = "PulseBar — " + file,
			Width = 900.0,
			Height = 640.0,
			Owner = this,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ThemeMode = ThemeMode.System
		};
		ComboBox parts = new ComboBox
		{
			Margin = new Thickness(12.0, 12.0, 12.0, 8.0),
			MinWidth = 260.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		ContentControl content = new ContentControl
		{
			Margin = new Thickness(12.0, 0.0, 12.0, 12.0)
		};
		DockPanel root = new DockPanel();
		DockPanel.SetDock(parts, Dock.Top);
		root.Children.Add(parts);
		root.Children.Add(content);
		w.Content = root;
		string[] names;
		using (ZipArchive zip = ZipFile.OpenRead(path))
		{
			names = zip.Entries.Select((ZipArchiveEntry x) => x.FullName).ToArray();
		}
		parts.ItemsSource = names;
		parts.SelectionChanged += delegate
		{
			if (parts.SelectedItem is not string name)
			{
				return;
			}
			try
			{
				using ZipArchive zip = ZipFile.OpenRead(path);
				ZipArchiveEntry? e = zip.GetEntry(name);
				if (e == null)
				{
					return;
				}
				using Stream s = e.Open();
				using MemoryStream ms = new MemoryStream();
				s.CopyTo(ms);
				if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
				{
					BitmapImage img = new BitmapImage();
					ms.Position = 0;
					img.BeginInit();
					img.CacheOption = BitmapCacheOption.OnLoad;
					img.StreamSource = ms;
					img.EndInit();
					content.Content = new ScrollViewer
					{
						Content = new Image
						{
							Source = img,
							Stretch = Stretch.None,
							HorizontalAlignment = HorizontalAlignment.Left
						},
						HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
					};
				}
				else
				{
					content.Content = new TextBox
					{
						Text = Encoding.UTF8.GetString(ms.ToArray()),
						IsReadOnly = true,
						FontFamily = new FontFamily("Consolas"),
						FontSize = 12.0,
						TextWrapping = TextWrapping.NoWrap,
						VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
						HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
					};
				}
			}
			catch (Exception ex)
			{
				content.Content = new TextBlock
				{
					Text = ex.Message
				};
			}
		};
		parts.SelectedIndex = 0;
		w.Show();
	}
}
