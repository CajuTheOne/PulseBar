using System;
using System.Collections.Generic;

namespace PulseBar.Logi;

// Профиль M1–M3 (2.3). PerKey — режим «по клавишам»: базовый цвет Keyboard.Color и свои цвета KeyColors
// (ключ — номер клавиши 0x8081 в hex, значение #RRGGBB), статично; иначе на клавишах эффект Keyboard.
// Keys — действия G1–G5 и MR; Apps — программы для автопереключения (через запятую, «.exe» можно не писать).
internal sealed class Profile
{
	public string Name { get; set; } = "";

	public string Apps { get; set; } = "";

	public Dictionary<string, KeyAction> Keys { get; set; } = new Dictionary<string, KeyAction>();

	public Lighting Keyboard { get; set; } = new Lighting();

	public Lighting Logo { get; set; } = new Lighting();

	public bool PerKey { get; set; }

	public int Brightness { get; set; } = 100;

	public Dictionary<string, string> KeyColors { get; set; } = new Dictionary<string, string>();

	public KeyAction Key(string name)
	{
		if (!Keys.TryGetValue(name, out KeyAction? value))
		{
			value = (Keys[name] = new KeyAction());
		}
		return value;
	}

	public bool MatchesApp(string exe)
	{
		if (exe.Length == 0 || Apps.Length == 0)
		{
			return false;
		}
		foreach (string a in Apps.Split(new char[2] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (string.Equals(a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a : (a + ".exe"), exe, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}
}
