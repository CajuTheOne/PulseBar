using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PulseBar.Logi;

// Профиль подсветки и клавиш (2.3; на M1–M3 — через слоты LogiConfig.Slots). Статичный режим клавиш (PerKey) — базовый
// цвет Keyboard.Color и свои цвета KeyColors (ключ — номер клавиши 0x8081 в hex, значение #RRGGBB); иначе на клавишах
// эффект Keyboard по всей клавиатуре.
// Keys — действия G1–G5 и MR; Apps — программы для автопереключения (через запятую, «.exe» можно не писать).
internal sealed class Profile
{
	public string Name { get; set; } = "";

	public string Apps { get; set; } = "";

	public Dictionary<string, KeyAction> Keys { get; set; } = new Dictionary<string, KeyAction>();

	public Lighting Keyboard { get; set; } = new Lighting();

	public Lighting Logo { get; set; } = new Lighting();

	// Статичный режим клавиш: базовый цвет и свои цвета клавиш (в анимациях свои цвета не действуют).
	[JsonIgnore]
	public bool PerKey => Keyboard.Effect == "static";

	// Флаг «по клавишам» из файлов версий 1–2 — только для переноса, в файл версии 3 не пишется.
	[JsonPropertyName("perKey")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool LegacyPerKey { get; set; }

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
