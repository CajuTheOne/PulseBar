using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PulseBar;

// Замеры за последнюю минуту для графика в подсказке.
internal sealed class History
{
	public const long WindowMs = 60000;

	private readonly Queue<(long T, float V)> _q = new Queue<(long, float)>();

	public void Add(long t, float v)
	{
		_q.Enqueue((t, v));
		while (_q.Count > 0 && t - _q.Peek().T > WindowMs)
		{
			_q.Dequeue();
		}
	}

	public IEnumerable<(long T, float V)> Items => _q;
}

// Подсказка над строкой при наведении на блок: заголовок, строки «название — значение» и график за 60 с.
// Не забирает фокус и пропускает мышь насквозь.
internal sealed class TooltipForm : Form
{
	public sealed record Series(History Data, Color Color, string Name, float Max);

	private string _title = "";

	private List<(string Label, string Value, Color? Color)> _rows = new List<(string, string, Color?)>();

	private List<Series> _series = new List<Series>();

	private bool _dark = true;

	private readonly Font _titleFont = new Font("Segoe UI Semibold", 9.5f);

	private readonly Font _font = new Font("Segoe UI", 9f);

	private readonly Font _smallFont = new Font("Segoe UI", 8f);

	protected override CreateParams CreateParams
	{
		get
		{
			CreateParams cp = base.CreateParams;
			cp.Style = unchecked((int)0x8000_0000);
			cp.ExStyle |= 0x0800_0000 | 0x80 | 0x8 | 0x20;
			return cp;
		}
	}

	protected override bool ShowWithoutActivation => true;

	public TooltipForm()
	{
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		DoubleBuffered = true;
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		int round = 2;
		DwmSetWindowAttribute(Handle, 33, ref round, 4);
	}

	public void SetContent(string title, List<(string, string, Color?)> rows, List<Series> series, bool dark)
	{
		_title = title;
		_rows = rows;
		_series = series;
		_dark = dark;
		BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(249, 249, 249);
		Size = Measure();
		Invalidate();
	}

	private float K => DeviceDpi / 96f;

	private Size Measure()
	{
		using Graphics g = CreateGraphics();
		float k = K;
		float w = g.MeasureString(_title, _titleFont).Width;
		float labelW = 0f;
		float valueW = 0f;
		foreach (var (label, value, _) in _rows)
		{
			labelW = Math.Max(labelW, g.MeasureString(label, _font).Width);
			valueW = Math.Max(valueW, g.MeasureString(value, _font).Width);
		}
		w = Math.Max(w, labelW + 12f * k + valueW);
		if (_series.Count > 0)
		{
			w = Math.Max(w, 240f * k);
		}
		float h = 10f * k + _titleFont.GetHeight(g) + 4f * k + _rows.Count * _font.GetHeight(g) + 10f * k;
		if (_series.Count > 0)
		{
			h += 64f * k + _smallFont.GetHeight(g) + 6f * k;
		}
		return new Size((int)MathF.Ceiling(w + 24f * k), (int)MathF.Ceiling(h));
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Graphics g = e.Graphics;
		g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
		float k = K;
		Color text = _dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(28, 28, 28);
		Color dim = _dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(96, 96, 96);
		using (Pen border = new Pen(_dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(214, 214, 214)))
		{
			g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
		}
		float x = 12f * k;
		float y = 10f * k;
		using (SolidBrush b = new SolidBrush(text))
		{
			g.DrawString(_title, _titleFont, b, x, y);
		}
		y += _titleFont.GetHeight(g) + 4f * k;
		float valueX = Width - 12f * k;
		using StringFormat right = new StringFormat { Alignment = StringAlignment.Far };
		foreach (var (label, value, color) in _rows)
		{
			using (SolidBrush b = new SolidBrush(dim))
			{
				g.DrawString(label, _font, b, x, y);
			}
			using (SolidBrush b = new SolidBrush(color ?? text))
			{
				g.DrawString(value, _font, b, new RectangleF(x, y, valueX - x, _font.GetHeight(g) + 2f), right);
			}
			y += _font.GetHeight(g);
		}
		if (_series.Count == 0)
		{
			return;
		}
		y += 8f * k;
		RectangleF chart = new RectangleF(x, y, Width - 24f * k, 60f * k);
		using (SolidBrush b = new SolidBrush(_dark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(240, 240, 240)))
		{
			g.FillRectangle(b, chart);
		}
		using (Pen grid = new Pen(_dark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(222, 222, 222)))
		{
			g.DrawLine(grid, chart.Left, chart.Top + chart.Height / 2f, chart.Right, chart.Top + chart.Height / 2f);
		}
		long now = Environment.TickCount64;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		foreach (Series s in _series)
		{
			List<PointF> pts = new List<PointF>();
			foreach (var (t, v) in s.Data.Items)
			{
				if (float.IsNaN(v))
				{
					continue;
				}
				float px = chart.Right - (now - t) * chart.Width / History.WindowMs;
				float py = chart.Bottom - Math.Clamp(v / s.Max, 0f, 1f) * chart.Height;
				pts.Add(new PointF(Math.Max(chart.Left, px), py));
			}
			if (pts.Count >= 2)
			{
				using Pen pen = new Pen(s.Color, 1.6f * k);
				g.DrawLines(pen, pts.ToArray());
			}
		}
		g.SmoothingMode = SmoothingMode.None;
		y = chart.Bottom + 4f * k;
		float lx = x;
		foreach (Series s in _series)
		{
			using (SolidBrush b = new SolidBrush(s.Color))
			{
				g.FillRectangle(b, lx, y + 5f * k, 10f * k, 3f * k);
			}
			lx += 14f * k;
			using (SolidBrush b = new SolidBrush(dim))
			{
				g.DrawString(s.Name, _smallFont, b, lx, y);
			}
			lx += g.MeasureString(s.Name, _smallFont).Width + 10f * k;
		}
		using (SolidBrush b = new SolidBrush(dim))
		{
			g.DrawString(L.T("60 s"), _smallFont, b, new RectangleF(x, y, chart.Width, 20f * k), right);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_titleFont.Dispose();
			_font.Dispose();
			_smallFont.Dispose();
		}
		base.Dispose(disposing);
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
