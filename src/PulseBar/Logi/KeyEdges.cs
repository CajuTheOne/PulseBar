using System.Collections.Generic;

namespace PulseBar.Logi;

// Нажатия G1–G5, M1–M3 и MR из уведомлений клавиатуры (2.3): только фронт нажатия — отпускание, повтор того же
// состояния и биты, уже бывшие нажатыми, не дают события.
//   GKeys 0x8010 fn0: битовая маска нажатых G-клавиш, LE16 (бит 0 — G1);
//   MKeys 0x8020 fn0: битовая маска M-клавиш (бит 0 — M1);
//   MacroRecord 0x8030 fn0: бит 0 — MR.
internal sealed class KeyEdges
{
	private int _g;

	private int _m;

	private int _mr;

	public void Reset()
	{
		_g = 0;
		_m = 0;
		_mr = 0;
	}

	public List<string> Feed(ushort feature, byte fn, byte[] data)
	{
		List<string> pressed = new List<string>();
		if (fn != 0 || data.Length < 2)
		{
			return pressed;
		}
		switch (feature)
		{
		case LogiDevice.GKeys:
		{
			int bits = data[0] | (data[1] << 8);
			int down = bits & ~_g;
			_g = bits;
			for (int i = 0; i < 16; i++)
			{
				if ((down & (1 << i)) != 0)
				{
					pressed.Add("G" + (i + 1));
				}
			}
			break;
		}
		case LogiDevice.MKeys:
		{
			int bits = data[0] & 0x07;
			int down = bits & ~_m;
			_m = bits;
			for (int i = 0; i < 3; i++)
			{
				if ((down & (1 << i)) != 0)
				{
					pressed.Add("M" + (i + 1));
				}
			}
			break;
		}
		case LogiDevice.MacroRecord:
		{
			int bit = data[0] & 1;
			if (bit != 0 && _mr == 0)
			{
				pressed.Add("MR");
			}
			_mr = bit;
			break;
		}
		}
		return pressed;
	}
}
