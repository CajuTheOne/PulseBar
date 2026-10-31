using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;

namespace PulseBar;

internal sealed class BarRenderer : IDisposable
{
	private readonly StringFormat _near = MakeFormat(StringAlignment.Near);

	private readonly StringFormat _far = MakeFormat(StringAlignment.Far);

	private readonly StringFormat _center = MakeFormat(StringAlignment.Center);

	private readonly Bitmap _measureBmp = new Bitmap(1, 1);

	private readonly Graphics _mg;

	private (float, int, int, int, bool, int, int, int, string, int, int, int) _key = (-1f, -1, -1, -1, false, -1, -1, -1, "", -1, -1, -1);

	private int _claudeBarLength;

	private int _claudeBarHeight;

	private int _claudeSegments;

	private bool _showRamGb;

	private int _claudeStyle;

	private float _snapGap;

	private float _sepGap;

	private float _wClaudeText;

	private float _wClaude;

	private float _wGpt;

	private float _wGrok;

	private float _wDs;

	private float _wBalance;

	private float _wTag;

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

	// Шрифты и размеры элементов для текущего масштаба. true — что-то изменилось, нужна перерисовка.
	// Элементы: cpu, gpu, vram, ram, claude, battery и разделители sep…; раскладку по панели делает Overlay.
	public bool Configure(float dpiScale, int height, Settings s)
	{
		(float, int, int, int, bool, int, int, int, string, int, int, int) tuple = (dpiScale, height, s.Layout, s.TextSize, s.ShowRamGb, s.ClaudeStyle, s.SnapGap, s.SepGap, L.Code, s.ClaudeBarLength, s.ClaudeBarHeight, s.ClaudeSegments);
		if (tuple == _key)
		{
			return false;
		}
		_key = tuple;
		DisposeFonts();
		_style = s.Layout;
		_showRamGb = s.ShowRamGb;
		_claudeStyle = s.ClaudeStyle;
		_claudeBarLength = s.ClaudeBarLength;
		_claudeBarHeight = s.ClaudeBarHeight;
		_claudeSegments = s.ClaudeSegments;
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
		_snapGap = MathF.Round((float)s.SnapGap * dpiScale);
		_sepGap = MathF.Round((float)s.SepGap * dpiScale);
		_wIcon = MathF.Ceiling(16f * num);
		_wBatPct = W("100%", _value);
		_wBatSmall = W("100%", _small);
		_wClaude = W("CLAUDE", _label);
		_wGpt = W("GPT", _label);
		_wGrok = W("GROK", _label);
		_wDs = W("DS", _label);
		_wBalance = W("$999.99", _value);
		_wTag = Math.Max(W("5h", _label), W("7d", _label));
		_wClaudeText = W("100% · 23h", _small);
		return true;
	}

	public static bool IsSeparator(string id)
	{
		return id.StartsWith("sep", StringComparison.Ordinal);
	}

	// Расстояние между соседними элементами группы: у разделителя — своё.
	public float Gap(string left, string right)
	{
		if (!IsSeparator(left) && !IsSeparator(right))
		{
			return _snapGap;
		}
		return _sepGap;
	}

	public float Pad => _pad;

	public float ElementWidth(string id)
	{
		if (IsSeparator(id))
		{
			return _sepW;
		}
		bool two = _style != 0;
		float num = _gap1 + _wPct + (_showRamGb ? (_gap2 + _wGb) : 0f);
		return id switch
		{
			"cpu" or "gpu" => two ? (HeadWidth(_wVLabel) + _colCg) : (_wLabel + _gap1 + _wPct + _gap2 + _wTemp),
			"vram" => two ? (HeadWidth(_wVLabelVram) + _colMem) : (_wVram + num),
			"ram" => two ? (HeadWidth(_wVLabel) + _colMem) : (_wRam + num),
			"claude" => (_claudeStyle == 2) ? (_wVLabelVram + 8f * _k + _wTag + ClaudeBarWidth + 6f * _k + _wClaudeText) : (two ? (_wVLabelVram + 7f * _k + _wTag + _wPct) : (_wClaude + (_gap2 + _wTag + 3f * _k + _wPct) * 2f)),
			"chatgpt" => two ? (_wVLabelVram + 7f * _k + _wTag + _wPct) : (_wGpt + (_gap2 + _wTag + 3f * _k + _wPct) * 2f),
			"grok" => two ? (_wVLabelVram + 7f * _k + _wPct) : (_wGrok + _gap2 + _wPct),
			"deepseek" => two ? (_wVLabelVram + 7f * _k + _wBalance) : (_wDs + _gap2 + _wBalance),
			"battery" => two ? (_wIcon + _gap1 + _wBatSmall) : ((_wIcon + _gap1 + _wBatPct) * 2f + _gap2 * 2f),
			_ => 0f,
		};
	}

	// Ряд элементов подряд (для превью в окне настроек): ширина и отрисовка.
	public float RowWidth(IReadOnlyList<string> ids)
	{
		float num = _pad * 2f;
		for (int i = 0; i < ids.Count; i++)
		{
			num += ElementWidth(ids[i]) + ((i > 0) ? Gap(ids[i - 1], ids[i]) : 0f);
		}
		return num;
	}

	public void DrawRow(Graphics g, IReadOnlyList<string> ids, in Readings r, Settings s, Palette p)
	{
		float num = _pad;
		for (int i = 0; i < ids.Count; i++)
		{
			if (i > 0)
			{
				num += Gap(ids[i - 1], ids[i]);
			}
			DrawElement(g, ids[i], num, in r, s, p);
			num += ElementWidth(ids[i]);
		}
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
		int ai = HashCode.Combine(Round(r.GptFirst), Round(r.GptSecond), r.GptStale, Round(r.GrokUsed), r.GrokStale, Round(r.DsBalance * 100f), r.DsCurrency, r.DsStale);
		int value = HashCode.Combine(Round(r.KbBattery), Round(r.MouseBattery), r.KbCharging, r.MouseCharging, Round(r.ClaudeFiveHour), Round(r.ClaudeSevenDay), r.ClaudeStale, HashCode.Combine(r.ClaudeShown, ShortLeft(r.ClaudeFiveHourLeft), ShortLeft(r.ClaudeSevenDayLeft), ai));
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

	// Элемент с левым краем в x (пикселях окна строки).
	public void DrawElement(Graphics g, string id, float x, in Readings r, Settings s, Palette p)
	{
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		_batKb = s.KbBattery;
		_batMouse = s.MouseBattery;
		if (IsSeparator(id))
		{
			float num = MathF.Round(_h * 0.42f);
			using SolidBrush brush = new SolidBrush(p.Sep);
			g.FillRectangle(brush, MathF.Round(x), MathF.Round((_h - num) / 2f), _sepW, num);
		}
		else
		{
			DrawBlock(g, id, x, _style != 0, in r, s, p);
		}
	}

	private float DrawBlock(Graphics g, string key, float x, bool two, in Readings r, Settings s, Palette p)
	{
		switch (key)
		{
		case "cpu":
		{
			string temp = Temp(r.CpuTemp);
			Color color = p.Level(r.CpuTemp, s.CpuTemp);
			if (!two)
			{
				return Block(g, x, "CPU", r.CpuLoad, s.CpuLoad, temp, color, p);
			}
			return Group(g, x, "CPU", _vlabel, _wVLabel, r.CpuLoad, s.CpuLoad, temp, color, _value, _colCg, p);
		}
		case "gpu":
		{
			string temp2 = (r.GpuAsleep ? L.T("sleep") : Temp(r.GpuTemp));
			Color color2 = (r.GpuAsleep ? p.NoData : p.Level(r.GpuTemp, s.GpuTemp));
			if (!two)
			{
				return Block(g, x, "GPU", r.GpuLoad, s.GpuLoad, temp2, color2, p);
			}
			return Group(g, x, "GPU", _vlabel, _wVLabel, r.GpuLoad, s.GpuLoad, temp2, color2, _value, _colCg, p);
		}
		case "vram":
			if (!two)
			{
				return MemBlock(g, x, "VRAM", _label, _wVram, r.VramLoad, s.Vram, r.VramUsedGb, s, p);
			}
			return Group(g, x, "VRAM", _vlabelVram, _wVLabelVram, r.VramLoad, s.Vram, s.ShowRamGb ? Gb(r.VramUsedGb) : null, p.Dim, _small, _colMem, p);
		case "ram":
			if (!two)
			{
				return MemBlock(g, x, "RAM", _label, _wRam, r.RamLoad, s.Ram, r.RamUsedGb, s, p);
			}
			return Group(g, x, "RAM", _vlabel, _wVLabel, r.RamLoad, s.Ram, s.ShowRamGb ? Gb(r.RamUsedGb) : null, p.Dim, _small, _colMem, p);
		case "claude":
			if (_claudeStyle == 2)
			{
				return ClaudeLarge(g, x, in r, s, p);
			}
			if (two)
			{
				VerticalText(g, "CLAUDE", _vlabelVram, p.Label, x, _wVLabelVram);
				x += _wVLabelVram + 4f * _k;
				float num = 8f * _k;
				ClaudeValue(g, x, "5h", r.ClaudeFiveHour, r.ClaudeStale, s, p, _h / 2f - num);
				return ClaudeValue(g, x, "7d", r.ClaudeSevenDay, r.ClaudeStale, s, p, _h / 2f + num);
			}
			Text(g, "CLAUDE", _label, p.Label, x, _wClaude, _near);
			x += _wClaude + _gap2;
			x = ClaudeValue(g, x, "5h", r.ClaudeFiveHour, r.ClaudeStale, s, p, _h / 2f) + _gap2;
			return ClaudeValue(g, x, "7d", r.ClaudeSevenDay, r.ClaudeStale, s, p, _h / 2f);
		case "chatgpt":
			if (two)
			{
				VerticalText(g, "GPT", _vlabelVram, p.Label, x, _wVLabelVram);
				x += _wVLabelVram + 4f * _k;
				float d = 8f * _k;
				AiValue(g, x, "5h", r.GptFirst, r.GptStale, s.ChatGpt, p, _h / 2f - d);
				return AiValue(g, x, "7d", r.GptSecond, r.GptStale, s.ChatGpt, p, _h / 2f + d);
			}
			Text(g, "GPT", _label, p.Label, x, _wGpt, _near);
			x += _wGpt + _gap2;
			x = AiValue(g, x, "5h", r.GptFirst, r.GptStale, s.ChatGpt, p, _h / 2f) + _gap2;
			return AiValue(g, x, "7d", r.GptSecond, r.GptStale, s.ChatGpt, p, _h / 2f);
		case "grok":
			if (two)
			{
				VerticalText(g, "GROK", _vlabelVram, p.Label, x, _wVLabelVram);
				x += _wVLabelVram + 4f * _k;
			}
			else
			{
				Text(g, "GROK", _label, p.Label, x, _wGrok, _near);
				x += _wGrok + _gap2;
			}
			Text(g, Pct(r.GrokUsed), _value, r.GrokStale ? p.NoData : p.Level(r.GrokUsed, s.Grok), x, _wPct, _far, _h / 2f);
			return x + _wPct;
		case "deepseek":
		{
			if (two)
			{
				VerticalText(g, "DS", _vlabelVram, p.Label, x, _wVLabelVram);
				x += _wVLabelVram + 4f * _k;
			}
			else
			{
				Text(g, "DS", _label, p.Label, x, _wDs, _near);
				x += _wDs + _gap2;
			}
			float b = r.DsBalance;
			Color c = (float.IsNaN(b) || r.DsStale) ? p.NoData : ((b <= (float)s.DeepSeek.Hot) ? p.Hot : ((b <= (float)s.DeepSeek.Warn) ? p.Warn : p.Ok));
			Text(g, Money(b, r.DsCurrency), _value, c, x, _wBalance, _far, _h / 2f);
			return x + _wBalance;
		}
		case "battery":
			if (two)
			{
				float num2 = 8f * _k;
				Battery(g, x, keyboard: true, r.KbBattery, r.KbCharging, _small, _wBatSmall, _h / 2f - num2, p);
				return Battery(g, x, keyboard: false, r.MouseBattery, r.MouseCharging, _small, _wBatSmall, _h / 2f + num2, p);
			}
			x = Battery(g, x, keyboard: true, r.KbBattery, r.KbCharging, _value, _wBatPct, _h / 2f, p) + _gap2 * 2f;
			return Battery(g, x, keyboard: false, r.MouseBattery, r.MouseCharging, _value, _wBatPct, _h / 2f, p);
		default:
			return x;
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

	private float ClaudeBarWidth => MathF.Round((float)_claudeBarLength * _k);

	// Большой стиль Claude: вертикальная подпись, две строки «5h ▮▮▮▮▯▯▯▯▯▯ 40% · 2h» (как у Claude Code Usage Monitor).
	private float ClaudeLarge(Graphics g, float x, in Readings r, Settings s, Palette p)
	{
		VerticalText(g, "CLAUDE", _vlabelVram, p.Label, x, _wVLabelVram);
		x += _wVLabelVram + 4f * _k;
		float num = 8f * _k;
		ClaudeLargeRow(g, x, "5h", r.ClaudeFiveHour, r.ClaudeFiveHourLeft, r.ClaudeStale, s, p, _h / 2f - num);
		return ClaudeLargeRow(g, x, "7d", r.ClaudeSevenDay, r.ClaudeSevenDayLeft, r.ClaudeStale, s, p, _h / 2f + num);
	}

	private float ClaudeLargeRow(Graphics g, float x, string tag, float v, float leftMin, bool stale, Settings s, Palette p, float cy)
	{
		Text(g, tag, _label, p.Label, x, _wTag, _near, cy);
		x += _wTag + 4f * _k;
		Color c = (stale ? p.NoData : p.Level(v, s.Claude));
		float len = ClaudeBarWidth;
		float h = Math.Max(2f, MathF.Round((float)_claudeBarHeight * _k));
		float y = MathF.Round(cy - h / 2f);
		float pct = (float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 100f));
		using (SolidBrush on = new SolidBrush(c))
		{
			using SolidBrush off = new SolidBrush(p.Track);
			int n = _claudeSegments;
			if (n <= 0)
			{
				g.FillRectangle(off, MathF.Round(x), y, len, h);
				if (pct > 0f)
				{
					g.FillRectangle(on, MathF.Round(x), y, Math.Max(1f, MathF.Round(len * pct / 100f)), h);
				}
			}
			else
			{
				float gap = Math.Max(1f, MathF.Round(2f * _k));
				float seg = Math.Max(1f, (len - gap * (float)(n - 1)) / (float)n);
				int filled = Math.Clamp((int)MathF.Ceiling(pct * (float)n / 100f - 0.001f), 0, n);
				for (int i = 0; i < n; i++)
				{
					float sx = MathF.Round(x + (float)i * (seg + gap));
					g.FillRectangle((i < filled) ? on : off, sx, y, Math.Max(1f, MathF.Round(x + (float)i * (seg + gap) + seg) - sx), h);
				}
			}
		}
		x += ClaudeBarWidth + 6f * _k;
		string text = Pct(v);
		string left = ShortLeft(leftMin);
		if (left.Length > 0)
		{
			text = text + " · " + left;
		}
		Text(g, text, _small, c, x, _wClaudeText, _near, cy);
		return x + _wClaudeText;
	}

	// Сколько осталось до сброса, коротко: 2d, 5h, 40m; пусто — неизвестно.
	private static string ShortLeft(float minutes)
	{
		if (float.IsNaN(minutes) || minutes <= 0f)
		{
			return "";
		}
		if (minutes >= 1440f)
		{
			return $"{(int)(minutes / 1440f)}d";
		}
		if (minutes >= 60f)
		{
			return $"{(int)(minutes / 60f)}h";
		}
		return $"{(int)MathF.Ceiling(minutes)}m";
	}

	// Значение лимита сервиса ИИ: «5h 23%», цвет по его порогам; устаревшие данные — серым.
	private float AiValue(Graphics g, float x, string tag, float v, bool stale, Limit limit, Palette p, float cy)
	{
		Text(g, tag, _label, p.Label, x, _wTag, _near, cy);
		x += _wTag + 3f * _k;
		Text(g, Pct(v), _value, stale ? p.NoData : p.Level(v, limit), x, _wPct, _far, cy);
		return x + _wPct;
	}

	// Баланс: $12.34, ¥30.1; неизвестно — «—».
	internal static string Money(float v, string? currency)
	{
		if (float.IsNaN(v))
		{
			return "—";
		}
		string sign = currency switch
		{
			"USD" => "$",
			"CNY" => "¥",
			_ => (currency ?? "") + " "
		};
		return sign + v.ToString((v >= 100f) ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);
	}

	// «5h 23%»: процент лимита Claude, цвет по порогам; устаревшие данные — серым.
	private float ClaudeValue(Graphics g, float x, string tag, float v, bool stale, Settings s, Palette p, float cy)
	{
		Text(g, tag, _label, p.Label, x, _wTag, _near, cy);
		x += _wTag + 3f * _k;
		Text(g, Pct(v), _value, stale ? p.NoData : p.Level(v, s.Claude), x, _wPct, _far, cy);
		return x + _wPct;
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
