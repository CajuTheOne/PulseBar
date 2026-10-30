using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace PulseBar;

internal sealed class BarRenderer : IDisposable
{
	private readonly StringFormat _near = MakeFormat(StringAlignment.Near);

	private readonly StringFormat _far = MakeFormat(StringAlignment.Far);

	private readonly StringFormat _center = MakeFormat(StringAlignment.Center);

	private readonly Bitmap _measureBmp = new Bitmap(1, 1);

	private readonly Graphics _mg;

	private (float, int, int, int, bool, int, string, bool, bool) _key = (-1f, -1, -1, -1, false, -1, "", false, false);

	private bool _showBat;

	private float _wIcon;

	private float _wBatPct;

	private float _wBatSmall;

	private Font? _label;

	private Font? _vlabel;

	private Font? _vlabelVram;

	private Font? _value;

	private Font? _small;

	private readonly Dictionary<string, Bitmap> _vcache = new Dictionary<string, Bitmap>();

	private Color _vcacheColor;

	private int _style;

	private bool _showVram;

	private float _k;

	private float _h;

	private float _wLabel;

	private float _wRam;

	private float _wVram;

	private float _wVLabel;

	private float _wVLabelVram;

	private float _wPct;

	private float _wTemp;

	private float _wGb;

	private float _colCg;

	private float _colMem;

	private float _pad;

	private float _gap1;

	private float _gap2;

	private float _sepPad;

	private float _sepW;

	private float _barW;

	private float _groupGap;

	private Limit _batKb = Settings.Defaults.KbBattery;

	private Limit _batMouse = Settings.Defaults.MouseBattery;

	public int Width { get; private set; }

	public BarRenderer()
	{
		_mg = Graphics.FromImage(_measureBmp);
		_mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
	}

	private static StringFormat MakeFormat(StringAlignment a)
	{
		StringFormat obj = (StringFormat)StringFormat.GenericTypographic.Clone();
		obj.Alignment = a;
		obj.LineAlignment = StringAlignment.Center;
		obj.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
		return obj;
	}

	public bool Configure(float dpiScale, int height, Settings s)
	{
		(float, int, int, int, bool, int, string, bool, bool) tuple = (dpiScale, height, s.Layout, s.TextSize, s.ShowRamGb, s.GroupGap, L.Code, s.ShowVram, s.ShowBattery);
		(float, int, int, int, bool, int, string, bool, bool) tuple2 = tuple;
		(float, int, int, int, bool, int, string, bool, bool) key = _key;
		if (tuple2.Item1 == key.Item1 && tuple2.Item2 == key.Item2 && tuple2.Item3 == key.Item3 && tuple2.Item4 == key.Item4 && tuple2.Item5 == key.Item5 && tuple2.Item6 == key.Item6 && tuple2.Item7 == key.Item7 && tuple2.Rest.Item1 == key.Rest.Item1 && tuple2.Rest.Item2 == key.Rest.Item2)
		{
			return false;
		}
		_key = tuple;
		DisposeFonts();
		_style = s.Layout;
		_showVram = s.ShowVram;
		_showBat = s.ShowBattery;
		bool flag = _style != 0;
		float num = (_k = dpiScale * s.TextScale);
		_h = height;
		_label = new Font("Segoe UI Semibold", 11f * num, GraphicsUnit.Pixel);
		_vlabel = new Font("Segoe UI Semibold", 12f * num, GraphicsUnit.Pixel);
		_vlabelVram = new Font("Segoe UI Semibold", 10f * num, GraphicsUnit.Pixel);
		_value = new Font("Segoe UI Semibold", (flag ? 12.5f : 13.5f) * num, GraphicsUnit.Pixel);
		_small = new Font("Segoe UI", (flag ? 11.5f : 12f) * num, GraphicsUnit.Pixel);
		_wLabel = Math.Max(W("CPU", _label), W("GPU", _label));
		_wRam = W("RAM", _label);
		_wVram = W("VRAM", _label);
		_wVLabel = MathF.Ceiling(_vlabel.Size * 0.9f);
		_wVLabelVram = MathF.Ceiling(_vlabelVram.Size * 0.9f);
		_wPct = W("100%", _value);
		_wTemp = Math.Max(W("100°C", _value), W(L.T("sleep"), _value));
		_wGb = W("99.9 GB", _small);
		_colCg = Math.Max(_wPct, _wTemp);
		_colMem = (s.ShowRamGb ? Math.Max(_wPct, _wGb) : _wPct);
		_pad = 4f * num;
		_sepPad = 10f * num;
		_sepW = Math.Max(1f, MathF.Round(dpiScale));
		_barW = Math.Max(3f, MathF.Round(4.5f * dpiScale, MidpointRounding.AwayFromZero));
		_gap1 = 5f * num;
		_gap2 = 5f * num;
		_groupGap = MathF.Round((float)s.GroupGap * dpiScale);
		int num2 = (_showVram ? 4 : 3);
		float num3 = _pad * 2f;
		_wIcon = MathF.Ceiling(16f * num);
		_wBatPct = W("100%", _value);
		_wBatSmall = W("100%", _small);
		if (!flag)
		{
			float num4 = _wLabel + _gap1 + _wPct + _gap2 + _wTemp;
			float num5 = _gap1 + _wPct + (s.ShowRamGb ? (_gap2 + _wGb) : 0f);
			num3 += num4 * 2f + _wRam + num5 + (_showVram ? (_wVram + num5) : 0f) + (float)(num2 - 1) * (2f * _sepPad + _sepW);
			if (_showBat)
			{
				num3 += 2f * _sepPad + _sepW + (_wIcon + _gap1 + _wBatPct) * 2f + _gap2 * 2f;
			}
		}
		else
		{
			num3 += HeadWidth(_wVLabel) * 3f + (_showVram ? HeadWidth(_wVLabelVram) : 0f) + _colCg * 2f + _colMem * (float)(num2 - 2) + (float)(num2 - 1) * _groupGap;
			if (_showBat)
			{
				num3 += _groupGap + _wIcon + _gap1 + _wBatSmall;
			}
		}
		Width = (int)MathF.Ceiling(num3);
		return true;
	}

	private float HeadWidth(float labelSlot)
	{
		return labelSlot + 4f * _k + _barW + 6f * _k;
	}

	private float W(string text, Font f)
	{
		return _mg.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
	}

	public static int Signature(in Readings r, Settings s, bool light)
	{
		int value = HashCode.Combine(Round(r.KbBattery), Round(r.MouseBattery), r.KbCharging, r.MouseCharging);
		return HashCode.Combine(HashCode.Combine(Round(r.CpuLoad), Round(r.CpuTemp), Round(r.GpuLoad), Round(r.GpuTemp), r.GpuAsleep), Round(r.VramLoad), Round(r.VramUsedGb * 10f), Round(r.RamLoad), Round(r.RamUsedGb * 10f), s.Version, light, value);
	}

	private static int Round(float v)
	{
		if (!float.IsNaN(v))
		{
			return (int)MathF.Round(v);
		}
		return int.MinValue;
	}

	public void Draw(Graphics g, in Readings r, Settings s, Palette p)
	{
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		_batKb = s.KbBattery;
		_batMouse = s.MouseBattery;
		string text = (r.GpuAsleep ? L.T("sleep") : Temp(r.GpuTemp));
		Color color = (r.GpuAsleep ? p.NoData : p.Level(r.GpuTemp, s.GpuTemp));
		if (_style == 0)
		{
			float pad = _pad;
			pad = Block(g, pad, "CPU", r.CpuLoad, s.CpuLoad, Temp(r.CpuTemp), p.Level(r.CpuTemp, s.CpuTemp), p);
			pad = Separator(g, pad, p);
			pad = Block(g, pad, "GPU", r.GpuLoad, s.GpuLoad, text, color, p);
			pad = Separator(g, pad, p);
			if (_showVram)
			{
				pad = MemBlock(g, pad, "VRAM", _label, _wVram, r.VramLoad, s.Vram, r.VramUsedGb, s, p);
				pad = Separator(g, pad, p);
			}
			pad = MemBlock(g, pad, "RAM", _label, _wRam, r.RamLoad, s.Ram, r.RamUsedGb, s, p);
			if (_showBat)
			{
				pad = Separator(g, pad, p);
				pad = Battery(g, pad, keyboard: true, r.KbBattery, r.KbCharging, _value, _wBatPct, _h / 2f, p) + _gap2 * 2f;
				Battery(g, pad, keyboard: false, r.MouseBattery, r.MouseCharging, _value, _wBatPct, _h / 2f, p);
			}
		}
		else
		{
			float pad2 = _pad;
			pad2 = Group(g, pad2, "CPU", _vlabel, _wVLabel, r.CpuLoad, s.CpuLoad, Temp(r.CpuTemp), p.Level(r.CpuTemp, s.CpuTemp), _value, _colCg, p) + _groupGap;
			pad2 = Group(g, pad2, "GPU", _vlabel, _wVLabel, r.GpuLoad, s.GpuLoad, text, color, _value, _colCg, p) + _groupGap;
			if (_showVram)
			{
				pad2 = Group(g, pad2, "VRAM", _vlabelVram, _wVLabelVram, r.VramLoad, s.Vram, s.ShowRamGb ? Gb(r.VramUsedGb) : null, p.Dim, _small, _colMem, p) + _groupGap;
			}
			pad2 = Group(g, pad2, "RAM", _vlabel, _wVLabel, r.RamLoad, s.Ram, s.ShowRamGb ? Gb(r.RamUsedGb) : null, p.Dim, _small, _colMem, p);
			if (_showBat)
			{
				float num = 8f * _k;
				pad2 += _groupGap;
				Battery(g, pad2, keyboard: true, r.KbBattery, r.KbCharging, _small, _wBatSmall, _h / 2f - num, p);
				Battery(g, pad2, keyboard: false, r.MouseBattery, r.MouseCharging, _small, _wBatSmall, _h / 2f + num, p);
			}
		}
	}

	private float Battery(Graphics g, float x, bool keyboard, float pct, bool charging, Font font, float col, float cy, Palette p)
	{
		Limit limit = (keyboard ? _batKb : _batMouse);
		Color c = (float.IsNaN(pct) ? p.NoData : (charging ? p.Ok : ((pct <= (float)limit.Hot) ? p.Hot : ((pct <= (float)limit.Warn) ? p.Warn : p.Ok))));
		SmoothingMode smoothingMode = g.SmoothingMode;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		using (Pen pen = new Pen(p.Label, Math.Max(1f, 1.2f * _k)))
		{
			if (keyboard)
			{
				float num = 15f * _k;
				float num2 = 9f * _k;
				float num3 = x + (_wIcon - num) / 2f;
				float num4 = cy - num2 / 2f;
				using (GraphicsPath path = RoundRect(num3, num4, num, num2, 1.8f * _k))
				{
					g.DrawPath(pen, path);
				}
				using SolidBrush brush = new SolidBrush(p.Label);
				float num5 = 1.4f * _k;
				float num6 = 2.6f * _k;
				for (int i = 0; i < 2; i++)
				{
					for (int j = 0; j < 5; j++)
					{
						g.FillRectangle(brush, num3 + 1.9f * _k + (float)j * num6, num4 + 1.8f * _k + (float)i * 2.2f * _k, num5, num5);
					}
				}
				g.FillRectangle(brush, num3 + 4.4f * _k, num4 + 6.3f * _k, 6.2f * _k, num5);
			}
			else
			{
				float num7 = 9f * _k;
				float num8 = 13f * _k;
				float num9 = x + (_wIcon - num7) / 2f;
				float num10 = cy - num8 / 2f;
				using (GraphicsPath path2 = RoundRect(num9, num10, num7, num8, 4.5f * _k))
				{
					g.DrawPath(pen, path2);
				}
				g.DrawLine(pen, num9 + num7 / 2f, num10 + 1.5f * _k, num9 + num7 / 2f, num10 + 4.5f * _k);
			}
		}
		g.SmoothingMode = smoothingMode;
		x += _wIcon + _gap1;
		Text(g, Pct(pct), font, c, x, col, _far, cy);
		return x + col;
	}

	private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
	{
		GraphicsPath graphicsPath = new GraphicsPath();
		float num = Math.Min(r * 2f, Math.Min(w, h));
		graphicsPath.AddArc(x, y, num, num, 180f, 90f);
		graphicsPath.AddArc(x + w - num, y, num, num, 270f, 90f);
		graphicsPath.AddArc(x + w - num, y + h - num, num, num, 0f, 90f);
		graphicsPath.AddArc(x, y + h - num, num, num, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}

	private float Block(Graphics g, float x, string label, float load, Limit limit, string temp, Color tempColor, Palette p)
	{
		Text(g, label, _label, p.Label, x, _wLabel, _near);
		x += _wLabel + _gap1;
		Text(g, Pct(load), _value, p.Level(load, limit), x, _wPct, _far);
		x += _wPct + _gap2;
		Text(g, temp, _value, tempColor, x, _wTemp, _far);
		return x + _wTemp;
	}

	private float MemBlock(Graphics g, float x, string label, Font labelFont, float labelWidth, float load, Limit limit, float gb, Settings s, Palette p)
	{
		Text(g, label, labelFont, p.Label, x, labelWidth, _near);
		x += labelWidth + _gap1;
		Text(g, Pct(load), _value, p.Level(load, limit), x, _wPct, _far);
		x += _wPct;
		if (!s.ShowRamGb)
		{
			return x;
		}
		Text(g, Gb(gb), _small, p.Dim, x + _gap2, _wGb, _far);
		return x + _gap2 + _wGb;
	}

	private float Separator(Graphics g, float x, Palette p)
	{
		x += _sepPad;
		float num = MathF.Round(_h * 0.42f);
		using (SolidBrush brush = new SolidBrush(p.Sep))
		{
			g.FillRectangle(brush, MathF.Round(x), MathF.Round((_h - num) / 2f), _sepW, num);
		}
		return x + _sepW + _sepPad;
	}

	private float Group(Graphics g, float x, string label, Font labelFont, float labelSlot, float load, Limit limit, string? second, Color secondColor, Font secondFont, float col, Palette p)
	{
		Color color = p.Level(load, limit);
		VerticalText(g, label, labelFont, p.Label, x, labelSlot);
		x = Bar(g, x + labelSlot + 4f * _k, load, color, p) + 6f * _k;
		float num = 8f * _k;
		if (second == null)
		{
			Text(g, Pct(load), _value, color, x, col, _far);
		}
		else
		{
			Text(g, Pct(load), _value, color, x, col, _far, _h / 2f - num);
			Text(g, second, secondFont, secondColor, x, col, _far, _h / 2f + num);
		}
		return x + col;
	}

	private float Bar(Graphics g, float x, float load, Color color, Palette p)
	{
		float num = MathF.Round(_h * 0.62f);
		float num2 = MathF.Round((_h - num) / 2f);
		float x2 = MathF.Round(x);
		using (SolidBrush brush = new SolidBrush(p.Track))
		{
			g.FillRectangle(brush, x2, num2, _barW, num);
		}
		if (!float.IsNaN(load) && load > 0f)
		{
			float num3 = Math.Max(1f, MathF.Round(num * load / 100f));
			using SolidBrush brush2 = new SolidBrush(color);
			g.FillRectangle(brush2, x2, num2 + num - num3, _barW, num3);
		}
		return x + _barW;
	}

	private void VerticalText(Graphics g, string s, Font f, Color c, float x, float w)
	{
		if (c != _vcacheColor)
		{
			ClearVerticalCache();
		}
		_vcacheColor = c;
		if (!_vcache.TryGetValue(s, out Bitmap value))
		{
			Bitmap bitmap = (_vcache[s] = RenderVertical(s, f, c, 2));
			value = bitmap;
		}
		float num = f.Size * (float)f.FontFamily.GetCellAscent(f.Style) / (float)f.FontFamily.GetEmHeight(f.Style);
		float num2 = 2f + num - 0.35f * f.Size;
		float num3 = (float)(value.Height - 2) - W(s, f) / 2f;
		int x2 = (int)MathF.Round(x + w / 2f - num2);
		int y = (int)MathF.Round(_h / 2f - num3);
		InterpolationMode interpolationMode = g.InterpolationMode;
		PixelOffsetMode pixelOffsetMode = g.PixelOffsetMode;
		g.InterpolationMode = InterpolationMode.NearestNeighbor;
		g.PixelOffsetMode = PixelOffsetMode.Half;
		g.DrawImage(value, new Rectangle(x2, y, value.Width, value.Height));
		InterpolationMode interpolationMode2 = interpolationMode;
		PixelOffsetMode pixelOffsetMode2 = pixelOffsetMode;
		g.InterpolationMode = interpolationMode2;
		g.PixelOffsetMode = pixelOffsetMode2;
	}

	private Bitmap RenderVertical(string s, Font f, Color c, int pad)
	{
		int width = (int)MathF.Ceiling(W(s, f)) + pad * 2;
		int height = (int)MathF.Ceiling(f.GetHeight(_mg)) + pad * 2;
		Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			using SolidBrush brush = new SolidBrush(c);
			graphics.Clear(Color.Transparent);
			graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			graphics.DrawString(s, f, brush, pad, pad, StringFormat.GenericTypographic);
		}
		bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
		return bitmap;
	}

	private void ClearVerticalCache()
	{
		foreach (Bitmap value in _vcache.Values)
		{
			value.Dispose();
		}
		_vcache.Clear();
	}

	private void Text(Graphics g, string s, Font f, Color c, float x, float w, StringFormat fmt, float cy = float.NaN)
	{
		if (float.IsNaN(cy))
		{
			cy = _h / 2f;
		}
		cy -= 0.065f * f.Size;
		float num = f.Size * 1.6f;
		float num2 = 8f;
		RectangleF layoutRectangle = ((fmt == _far) ? new RectangleF(x - num2, cy - num / 2f, w + num2, num) : new RectangleF(x, cy - num / 2f, w + num2, num));
		using SolidBrush brush = new SolidBrush(c);
		g.DrawString(s, f, brush, layoutRectangle, fmt);
	}

	private static string Pct(float v)
	{
		if (!float.IsNaN(v))
		{
			return $"{(int)MathF.Round(v)}%";
		}
		return "—";
	}

	private static string Temp(float v)
	{
		if (!float.IsNaN(v))
		{
			return $"{(int)MathF.Round(v)}°C";
		}
		return "—";
	}

	private static string Gb(float v)
	{
		if (!float.IsNaN(v))
		{
			return v.ToString("0.0 'GB'", CultureInfo.InvariantCulture);
		}
		return "—";
	}

	private void DisposeFonts()
	{
		ClearVerticalCache();
		_label?.Dispose();
		_vlabel?.Dispose();
		_vlabelVram?.Dispose();
		_value?.Dispose();
		_small?.Dispose();
	}

	public void Dispose()
	{
		DisposeFonts();
		_near.Dispose();
		_far.Dispose();
		_center.Dispose();
		_mg.Dispose();
		_measureBmp.Dispose();
	}
}
