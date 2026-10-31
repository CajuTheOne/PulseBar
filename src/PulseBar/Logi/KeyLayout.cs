using System.Collections.Generic;
using System.Linq;

namespace PulseBar.Logi;

// Раскладка G915/G913 для редактора цветов клавиш (2.3): номер клавиши 0x8081 (= код HID − 3 у обычных клавиш),
// подпись, положение и размер в ширинах клавиши. ANSI или ISO выбирается по клавишам, которые сообщила клавиатура:
// ISO — есть клавиша 0x61 (лишняя слева от Z) и Enter в две строки; ANSI — длинный левый Shift и «\» над Enter.
// Сама клавиатура принимает все номера 0–111 (карта 0x8081), поэтому список её клавиш строится по раскладке из 0x4540.
internal static class KeyLayout
{
	public const byte Logo = 210;

	public const byte IsoExtra = 97;

	public const byte AnsiBackslash = 46;

	public const byte IsoHash = 47;

	public static KeyDef[] For(IReadOnlyCollection<byte> ids)
	{
		bool iso = ids.Contains(IsoExtra);
		KeyDef[] all = Build(iso, ids.Contains(AnsiBackslash) || !ids.Contains(IsoHash));
		// Логотип — отдельная зона (своя подсветка); остальные — только подсвечиваемые клавиши, если список известен.
		return all.Where(k => k.Id != Logo && (ids.Count == 0 || ids.Contains(k.Id))).ToArray();
	}

	// Раскладка из 0x4540 (1 — US, 2 — US International, 3 — UK, 4 — немецкая, 5 — французская, …, 7 — русская):
	// у US и русской клавиатур Logitech форма ANSI, у европейских — ISO. 0 — не сообщила: ANSI.
	public static bool IsIso(int layout)
	{
		return layout != 0 && layout != 1 && layout != 2 && layout != 7;
	}

	// Номера всех клавиш раскладки, включая логотип.
	public static HashSet<byte> Physical(bool iso)
	{
		return new HashSet<byte>(Build(iso, !iso).Select(k => k.Id));
	}

	// Быстрые группы редактора.
	public static readonly (string Name, byte[] Ids)[] Groups = new (string, byte[])[]
	{
		("WASD", new byte[4] { 23, 1, 19, 4 }),
		("Arrows", new byte[4] { 76, 77, 78, 79 }),
		("F1–F12", Enumerable.Range(55, 12).Select(i => (byte)i).ToArray()),
		("Digits", Enumerable.Range(27, 10).Select(i => (byte)i).ToArray()),
		("NumPad", Enumerable.Range(80, 17).Select(i => (byte)i).ToArray()),
		("Media", new byte[5] { 153, 155, 156, 157, 158 }),
		("G-keys", new byte[5] { 180, 181, 182, 183, 184 })
	};

	private static KeyDef[] Build(bool iso, bool ansiBackslash)
	{
		List<KeyDef> k = new List<KeyDef>();
		Add(Logo, "G", 0f, -1.25f);
		Add(153, "☼", 12.5f, -1.25f);
		string[] media = new string[4] { "⏮", "⏯", "⏭", "🔇" };
		for (int i = 0; i < 4; i++)
		{
			Add(155 + i, media[i], 20 + i, -1.25f);
		}
		Add(38, "Esc", 1.5f, 0f);
		for (int j = 0; j < 12; j++)
		{
			Add(55 + j, "F" + (j + 1), 3.5f + j + j / 4 * 0.5f, 0f);
		}
		Add(67, "PrtSc", 16.75f, 0f);
		Add(68, "ScrLk", 17.75f, 0f);
		Add(69, "Pause", 18.75f, 0f);
		float y = 1.25f;
		Add(50, "`", 1.5f, y);
		for (int l = 0; l < 10; l++)
		{
			Add(27 + l, "1234567890"[l].ToString(), 2.5f + l, y);
		}
		Add(42, "-", 12.5f, y);
		Add(43, "=", 13.5f, y);
		Add(39, "⌫", 14.5f, y, 2f);
		Add(70, "Ins", 16.75f, y);
		Add(71, "Home", 17.75f, y);
		Add(72, "PgUp", 18.75f, y);
		Add(80, "Num", 20f, y);
		Add(81, "/", 21f, y);
		Add(82, "*", 22f, y);
		Add(83, "-", 23f, y);
		y = 2.25f;
		Add(40, "Tab", 1.5f, y, 1.5f);
		string row1 = "QWERTYUIOP";
		for (int m = 0; m < row1.Length; m++)
		{
			Add(Letter(row1[m]), row1[m].ToString(), 3 + m, y);
		}
		Add(44, "[", 13f, y);
		Add(45, "]", 14f, y);
		if (iso)
		{
			Add(37, "Enter", 15.25f, y, 1.25f, 2f);
		}
		else
		{
			Add(ansiBackslash ? AnsiBackslash : IsoHash, "\\", 15f, y, 1.5f);
		}
		Add(73, "Del", 16.75f, y);
		Add(74, "End", 17.75f, y);
		Add(75, "PgDn", 18.75f, y);
		Add(92, "7", 20f, y);
		Add(93, "8", 21f, y);
		Add(94, "9", 22f, y);
		Add(84, "+", 23f, y, 1f, 2f);
		y = 3.25f;
		Add(54, "Caps", 1.5f, y, 1.75f);
		string row2 = "ASDFGHJKL";
		for (int n = 0; n < row2.Length; n++)
		{
			Add(Letter(row2[n]), row2[n].ToString(), 3.25f + n, y);
		}
		Add(48, ";", 12.25f, y);
		Add(49, "'", 13.25f, y);
		if (iso)
		{
			Add(IsoHash, "#", 14.25f, y);
		}
		else
		{
			Add(37, "Enter", 14.25f, y, 2.25f);
		}
		Add(89, "4", 20f, y);
		Add(90, "5", 21f, y);
		Add(91, "6", 22f, y);
		y = 4.25f;
		if (iso)
		{
			Add(105, "Shift", 1.5f, y, 1.25f);
			Add(IsoExtra, "\\", 2.75f, y);
		}
		else
		{
			Add(105, "Shift", 1.5f, y, 2.25f);
		}
		string row3 = "ZXCVBNM";
		for (int o = 0; o < row3.Length; o++)
		{
			Add(Letter(row3[o]), row3[o].ToString(), 3.75f + o, y);
		}
		Add(51, ",", 10.75f, y);
		Add(52, ".", 11.75f, y);
		Add(53, "/", 12.75f, y);
		Add(109, "Shift", 13.75f, y, 2.75f);
		Add(79, "↑", 17.75f, y);
		Add(86, "1", 20f, y);
		Add(87, "2", 21f, y);
		Add(88, "3", 22f, y);
		Add(85, "Ent", 23f, y, 1f, 2f);
		y = 5.25f;
		Add(104, "Ctrl", 1.5f, y, 1.25f);
		Add(107, "Win", 2.75f, y, 1.25f);
		Add(106, "Alt", 4f, y, 1.25f);
		Add(41, "", 5.25f, y, 6.25f);
		Add(110, "AltGr", 11.5f, y, 1.25f);
		Add(111, "Win", 12.75f, y, 1.25f);
		Add(98, "Menu", 14f, y, 1.25f);
		Add(108, "Ctrl", 15.25f, y, 1.25f);
		Add(77, "←", 16.75f, y);
		Add(78, "↓", 17.75f, y);
		Add(76, "→", 18.75f, y);
		Add(95, "0", 20f, y, 2f);
		Add(96, ".", 22f, y);
		for (int g = 0; g < 5; g++)
		{
			Add(180 + g, "G" + (g + 1), 0f, 1.25f + g);
		}
		return k.ToArray();
		void Add(int id, string label, float x, float y2, float w = 1f, float h = 1f)
		{
			k.Add(new KeyDef((byte)id, label, x, y2, w, h));
		}
		static int Letter(char c)
		{
			return c - 'A' + 1;
		}
	}
}
