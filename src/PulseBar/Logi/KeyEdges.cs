using System.Collections.Generic;

namespace PulseBar.Logi;

// Нажатия G1–G5, M1–M3 и MR из уведомлений клавиатуры (2.3): только фронт нажатия — отпускание, повтор того же
// состояния и биты, уже бывшие нажатыми, не дают события. Форматы проверены журналом G915 31.10.2026:
//   GKeys 0x8010 fn0: битовая маска нажатых G-клавиш, LE16 (G1 01, G2 02, G3 04, G4 08, G5 10), удержание — без повторов;
//   MKeys 0x8020 fn0: битовая маска M-клавиш (M1 01, M2 02, M3 04);
//   MacroRecord 0x8030 fn0: 01 — MR нажата, 00 — отпущена.
// M-клавиши маленькие и стоят вплотную: одно нажатие M1 пришло как 03 → 02 → 03 → 02 → 00 (задета и M2). Из клавиш M,
// нажатых одним сообщением, считается только левая — иначе одно нажатие давало M1 и сразу M2 (ошибка 2.2).
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
					break;
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
