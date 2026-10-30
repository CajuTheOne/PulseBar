using System.Collections.Generic;

namespace PulseBar.Logi;

internal static class KeyLayout
{
	public static readonly KeyDef[] G915 = Build();

	private static KeyDef[] Build()
	{
		List<KeyDef> k = new List<KeyDef>();
		Add(210, "G", 0f, -1.25f);
		Add(153, "☼", 12.5f, -1.25f);
		string[] array = new string[4] { "⏮", "⏯", "⏭", "Mute" };
		for (int i = 0; i < 4; i++)
		{
			Add(155 + i, array[i], 20 + i, -1.25f);
		}
		Add(38, "Esc", 1.5f, 0f);
		for (int j = 0; j < 12; j++)
		{
			Add(55 + j, "F" + (j + 1), 3.5f + (float)j + (float)(j / 4) * 0.5f, 0f);
		}
		Add(67, "PrtSc", 16.75f, 0f);
		Add(68, "ScrLk", 17.75f, 0f);
		Add(69, "Pause", 18.75f, 0f);
		float y = 1.25f;
		Add(50, "`", 1.5f, y);
		string text = "1234567890";
		for (int l = 0; l < 10; l++)
		{
			Add(27 + l, text[l].ToString(), 2.5f + (float)l, y);
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
		string text2 = "QWERTYUIOP";
		for (int m = 0; m < text2.Length; m++)
		{
			Add(L(text2[m]), text2[m].ToString(), 3 + m, y);
		}
		Add(44, "[", 13f, y);
		Add(45, "]", 14f, y);
		Add(37, "Enter", 15.25f, y, 1.25f, 2f);
		Add(73, "Del", 16.75f, y);
		Add(74, "End", 17.75f, y);
		Add(75, "PgDn", 18.75f, y);
		Add(92, "7", 20f, y);
		Add(93, "8", 21f, y);
		Add(94, "9", 22f, y);
		Add(84, "+", 23f, y, 1f, 2f);
		y = 3.25f;
		Add(54, "Caps", 1.5f, y, 1.75f);
		string text3 = "ASDFGHJKL";
		for (int n = 0; n < text3.Length; n++)
		{
			Add(L(text3[n]), text3[n].ToString(), 3.25f + (float)n, y);
		}
		Add(48, ";", 12.25f, y);
		Add(49, "'", 13.25f, y);
		Add(47, "\\", 14.25f, y);
		Add(89, "4", 20f, y);
		Add(90, "5", 21f, y);
		Add(91, "6", 22f, y);
		y = 4.25f;
		Add(105, "Shift", 1.5f, y, 2.25f);
		string text4 = "ZXCVBNM";
		for (int num = 0; num < text4.Length; num++)
		{
			Add(L(text4[num]), text4[num].ToString(), 3.75f + (float)num, y);
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
		for (int num2 = 0; num2 < 5; num2++)
		{
			Add(180 + num2, "G" + (num2 + 1), 0f, 1.25f + (float)num2);
		}
		return k.ToArray();
		void Add(int id, string label, float x, float y2, float w = 1f, float h = 1f)
		{
			k.Add(new KeyDef((byte)id, label, x, y2, w, h));
		}
		static int L(char c)
		{
			return c - 65 + 1;
		}
	}
}
