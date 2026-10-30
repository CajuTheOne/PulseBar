using System;
using System.Collections.Generic;

namespace PulseBar.Logi;

internal sealed class Profile
{
	public string Name { get; set; } = "";

	public string Apps { get; set; } = "";

	public Dictionary<string, KeyAction> Keys { get; set; } = new Dictionary<string, KeyAction>();

	public Lighting Keyboard { get; set; } = new Lighting();

	public Lighting Logo { get; set; } = new Lighting();

	public int Brightness { get; set; } = 100;

	public Dictionary<string, string> KeyColors { get; set; } = new Dictionary<string, string>();

	public KeyAction Key(string name)
	{
		if (!Keys.TryGetValue(name, out KeyAction value))
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
		string[] array = Apps.Split(new char[2] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string text in array)
		{
			if (string.Equals(text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text : (text + ".exe"), exe, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}
}
