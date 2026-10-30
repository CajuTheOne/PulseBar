using System.Drawing;

namespace PulseBar;

internal sealed class Palette
{
	public Color Label;

	public Color Dim;

	public Color Sep;

	public Color Track;

	public Color Ok;

	public Color Warn;

	public Color Hot;

	public Color NoData;

	public static Palette Create(bool lightTaskbar, bool blueNormal)
	{
		if (!lightTaskbar)
		{
			return new Palette
			{
				Label = C(10526880),
				Dim = C(13158600),
				Sep = C(4868682),
				Track = C(3947580),
				NoData = C(8026746),
				Ok = (blueNormal ? C(6333946) : C(4906624)),
				Warn = C(16498468),
				Hot = C(16735324)
			};
		}
		return new Palette
		{
			Label = C(6052956),
			Dim = C(3026478),
			Sep = C(12895428),
			Track = C(13816530),
			NoData = C(10132122),
			Ok = (blueNormal ? C(1920728) : C(1409085)),
			Warn = C(11817737),
			Hot = C(14427686)
		};
	}

	private static Color C(int rgb)
	{
		return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
	}

	public Color Level(float v, Limit l)
	{
		if (!float.IsNaN(v))
		{
			if (!(v >= (float)l.Hot))
			{
				if (!(v >= (float)l.Warn))
				{
					return Ok;
				}
				return Warn;
			}
			return Hot;
		}
		return NoData;
	}
}
