using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PulseBar.Logi;

// Желаемое состояние клавиатуры в режиме PulseBar (2.3): подсветка клавиш и логотипа (эффект или «по клавишам»),
// яркость 0–100 и лампочки M (биты M1–M3). LightingKey — отпечаток подсветки: одинаковый отпечаток — нечего отправлять.
internal sealed record KeyFrame(Lighting Keys, Lighting Logo, bool PerKey, IReadOnlyDictionary<byte, Rgb> Colors, int Brightness, byte MLeds)
{
	public string LightingKey
	{
		get
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(PerKey ? "perkey " : "effect ").Append(Keys.Effect).Append(' ').Append(Keys.Color).Append(' ').Append(Keys.Period);
			sb.Append(" | ").Append(Logo.Effect).Append(' ').Append(Logo.Color).Append(' ').Append(Logo.Period);
			if (PerKey)
			{
				foreach (var (id, c) in Colors.OrderBy(x => x.Key))
				{
					sb.Append(' ').Append(id.ToString("X2")).Append(c);
				}
			}
			return sb.ToString();
		}
	}

	public static KeyFrame From(Profile p, int brightness, byte mleds)
	{
		Dictionary<byte, Rgb> colors = new Dictionary<byte, Rgb>();
		if (p.PerKey)
		{
			foreach (var (k, v) in p.KeyColors)
			{
				if (byte.TryParse(k, System.Globalization.NumberStyles.HexNumber, null, out byte id))
				{
					colors[id] = Rgb.Parse(v);
				}
			}
		}
		Lighting keys = p.PerKey ? new Lighting
		{
			Effect = "static",
			Color = p.Keyboard.Color
		} : p.Keyboard;
		return new KeyFrame(Copy(keys), Copy(p.Logo), p.PerKey, colors, Math.Clamp(brightness, 0, 100), mleds);
	}

	private static Lighting Copy(Lighting l)
	{
		return new Lighting
		{
			Effect = l.Effect,
			Color = l.Color,
			Period = l.Period
		};
	}
}
