using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PulseBar.Logi;

internal sealed class LogiForm : Form
{
	private sealed class LightRow
	{
		public readonly FlowLayoutPanel Panel;

		private readonly LogiForm _f;

		private readonly ComboBox _effect = new ComboBox
		{
			DropDownStyle = ComboBoxStyle.DropDownList,
			Width = 150
		};

		private readonly Button _color = new Button
		{
			Width = 90,
			Height = 28,
			FlatStyle = FlatStyle.Flat
		};

		private Lighting? _l;

		public LightRow(LogiForm f, string title)
		{
			_f = f;
			Panel = new FlowLayoutPanel
			{
				AutoSize = true,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false,
				Margin = new Padding(0, 0, 0, 4)
			};
			FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
			{
				AutoSize = true,
				Margin = Padding.Empty
			};
			ComboBox.ObjectCollection items = _effect.Items;
			object[] effectNames = EffectNames;
			items.AddRange(effectNames);
			flowLayoutPanel.Controls.AddRange(_effect, _color);
			_effect.Margin = new Padding(0, 4, 8, 0);
			_color.Margin = new Padding(0, 3, 0, 0);
			Panel.Controls.Add(flowLayoutPanel);
			_effect.SelectedIndexChanged += delegate
			{
				if (_l != null && !_f._loading)
				{
					_l.Effect = EffectIds[_effect.SelectedIndex];
					Sync();
					_f.Changed();
				}
			};
			_color.Click += delegate
			{
				if (_l == null)
				{
					return;
				}
				var (red, green, blue) = LogiService.ParseColor(_l.Color);
				using ColorDialog colorDialog = new ColorDialog
				{
					Color = Color.FromArgb(red, green, blue),
					FullOpen = true,
					AnyColor = true
				};
				if (colorDialog.ShowDialog(_f) == DialogResult.OK)
				{
					_l.Color = $"#{colorDialog.Color.R:X2}{colorDialog.Color.G:X2}{colorDialog.Color.B:X2}";
					Sync();
					_f.Changed();
				}
			};
		}

		public void Load(Lighting l)
		{
			_l = l;
			_effect.SelectedIndex = Math.Max(0, Array.IndexOf(EffectIds, l.Effect));
			Sync();
		}

		private void Sync()
		{
			if (_l != null)
			{
				var (red, green, blue) = LogiService.ParseColor(_l.Color);
				_color.BackColor = Color.FromArgb(red, green, blue);
				_color.FlatAppearance.BorderColor = Color.Gray;
				_color.Visible = _l.Effect == "static";
			}
		}
	}

	private readonly LogiService _svc;

	private readonly bool _dark;

	private readonly Timer _applyTimer = new Timer
	{
		Interval = 200
	};

	private readonly Label _status = new Label
	{
		AutoSize = true,
		ForeColor = Color.Gray,
		Margin = new Padding(0, 2, 0, 4)
	};

	private readonly Label _kbTitle = new Label
	{
		AutoSize = true,
		Margin = Padding.Empty
	};

	private readonly Label _mouseTitle = new Label
	{
		AutoSize = true,
		Margin = Padding.Empty
	};

	private readonly Label _mouseStatus = new Label
	{
		AutoSize = true,
		ForeColor = Color.Gray,
		Margin = new Padding(0, 2, 0, 4)
	};

	private readonly Label _ghubWarn = new Label
	{
		AutoSize = true,
		ForeColor = Color.FromArgb(240, 170, 60),
		Margin = new Padding(0, 6, 8, 0)
	};

	private readonly Button _ghubClose = new Button();

	private readonly ComboBox _profiles = new ComboBox
	{
		DropDownStyle = ComboBoxStyle.DropDownList,
		Width = 200
	};

	private readonly Panel _pages = new Panel
	{
		Dock = DockStyle.Fill
	};

	private static readonly string[] GKeys = new string[5] { "G1", "G2", "G3", "G4", "G5" };

	private readonly ComboBox[] _type = new ComboBox[GKeys.Length];

	private readonly TextBox[] _value = new TextBox[GKeys.Length];

	private readonly Button[] _browse = new Button[GKeys.Length];

	private readonly LightRow _kb;

	private readonly KeyboardView _view = new KeyboardView(32f);

	private readonly Label _selInfo = new Label
	{
		AutoSize = true,
		ForeColor = Color.Gray
	};

	private readonly TrackBar _brightness = new TrackBar
	{
		Minimum = 0,
		Maximum = 100,
		TickFrequency = 10,
		Width = 300,
		SmallChange = 5,
		LargeChange = 10
	};

	private readonly Label _brightnessValue = new Label
	{
		AutoSize = true
	};

	private readonly Label _mice = new Label
	{
		AutoSize = true,
		Margin = new Padding(0, 0, 0, 10)
	};

	private readonly CheckBox _enabled = new CheckBox
	{
		AutoSize = true
	};

	private bool _loading;

	private readonly List<RadioButton> _tabButtons = new List<RadioButton>();

	private static readonly string[] TypeIds = new string[5] { "none", "keys", "text", "run", "macro" };

	private static readonly string[] EffectIds = new string[2] { "off", "static" };

	private static string[] TypeNames => new string[5]
	{
		L.T("Nothing"),
		L.T("Key combination"),
		L.T("Type text"),
		L.T("Run program / open"),
		L.T("Macro")
	};

	private static string[] EffectNames => new string[2]
	{
		L.T("Off"),
		L.T("Color")
	};

	private Profile P => _svc.Config.Profiles[Math.Clamp(_profiles.SelectedIndex, 0, _svc.Config.Profiles.Count - 1)];

	public void ShowTab(int i)
	{
		_tabButtons[i].Checked = true;
	}

	public LogiForm(LogiService svc, bool dark)
	{
		_svc = svc;
		_dark = dark;
		Text = L.T("PulseBar — keyboard and mouse");
		base.StartPosition = FormStartPosition.CenterScreen;
		base.AutoScaleMode = AutoScaleMode.Dpi;
		base.AutoScaleDimensions = new SizeF(96f, 96f);
		Font = new Font("Segoe UI", 9.75f);
		base.MinimizeBox = true;
		base.MaximizeBox = false;
		base.FormBorderStyle = FormBorderStyle.FixedSingle;
		base.ClientSize = new Size(1170, 790);
		base.Padding = new Padding(14);
		_applyTimer.Tick += delegate
		{
			_applyTimer.Stop();
			_svc.Save();
			_svc.ApplyNow();
		};
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false
		};
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			AutoSize = true,
			Margin = Padding.Empty
		};
		FlowLayoutPanel flowLayoutPanel3 = new FlowLayoutPanel
		{
			AutoSize = true,
			Margin = Padding.Empty
		};
		_ghubClose.Text = L.T("Close G HUB");
		_ghubClose.AutoSize = true;
		_ghubClose.Click += delegate
		{
			CloseGHub();
		};
		flowLayoutPanel3.Controls.AddRange(_ghubWarn, _ghubClose);
		flowLayoutPanel.Controls.AddRange(flowLayoutPanel2, flowLayoutPanel3);
		Panel panel = new Panel();
		_kbTitle.Font = (_mouseTitle.Font = new Font("Segoe UI Semibold", 12f));
		FlowLayoutPanel flowLayoutPanel4 = new FlowLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			Padding = new Padding(0, 6, 0, 6)
		};
		FlowLayoutPanel flowLayoutPanel5 = new FlowLayoutPanel
		{
			AutoSize = true,
			Margin = new Padding(0, 8, 0, 0)
		};
		flowLayoutPanel5.Controls.AddRange(new Label
		{
			Text = L.T("Profile"),
			AutoSize = true,
			Margin = new Padding(0, 6, 8, 0)
		}, _profiles, Small(L.T("Rename"), RenameProfile), Small(L.T("Delete"), DeleteProfile), _status);
		_status.Margin = new Padding(12, 7, 0, 0);
		flowLayoutPanel4.Controls.AddRange(_kbTitle, flowLayoutPanel5);
		_profiles.SelectedIndexChanged += delegate
		{
			if (!_loading && _profiles.SelectedIndex >= 0)
			{
				if (_profiles.SelectedIndex == _svc.Config.Profiles.Count)
				{
					AddProfile();
				}
				else
				{
					_svc.SelectProfile(_profiles.SelectedIndex);
					LoadProfile();
				}
			}
		};
		FlowLayoutPanel flowLayoutPanel6 = new FlowLayoutPanel
		{
			Dock = DockStyle.Left,
			Width = 300,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			AutoScroll = true,
			Padding = new Padding(0, 8, 14, 0)
		};
		flowLayoutPanel6.Controls.Add(Section(L.T("Buttons G1–G5")));
		flowLayoutPanel6.Controls.Add(BuildKeysPage());
		FlowLayoutPanel flowLayoutPanel7 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			AutoScroll = true,
			Padding = new Padding(4, 8, 0, 0)
		};
		_kb = new LightRow(this, L.T("Keyboard and logo"));
		flowLayoutPanel7.Controls.Add(Section(L.T("Lighting")));
		flowLayoutPanel7.Controls.Add(BuildLightPage());
		panel.Controls.Add(flowLayoutPanel7);
		panel.Controls.Add(flowLayoutPanel6);
		panel.Controls.Add(flowLayoutPanel4);
		Panel panel2 = new Panel();
		FlowLayoutPanel flowLayoutPanel8 = new FlowLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			Padding = new Padding(0, 6, 0, 6)
		};
		flowLayoutPanel8.Controls.AddRange(_mouseTitle, _mouseStatus);
		Control control = BuildMousePage();
		control.Dock = DockStyle.Fill;
		panel2.Controls.Add(control);
		panel2.Controls.Add(flowLayoutPanel8);
		Control[] pages = new Control[2] { panel, panel2 };
		string[] array = new string[2]
		{
			L.T("Keyboard"),
			L.T("Mouse")
		};
		for (int num = 0; num < pages.Length; num++)
		{
			int n = num;
			RadioButton rb = new RadioButton
			{
				Text = array[num],
				Appearance = Appearance.Button,
				AutoSize = true,
				FlatStyle = FlatStyle.Flat,
				Padding = new Padding(14, 3, 14, 3),
				Margin = new Padding(0, 0, 4, 6)
			};
			rb.CheckedChanged += delegate
			{
				if (rb.Checked)
				{
					Control[] array2 = pages;
					foreach (Control obj in array2)
					{
						obj.Visible = obj == pages[n];
					}
				}
			};
			flowLayoutPanel2.Controls.Add(rb);
			_tabButtons.Add(rb);
			pages[num].Dock = DockStyle.Fill;
			pages[num].Visible = false;
			_pages.Controls.Add(pages[num]);
			if (num == 0)
			{
				rb.Checked = true;
			}
		}
		FlowLayoutPanel flowLayoutPanel9 = new FlowLayoutPanel
		{
			Dock = DockStyle.Bottom,
			AutoSize = true,
			Padding = new Padding(0, 8, 0, 0)
		};
		_enabled.Text = L.T("Control Logitech devices (instead of G HUB)");
		_enabled.Checked = _svc.Config.Enabled;
		_enabled.CheckedChanged += delegate
		{
			_svc.Config.Enabled = _enabled.Checked;
			_svc.Save();
			if (_enabled.Checked)
			{
				_svc.Start();
			}
			else
			{
				_svc.Stop();
			}
		};
		Button button = new Button
		{
			Text = L.T("Close"),
			AutoSize = true,
			MinimumSize = new Size(90, 30)
		};
		button.Click += delegate
		{
			Close();
		};
		flowLayoutPanel9.Controls.AddRange(_enabled, button);
		_enabled.Margin = new Padding(0, 6, 420, 0);
		base.Controls.Add(_pages);
		base.Controls.Add(flowLayoutPanel9);
		base.Controls.Add(flowLayoutPanel);
		LoadProfiles();
		RefreshStatus();
		_svc.Changed += OnSvcChanged;
		if (dark)
		{
			Theme(this);
		}
	}

	private Control BuildKeysPage()
	{
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			AutoSize = true,
			Margin = new Padding(0, 0, 0, 10)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			ColumnCount = 2,
			AutoSize = true,
			Margin = Padding.Empty
		};
		for (int i = 0; i < GKeys.Length; i++)
		{
			int n = i;
			tableLayoutPanel.Controls.Add(new Label
			{
				Text = GKeys[i],
				AutoSize = true,
				Anchor = AnchorStyles.Left,
				Font = new Font(Font, FontStyle.Bold),
				Margin = new Padding(0, 9, 10, 0)
			}, 0, 2 * i);
			ComboBox cb = (_type[i] = new ComboBox
			{
				DropDownStyle = ComboBoxStyle.DropDownList,
				Width = 230,
				Margin = new Padding(0, 6, 0, 2)
			});
			ComboBox.ObjectCollection items = cb.Items;
			object[] typeNames = TypeNames;
			items.AddRange(typeNames);
			tableLayoutPanel.Controls.Add(cb, 1, 2 * i);
			FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
			{
				AutoSize = true,
				Margin = new Padding(0, 0, 0, 6),
				WrapContents = false
			};
			TextBox tb = (_value[i] = new TextBox
			{
				Width = 230,
				Margin = Padding.Empty
			});
			Button button = (_browse[i] = new Button
			{
				Text = "…",
				Width = 30,
				Height = 25,
				Margin = new Padding(2, 0, 0, 0)
			});
			flowLayoutPanel2.Controls.AddRange(tb, button);
			tableLayoutPanel.Controls.Add(flowLayoutPanel2, 1, 2 * i + 1);
			cb.SelectedIndexChanged += delegate
			{
				if (!_loading)
				{
					P.Key(GKeys[n]).Type = TypeIds[cb.SelectedIndex];
					UpdateKeyRow(n);
					Changed();
				}
			};
			tb.TextChanged += delegate
			{
				if (!_loading)
				{
					P.Key(GKeys[n]).Value = tb.Text;
					Changed();
				}
			};
			tb.PreviewKeyDown += delegate(object? _, PreviewKeyDownEventArgs e)
			{
				if (TypeIds[cb.SelectedIndex] == "keys")
				{
					e.IsInputKey = true;
				}
			};
			tb.KeyDown += delegate(object? _, KeyEventArgs e)
			{
				if (!(TypeIds[cb.SelectedIndex] != "keys"))
				{
					e.SuppressKeyPress = true;
					e.Handled = true;
					TextBox textBox = tb;
					Keys keyCode = e.KeyCode;
					bool flag = ((keyCode == Keys.Back || keyCode == Keys.Delete) ? true : false);
					textBox.Text = ((flag && e.Modifiers == Keys.None) ? "" : Macro.Describe(e.KeyData));
					tb.SelectionStart = tb.Text.Length;
				}
			};
			button.Click += delegate
			{
				using OpenFileDialog openFileDialog = new OpenFileDialog
				{
					Filter = L.T("Programs") + "|*.exe;*.bat;*.cmd;*.lnk|" + L.T("All files") + "|*.*"
				};
				if (openFileDialog.ShowDialog(this) == DialogResult.OK)
				{
					tb.Text = "\"" + openFileDialog.FileName + "\"";
				}
			};
		}
		flowLayoutPanel.Controls.Add(tableLayoutPanel);
		string[] array = new string[5]
		{
			L.T("Unassigned G-keys do nothing."),
			L.T("Key combination: click the field and press the keys (Backspace clears)."),
			L.T("Macro: steps separated by ';', e.g.  Ctrl+C; wait 100; text Hello; Enter; run notepad"),
			L.T("Steps: a key combination, wait <ms>, text <text>, down <key>, up <key>, run <program>."),
			L.T("Media keys: PlayPause, Next, Prev, Mute, VolUp, VolDown.")
		};
		for (int num = 0; num < array.Length; num++)
		{
			Label label = Hint(array[num]);
			label.MaximumSize = new Size(270, 0);
			flowLayoutPanel.Controls.Add(label);
		}
		return flowLayoutPanel;
	}

	private void UpdateKeyRow(int i)
	{
		string text = TypeIds[Math.Max(0, _type[i].SelectedIndex)];
		_value[i].Enabled = text != "none";
		_browse[i].Visible = text == "run";
		TextBox textBox = _value[i];
		textBox.PlaceholderText = text switch
		{
			"keys" => L.T("press keys…"), 
			"text" => L.T("text to type"), 
			"run" => L.T("program, file, folder or URL"), 
			"macro" => "Ctrl+C; wait 100; Ctrl+V", 
			_ => "", 
		};
	}

	private Control BuildLightPage()
	{
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			AutoSize = true,
			Margin = new Padding(0, 0, 0, 10)
		};
		TableLayoutPanel t = new TableLayoutPanel
		{
			ColumnCount = 2,
			AutoSize = true,
			Margin = Padding.Empty
		};
		Row(L.T("Profile colour"), _kb.Panel);
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			AutoSize = true,
			Margin = Padding.Empty
		};
		flowLayoutPanel2.Controls.AddRange(_brightness, _brightnessValue);
		_brightnessValue.Margin = new Padding(0, 8, 0, 0);
		Row(L.T("Brightness"), flowLayoutPanel2);
		_brightness.ValueChanged += delegate
		{
			_brightnessValue.Text = _brightness.Value + " %";
			if (!_loading)
			{
				P.Brightness = _brightness.Value;
				Changed();
			}
		};
		LogiConfig cfg = _svc.Config;
		FlowLayoutPanel flowLayoutPanel3 = new FlowLayoutPanel
		{
			AutoSize = true,
			Margin = Padding.Empty
		};
		NumericUpDown minutes = new NumericUpDown
		{
			Minimum = 0m,
			Maximum = 240m,
			Width = 64,
			Value = cfg.IdleMinutes,
			Margin = new Padding(0, 4, 6, 0)
		};
		TrackBar idleBright = new TrackBar
		{
			Minimum = 0,
			Maximum = 100,
			TickFrequency = 10,
			Width = 180,
			SmallChange = 5,
			LargeChange = 10,
			Value = cfg.IdleBrightness
		};
		Label idleVal = new Label
		{
			AutoSize = true,
			Margin = new Padding(0, 8, 0, 0)
		};
		IdleText();
		flowLayoutPanel3.Controls.AddRange(minutes, new Label
		{
			Text = L.T("min without use → brightness"),
			AutoSize = true,
			Margin = new Padding(0, 8, 6, 0)
		}, idleBright, idleVal);
		Row(L.T("Dim when idle"), flowLayoutPanel3);
		minutes.ValueChanged += delegate
		{
			cfg.IdleMinutes = (int)minutes.Value;
			_svc.Save();
		};
		idleBright.ValueChanged += delegate
		{
			cfg.IdleBrightness = idleBright.Value;
			IdleText();
			_svc.Save();
		};
		flowLayoutPanel.Controls.Add(t);
		flowLayoutPanel.Controls.Add(Hint(L.T("0 minutes = never dim. Any key press or mouse move brings the profile brightness back.")));
		flowLayoutPanel.Controls.Add(Caption(L.T("Key colours")));
		_view.Margin = new Padding(0, 2, 0, 4);
		_view.ColorOf = KeyColor;
		flowLayoutPanel.Controls.Add(_view);
		FlowLayoutPanel flowLayoutPanel4 = new FlowLayoutPanel
		{
			AutoSize = true,
			Margin = new Padding(0, 0, 0, 2)
		};
		Button colorBtn = Small(L.T("Colour for selected…"), PickKeyColor);
		Button resetBtn = Small(L.T("Use profile colour"), ResetKeyColor);
		flowLayoutPanel4.Controls.AddRange(Small(L.T("Select all"), _view.SelectAll), Small(L.T("Clear selection"), _view.ClearSelection), colorBtn, resetBtn, _selInfo);
		_selInfo.Margin = new Padding(8, 6, 0, 0);
		flowLayoutPanel.Controls.Add(flowLayoutPanel4);
		_view.SelectionChanged += Sel;
		Sel();
		Button button = new Button
		{
			Text = L.T("Apply"),
			AutoSize = true,
			MinimumSize = new Size(120, 32),
			Margin = new Padding(0, 10, 0, 0)
		};
		button.Click += delegate
		{
			_applyTimer.Stop();
			_svc.Save();
			_svc.ApplyNow();
		};
		flowLayoutPanel.Controls.Add(button);
		flowLayoutPanel.Controls.Add(Hint(L.T("Changes are sent right away; Apply sends the whole profile to the keyboard again.")));
		return flowLayoutPanel;
		void IdleText()
		{
			idleVal.Text = ((cfg.IdleBrightness == 0) ? L.T("lights off") : (cfg.IdleBrightness + " %"));
		}
		void Row(string label, Control c)
		{
			t.Controls.Add(new Label
			{
				Text = label,
				AutoSize = true,
				Anchor = AnchorStyles.Left,
				Margin = new Padding(0, 8, 14, 0)
			});
			t.Controls.Add(c);
		}
		void Sel()
		{
			int count = _view.Selected.Count;
			Button button2 = colorBtn;
			bool enabled = (resetBtn.Enabled = count > 0);
			button2.Enabled = enabled;
			_selInfo.Text = ((count == 0) ? L.T("click keys to select them") : L.F("selected: {0}", count));
		}
	}

	private Color KeyColor(byte id)
	{
		Profile p = P;
		var (red, green, blue) = LogiService.ParseColor(p.KeyColors.TryGetValue(id.ToString("X2"), out string value) ? value : ((p.Keyboard.Effect == "off") ? "#000000" : p.Keyboard.Color));
		return Color.FromArgb(red, green, blue);
	}

	private void PickKeyColor()
	{
		if (_view.Selected.Count == 0)
		{
			return;
		}
		Color color = KeyColor(_view.Selected.First());
		using ColorDialog colorDialog = new ColorDialog
		{
			Color = color,
			FullOpen = true,
			AnyColor = true
		};
		if (colorDialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		string text = $"#{colorDialog.Color.R:X2}{colorDialog.Color.G:X2}{colorDialog.Color.B:X2}";
		if (_view.AllSelected)
		{
			P.Keyboard.Effect = "static";
			P.Keyboard.Color = text;
			P.KeyColors.Clear();
			_loading = true;
			_kb.Load(P.Keyboard);
			_loading = false;
		}
		else
		{
			foreach (byte item in _view.Selected)
			{
				P.KeyColors[item.ToString("X2")] = text;
			}
		}
		_view.Invalidate();
		Changed();
	}

	private void ResetKeyColor()
	{
		foreach (byte item in _view.Selected)
		{
			P.KeyColors.Remove(item.ToString("X2"));
		}
		_view.Invalidate();
		Changed();
	}

	private Control BuildMousePage()
	{
		FlowLayoutPanel obj = new FlowLayoutPanel
		{
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			AutoScroll = true,
			Padding = new Padding(0, 8, 0, 0),
			Controls = 
			{
				(Control?)Caption(L.T("Other mice")),
				(Control?)_mice,
				(Control?)Caption(L.T("Windows mouse settings (all mice)"))
			}
		};
		TableLayoutPanel t = new TableLayoutPanel
		{
			ColumnCount = 3,
			AutoSize = true,
			Margin = Padding.Empty
		};
		int row = 0;
		TrackBar speed = new TrackBar
		{
			Minimum = 1,
			Maximum = 20,
			Width = 260,
			TickFrequency = 1,
			Value = Math.Clamp(MouseSys.Speed, 1, 20)
		};
		Label speedVal = new Label
		{
			AutoSize = true,
			Margin = new Padding(0, 8, 0, 0),
			Text = speed.Value.ToString()
		};
		speed.ValueChanged += delegate
		{
			speedVal.Text = speed.Value.ToString();
			MouseSys.Speed = speed.Value;
		};
		Row(L.T("Pointer speed"), speed, speedVal);
		CheckBox precise = new CheckBox
		{
			Text = L.T("Enhance pointer precision (acceleration)"),
			AutoSize = true,
			Checked = MouseSys.Acceleration,
			Margin = new Padding(0, 6, 0, 0)
		};
		precise.CheckedChanged += delegate
		{
			MouseSys.Acceleration = precise.Checked;
		};
		Row("", precise);
		NumericUpDown lines = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 20m,
			Width = 70,
			Value = Math.Clamp(MouseSys.WheelLines, 1, 20)
		};
		lines.ValueChanged += delegate
		{
			MouseSys.WheelLines = (int)lines.Value;
		};
		Row(L.T("Wheel scrolls lines"), lines);
		TrackBar dbl = new TrackBar
		{
			Minimum = 200,
			Maximum = 900,
			Width = 260,
			TickFrequency = 100,
			SmallChange = 50,
			LargeChange = 100,
			Value = Math.Clamp(MouseSys.DoubleClickMs, 200, 900)
		};
		Label dblVal = new Label
		{
			AutoSize = true,
			Margin = new Padding(0, 8, 0, 0),
			Text = dbl.Value + " ms"
		};
		dbl.ValueChanged += delegate
		{
			dblVal.Text = dbl.Value + " ms";
			MouseSys.DoubleClickMs = dbl.Value;
		};
		Row(L.T("Double-click speed"), dbl, dblVal);
		CheckBox swap = new CheckBox
		{
			Text = L.T("Swap left and right buttons"),
			AutoSize = true,
			Checked = MouseSys.Swapped,
			Margin = new Padding(0, 6, 0, 0)
		};
		swap.CheckedChanged += delegate
		{
			MouseSys.Swapped = swap.Checked;
		};
		Row("", swap);
		obj.Controls.Add(t);
		obj.Controls.Add(Hint(L.T("These Windows settings apply to every mouse. DPI and lighting of a Razer mouse stay in its own memory / Razer software.")));
		return obj;
		void Row(string label, Control c, Control? extra = null)
		{
			t.Controls.Add(new Label
			{
				Text = label,
				AutoSize = true,
				Anchor = AnchorStyles.Left,
				Margin = new Padding(0, 8, 12, 0)
			}, 0, row);
			t.Controls.Add(c, 1, row);
			if (extra != null)
			{
				t.Controls.Add(extra, 2, row);
			}
			row++;
		}
	}

	private void LoadProfiles()
	{
		_loading = true;
		_profiles.Items.Clear();
		for (int i = 0; i < _svc.Config.Profiles.Count; i++)
		{
			string name = _svc.Config.Profiles[i].Name;
			_profiles.Items.Add((i < 3 && name != $"M{i + 1}") ? $"M{i + 1} · {name}" : name);
		}
		_profiles.Items.Add(L.T("+ Add profile…"));
		_profiles.SelectedIndex = Math.Clamp(_svc.Current, 0, _svc.Config.Profiles.Count - 1);
		_loading = false;
		LoadProfile();
	}

	private void LoadProfile()
	{
		_loading = true;
		Profile p = P;
		for (int i = 0; i < GKeys.Length; i++)
		{
			KeyAction keyAction = p.Key(GKeys[i]);
			_type[i].SelectedIndex = Math.Max(0, Array.IndexOf(TypeIds, keyAction.Type));
			_value[i].Text = keyAction.Value;
			UpdateKeyRow(i);
		}
		_kb.Load(p.Keyboard);
		_view.Invalidate();
		_brightness.Value = Math.Clamp(p.Brightness, 0, 100);
		_brightnessValue.Text = _brightness.Value + " %";
		_loading = false;
	}

	private void Changed()
	{
		_view.Invalidate();
		_applyTimer.Stop();
		_applyTimer.Start();
	}

	private void AddProfile()
	{
		string text = Prompt(L.T("New profile"), L.F("Profile {0}", _svc.Config.Profiles.Count + 1));
		if (text == null)
		{
			LoadProfiles();
			return;
		}
		Profile p = P;
		Profile item = new Profile
		{
			Name = text,
			Brightness = p.Brightness,
			Keyboard = new Lighting
			{
				Effect = p.Keyboard.Effect,
				Color = p.Keyboard.Color
			},
			Logo = new Lighting
			{
				Effect = p.Logo.Effect,
				Color = p.Logo.Color
			},
			KeyColors = new Dictionary<string, string>(p.KeyColors)
		};
		_svc.Config.Profiles.Add(item);
		_svc.SelectProfile(_svc.Config.Profiles.Count - 1);
		LoadProfiles();
	}

	private void RenameProfile()
	{
		string text = Prompt(L.T("Rename profile"), P.Name);
		if (text != null)
		{
			P.Name = text;
			_svc.Save();
			LoadProfiles();
		}
	}

	private void DeleteProfile()
	{
		if (_svc.Config.Profiles.Count > 1 && MessageBox.Show(this, L.F("Delete profile \"{0}\"?", P.Name), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
		{
			int num = Math.Clamp(_profiles.SelectedIndex, 0, _svc.Config.Profiles.Count - 1);
			_svc.Config.Profiles.RemoveAt(num);
			_svc.SelectProfile(Math.Clamp(num - 1, 0, _svc.Config.Profiles.Count - 1));
			LoadProfiles();
		}
	}

	private string? Prompt(string title, string value)
	{
		using Form form = new Form
		{
			Text = title,
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterParent,
			MinimizeBox = false,
			MaximizeBox = false,
			ShowInTaskbar = false,
			AutoScaleMode = AutoScaleMode.Dpi,
			AutoScaleDimensions = new SizeF(96f, 96f),
			Font = Font,
			ClientSize = new Size(320, 96),
			Padding = new Padding(12)
		};
		TextBox textBox = new TextBox
		{
			Text = value,
			Dock = DockStyle.Top
		};
		Button button = new Button
		{
			Text = "OK",
			DialogResult = DialogResult.OK,
			Size = new Size(90, 30),
			Location = new Point(122, 54)
		};
		Button button2 = new Button
		{
			Text = L.T("Cancel"),
			DialogResult = DialogResult.Cancel,
			Size = new Size(90, 30),
			Location = new Point(218, 54)
		};
		form.Controls.AddRange(textBox, button, button2);
		form.AcceptButton = button;
		form.CancelButton = button2;
		if (_dark)
		{
			Theme(form);
		}
		return (form.ShowDialog(this) == DialogResult.OK && textBox.Text.Trim().Length > 0) ? textBox.Text.Trim() : null;
	}

	private void OnSvcChanged()
	{
		if (base.IsHandleCreated && !base.IsDisposed)
		{
			BeginInvoke(RefreshStatus);
		}
	}

	private void RefreshStatus()
	{
		if (!base.IsDisposed)
		{
			string keyboardName = _svc.KeyboardName;
			HidppDevice keyboard = _svc.Keyboard;
			_kbTitle.Text = ((keyboardName.Length > 0) ? keyboardName : L.T("No Logitech G keyboard found"));
			string text = ((!_svc.Config.Enabled) ? L.T("Logitech control is off") : ((keyboard != null && keyboard.Online) ? ((!float.IsNaN(_svc.KeyboardBattery)) ? ($"{L.T("battery")} {_svc.KeyboardBattery:0}%" + (_svc.KeyboardCharging ? " ⚡" : "")) : L.T("connected")) : ((keyboardName.Length > 0) ? L.T("asleep or off") : "")));
			text = text + ((text.Length > 0) ? "   ·   " : "") + L.T("Profile") + ": " + _svc.Config.Profiles[_svc.Current].Name + (_svc.AutoSwitched ? (" " + L.T("(auto)")) : "");
			_status.Text = text;
			(string Name, float Battery, bool Charging, bool Online) m = _svc.CurrentMouse;
			_mouseTitle.Text = ((m.Name.Length > 0) ? m.Name : L.T("No mouse found"));
			_mouseStatus.Text = ((m.Name.Length == 0) ? "" : ((!m.Online) ? L.T("asleep or off") : ((!float.IsNaN(m.Battery)) ? ($"{L.T("battery")} {m.Battery:0}%" + (m.Charging ? " ⚡" : "")) : (L.T("connected") + " · " + L.T("battery level not available")))));
			bool flag = LogiService.GHubRunning();
			Label ghubWarn = _ghubWarn;
			bool visible = (_ghubClose.Visible = flag);
			ghubWarn.Visible = visible;
			_ghubWarn.Text = L.T("G HUB is running: it fights for the keyboard and doubles the macros.");
			List<string> list = (from d in _svc.Devices
				where d.IsMouse && d.Name != m.Name
				select "• " + d.Name + " — " + ((!d.Online) ? L.T("asleep or off") : ((d.BatteryPercent >= 0) ? $"{L.T("battery")} {d.BatteryPercent}%" : L.T("connected")))).Concat(from o in _svc.OtherMiceList
				where o.Name != m.Name
				select "• " + o.Name + " — " + ((o.BatteryPercent >= 0) ? $"{L.T("battery")} {o.BatteryPercent}%" : L.T("connected"))).ToList();
			_mice.Text = ((list.Count == 0) ? L.T("none") : string.Join(Environment.NewLine, list));
			if (!_loading && _profiles.Items.Count == _svc.Config.Profiles.Count && _profiles.SelectedIndex != _svc.Current)
			{
				_loading = true;
				_profiles.SelectedIndex = _svc.Current;
				_loading = false;
				LoadProfile();
			}
		}
	}

	private void CloseGHub()
	{
		if (MessageBox.Show(this, L.T("Close G HUB now? It will start again with Windows unless its autostart is turned off."), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}
		string[] array = new string[3] { "lghub_system_tray", "lghub", "lghub_agent" };
		for (int i = 0; i < array.Length; i++)
		{
			Process[] processesByName = Process.GetProcessesByName(array[i]);
			foreach (Process process in processesByName)
			{
				try
				{
					process.Kill();
				}
				catch (Exception ex)
				{
					Log.Write("logi: close G HUB: " + ex.Message);
				}
				process.Dispose();
			}
		}
		_svc.DevicesChanged();
		Task.Delay(1500).ContinueWith(delegate
		{
			OnSvcChanged();
		});
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		if (_dark)
		{
			Native.UseDarkTitleBar(base.Handle);
		}
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_svc.Changed -= OnSvcChanged;
		if (_applyTimer.Enabled)
		{
			_applyTimer.Stop();
			_svc.Save();
			_svc.ApplyNow();
		}
		_applyTimer.Dispose();
		base.OnFormClosed(e);
	}

	private static Label Section(string text)
	{
		return new Label
		{
			Text = text,
			AutoSize = true,
			Font = new Font("Segoe UI Semibold", 11f),
			Margin = new Padding(0, 4, 0, 6)
		};
	}

	private static Label Caption(string text)
	{
		return new Label
		{
			Text = text,
			AutoSize = true,
			Font = new Font("Segoe UI Semibold", 9.75f),
			Margin = new Padding(0, 4, 0, 4)
		};
	}

	private static Label Hint(string text)
	{
		return new Label
		{
			Text = text,
			AutoSize = true,
			MaximumSize = new Size(600, 0),
			ForeColor = Color.Gray,
			Margin = new Padding(0, 6, 0, 0)
		};
	}

	private static Button Small(string text, Action a)
	{
		Button button = new Button();
		button.Text = text;
		button.AutoSize = true;
		button.MinimumSize = new Size(34, 28);
		button.Margin = new Padding(0, 0, 4, 0);
		button.Click += delegate
		{
			a();
		};
		return button;
	}

	private static void Theme(Control c)
	{
		Color backColor = Color.FromArgb(32, 32, 32);
		Color backColor2 = Color.FromArgb(48, 48, 48);
		Color foreColor = Color.FromArgb(235, 235, 235);
		if (!(c is Button button))
		{
			if (!(c is RadioButton radioButton))
			{
				if (!(c is TextBox) && !(c is ListBox) && !(c is ComboBox) && !(c is NumericUpDown))
				{
					goto IL_0197;
				}
				c.BackColor = backColor2;
				c.ForeColor = foreColor;
				if (c is TextBox textBox)
				{
					textBox.BorderStyle = BorderStyle.FixedSingle;
				}
				if (c is ListBox listBox)
				{
					listBox.BorderStyle = BorderStyle.FixedSingle;
				}
				if (c is ComboBox comboBox)
				{
					comboBox.FlatStyle = FlatStyle.Flat;
				}
			}
			else
			{
				if (radioButton.Appearance != Appearance.Button)
				{
					goto IL_0197;
				}
				radioButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
				radioButton.FlatAppearance.CheckedBackColor = Color.FromArgb(0, 95, 184);
				radioButton.BackColor = Color.FromArgb(52, 52, 52);
				radioButton.ForeColor = Color.White;
			}
		}
		else if (button.Width <= 60 || button.Height >= 30 || button.FlatStyle != FlatStyle.Flat || button.Text.Length != 0)
		{
			button.FlatStyle = FlatStyle.Flat;
			button.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
			button.BackColor = Color.FromArgb(52, 52, 52);
			button.ForeColor = Color.White;
		}
		goto IL_01c9;
		IL_01c9:
		foreach (Control control in c.Controls)
		{
			Theme(control);
		}
		return;
		IL_0197:
		c.BackColor = backColor;
		if (c.ForeColor == SystemColors.ControlText || c.ForeColor == Control.DefaultForeColor)
		{
			c.ForeColor = foreColor;
		}
		goto IL_01c9;
	}
}
