using System;
using System.Drawing;
using System.Windows.Forms;

namespace PulseBar;

internal sealed class ThresholdsForm : Form
{
	private static readonly bool[] IsTemp = new bool[6] { false, true, false, true, false, false };

	private readonly Settings _s;

	private readonly bool _dark;

	private readonly NumericUpDown[] _warn = new NumericUpDown[6];

	private readonly NumericUpDown[] _hot = new NumericUpDown[6];

	private readonly NumericUpDown[] _batWarn = new NumericUpDown[2];

	private readonly NumericUpDown[] _batHot = new NumericUpDown[2];

	private static string[] Names => new string[6]
	{
		L.T("CPU load, %"),
		L.T("CPU temperature, °C"),
		L.T("GPU load, %"),
		L.T("GPU temperature, °C"),
		L.T("VRAM load, %"),
		L.T("RAM load, %")
	};

	private static NumericUpDown BatNumber(int value)
	{
		return new NumericUpDown
		{
			Minimum = 0m,
			Maximum = 99m,
			Width = 72,
			TextAlign = HorizontalAlignment.Right,
			Margin = new Padding(0, 3, 10, 3),
			Value = Math.Clamp(value, 0, 99)
		};
	}

	public ThresholdsForm(Settings s, bool dark)
	{
		_s = s;
		_dark = dark;
		Text = L.T("PulseBar — thresholds");
		base.FormBorderStyle = FormBorderStyle.FixedDialog;
		base.MaximizeBox = (base.MinimizeBox = false);
		base.ShowInTaskbar = false;
		base.TopMost = true;
		base.StartPosition = FormStartPosition.CenterScreen;
		base.AutoScaleMode = AutoScaleMode.Dpi;
		base.AutoScaleDimensions = new SizeF(96f, 96f);
		Font = new Font("Segoe UI", 9.75f);
		AutoSize = true;
		base.AutoSizeMode = AutoSizeMode.GrowAndShrink;
		base.Padding = new Padding(16, 12, 16, 12);
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			ColumnCount = 3,
			AutoSize = true,
			Margin = Padding.Empty
		};
		tableLayoutPanel.Controls.Add(Header(""), 0, 0);
		tableLayoutPanel.Controls.Add(Header(L.T("Yellow from")), 1, 0);
		tableLayoutPanel.Controls.Add(Header(L.T("Red from")), 2, 0);
		Limit[] array = Current();
		string[] names = Names;
		for (int i = 0; i < names.Length; i++)
		{
			tableLayoutPanel.Controls.Add(new Label
			{
				Text = names[i],
				AutoSize = true,
				Anchor = AnchorStyles.Left,
				Margin = new Padding(0, 6, 18, 6)
			}, 0, i + 1);
			tableLayoutPanel.Controls.Add(_warn[i] = Number(array[i].Warn, IsTemp[i]), 1, i + 1);
			tableLayoutPanel.Controls.Add(_hot[i] = Number(array[i].Hot, IsTemp[i]), 2, i + 1);
		}
		int num = names.Length + 1;
		tableLayoutPanel.Controls.Add(Header(L.T("Battery")), 0, num);
		tableLayoutPanel.Controls.Add(Header(L.T("Yellow at or below")), 1, num);
		tableLayoutPanel.Controls.Add(Header(L.T("Red at or below")), 2, num);
		tableLayoutPanel.GetControlFromPosition(0, num).Margin = new Padding(0, 12, 10, 4);
		tableLayoutPanel.GetControlFromPosition(1, num).Margin = new Padding(0, 12, 10, 4);
		tableLayoutPanel.GetControlFromPosition(2, num).Margin = new Padding(0, 12, 10, 4);
		Limit[] array2 = new Limit[2] { _s.KbBattery, _s.MouseBattery };
		string[] array3 = new string[2]
		{
			L.T("Keyboard charge, %"),
			L.T("Mouse charge, %")
		};
		for (int j = 0; j < 2; j++)
		{
			tableLayoutPanel.Controls.Add(new Label
			{
				Text = array3[j],
				AutoSize = true,
				Anchor = AnchorStyles.Left,
				Margin = new Padding(0, 6, 18, 6)
			}, 0, num + 1 + j);
			tableLayoutPanel.Controls.Add(_batWarn[j] = BatNumber(array2[j].Warn), 1, num + 1 + j);
			tableLayoutPanel.Controls.Add(_batHot[j] = BatNumber(array2[j].Hot), 2, num + 1 + j);
		}
		Label value = new Label
		{
			Text = L.T("Below yellow is normal, from red is overload."),
			AutoSize = true,
			Margin = new Padding(0, 10, 0, 4),
			ForeColor = (dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText)
		};
		Button button = Button("OK");
		button.DialogResult = DialogResult.OK;
		Button button2 = Button(L.T("Cancel"));
		button2.DialogResult = DialogResult.Cancel;
		Button button3 = Button(L.T("Defaults"));
		button3.Click += delegate
		{
			Fill(new Limit[6]
			{
				Settings.Defaults.CpuLoad,
				Settings.Defaults.CpuTemp,
				Settings.Defaults.GpuLoad,
				Settings.Defaults.GpuTemp,
				Settings.Defaults.Vram,
				Settings.Defaults.Ram
			});
			Limit[] array4 = new Limit[2]
			{
				Settings.Defaults.KbBattery,
				Settings.Defaults.MouseBattery
			};
			for (int k = 0; k < 2; k++)
			{
				_batWarn[k].Value = array4[k].Warn;
				_batHot[k].Value = array4[k].Hot;
			}
		};
		base.AcceptButton = button;
		base.CancelButton = button2;
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			FlowDirection = FlowDirection.RightToLeft,
			AutoSize = true,
			Anchor = AnchorStyles.Right,
			Margin = new Padding(0, 10, 0, 0)
		};
		flowLayoutPanel.Controls.AddRange(button, button2, button3);
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			ColumnCount = 1,
			AutoSize = true,
			Dock = DockStyle.Fill
		};
		tableLayoutPanel2.Controls.Add(tableLayoutPanel);
		tableLayoutPanel2.Controls.Add(value);
		tableLayoutPanel2.Controls.Add(flowLayoutPanel);
		base.Controls.Add(tableLayoutPanel2);
		if (dark)
		{
			ApplyDark(this);
		}
	}

	private Limit[] Current()
	{
		return new Limit[6] { _s.CpuLoad, _s.CpuTemp, _s.GpuLoad, _s.GpuTemp, _s.Vram, _s.Ram };
	}

	private void Fill(Limit[] limits)
	{
		for (int i = 0; i < limits.Length; i++)
		{
			_warn[i].Value = Math.Clamp(limits[i].Warn, _warn[i].Minimum, _warn[i].Maximum);
			_hot[i].Value = Math.Clamp(limits[i].Hot, _hot[i].Minimum, _hot[i].Maximum);
		}
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		if (_dark)
		{
			Native.UseDarkTitleBar(base.Handle);
		}
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		base.OnFormClosing(e);
		if (base.DialogResult != DialogResult.OK)
		{
			return;
		}
		Limit[] array = new Limit[_warn.Length];
		for (int i = 0; i < array.Length; i++)
		{
			int num = (int)_warn[i].Value;
			int num2 = (int)_hot[i].Value;
			if (num2 <= num)
			{
				num2 = num + 1;
			}
			array[i] = new Limit(num, num2);
		}
		Settings s = _s;
		Settings s2 = _s;
		Settings s3 = _s;
		Settings s4 = _s;
		Settings s5 = _s;
		Settings s6 = _s;
		Limit cpuLoad = array[0];
		Limit cpuTemp = array[1];
		Limit gpuLoad = array[2];
		Limit gpuTemp = array[3];
		Limit vram = array[4];
		Limit ram = array[5];
		s.CpuLoad = cpuLoad;
		s2.CpuTemp = cpuTemp;
		s3.GpuLoad = gpuLoad;
		s4.GpuTemp = gpuTemp;
		s5.Vram = vram;
		s6.Ram = ram;
		Limit[] array2 = new Limit[2];
		for (int j = 0; j < 2; j++)
		{
			int num3 = (int)_batWarn[j].Value;
			int num4 = (int)_batHot[j].Value;
			if (num4 >= num3)
			{
				num4 = Math.Max(0, num3 - 1);
			}
			array2[j] = new Limit(num3, num4);
		}
		_s.KbBattery = array2[0];
		_s.MouseBattery = array2[1];
	}

	private Label Header(string text)
	{
		return new Label
		{
			Text = text,
			AutoSize = true,
			Anchor = AnchorStyles.Left,
			Margin = new Padding(0, 0, 10, 4),
			ForeColor = (_dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText)
		};
	}

	private static NumericUpDown Number(int value, bool temp)
	{
		NumericUpDown numericUpDown = new NumericUpDown
		{
			Minimum = ((!temp) ? 1 : 30),
			Maximum = (temp ? 120 : 100),
			Width = 72,
			TextAlign = HorizontalAlignment.Right,
			Margin = new Padding(0, 3, 10, 3)
		};
		numericUpDown.Value = Math.Clamp(value, numericUpDown.Minimum, numericUpDown.Maximum);
		return numericUpDown;
	}

	private static Button Button(string text)
	{
		return new Button
		{
			Text = text,
			AutoSize = true,
			MinimumSize = new Size(88, 30),
			Margin = new Padding(8, 0, 0, 0)
		};
	}

	private static void ApplyDark(Control c)
	{
		Color backColor = Color.FromArgb(32, 32, 32);
		c.BackColor = backColor;
		if (c.ForeColor == SystemColors.ControlText)
		{
			c.ForeColor = Color.FromArgb(235, 235, 235);
		}
		foreach (Control control in c.Controls)
		{
			if (!(control is NumericUpDown numericUpDown))
			{
				if (control is Button button)
				{
					button.FlatStyle = FlatStyle.Flat;
					button.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
					button.BackColor = Color.FromArgb(52, 52, 52);
					button.ForeColor = Color.White;
				}
				else
				{
					ApplyDark(control);
				}
			}
			else
			{
				numericUpDown.BackColor = Color.FromArgb(48, 48, 48);
				numericUpDown.ForeColor = Color.White;
				numericUpDown.BorderStyle = BorderStyle.FixedSingle;
			}
		}
	}
}
