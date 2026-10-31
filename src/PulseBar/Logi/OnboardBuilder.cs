using System;
using System.Collections.Generic;
using System.Linq;

namespace PulseBar.Logi;

// Профиль PulseBar → профиль встроенной памяти (формат 4). Во встроенную память попадает то, что клавиатура умеет сама:
// цвет или эффект клавиш и логотипа, приглушение при простое, имя, G-клавиши с одним сочетанием клавиш или медиаклавишей,
// M1–M3 — выбор профиля. Остальное (цвета отдельных клавиш, набор текста, запуск программ, макросы, MR) выполняет
// PulseBar, пока запущен.
internal static class OnboardBuilder
{
	// Сколько секунд простоя до приглушения, если в настройках «0 — не приглушать»: значение 0 у прошивки не проверено.
	private const int NeverDimSeconds = 3600;

	public static bool NeedsHost(Profile p)
	{
		if (p.KeyColors.Count > 0)
		{
			return true;
		}
		for (int i = 1; i <= 5; i++)
		{
			if (HostOnly(p.Key("G" + i)))
			{
				return true;
			}
		}
		return p.Key("MR").Type != "none" && !string.IsNullOrWhiteSpace(p.Key("MR").Value);
	}

	public static bool HostOnly(KeyAction a)
	{
		if (a.Type != "none" && !string.IsNullOrWhiteSpace(a.Value))
		{
			return ButtonFor(a) == null;
		}
		return false;
	}

	// Запись кнопки во встроенной памяти или null, если клавиатура сама такое не выполнит.
	public static byte[]? ButtonFor(KeyAction a)
	{
		if (a.Type == "none" || string.IsNullOrWhiteSpace(a.Value))
		{
			return OnboardProfile.Disabled();
		}
		if (a.Type != "keys")
		{
			return null;
		}
		string[] chords = a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (chords.Length != 1 || !Macro.TryParseChord(chords[0], out List<ushort> vks))
		{
			return null;
		}
		byte mods = 0;
		List<ushort> rest = new List<ushort>();
		foreach (ushort vk in vks)
		{
			byte m = Modifier(vk);
			if (m != 0)
			{
				mods |= m;
			}
			else
			{
				rest.Add(vk);
			}
		}
		if (rest.Count != 1)
		{
			return null;
		}
		ushort consumer = Consumer(rest[0]);
		if (consumer != 0)
		{
			return (mods == 0) ? OnboardProfile.Consumer(consumer) : null;
		}
		byte usage = HidUsage(rest[0]);
		if (usage == 0)
		{
			return null;
		}
		return OnboardProfile.Key(mods, usage);
	}

	public static OnboardProfile Build(OnboardProfile basis, Profile p, LogiConfig c)
	{
		OnboardProfile o = OnboardProfile.Parse(basis.Raw)!;
		o.Name = p.Name;
		int bright = Math.Clamp(p.Brightness, 1, 100);
		int dim = Math.Clamp(c.IdleBrightness, 1, 100);
		o.SetLed(OnboardProfile.LedKeys, Led(p.Keyboard, bright));
		o.SetLed(OnboardProfile.LedLogo, Led(p.Logo, bright));
		o.SetLed(OnboardProfile.LedKeysDim, Led(p.Keyboard, Math.Min(bright, dim)));
		o.SetLed(OnboardProfile.LedLogoDim, Led(p.Logo, Math.Min(bright, dim)));
		int save = (c.IdleMinutes > 0) ? Math.Clamp(c.IdleMinutes * 60, 10, 0xFFFF) : NeverDimSeconds;
		o.PowerSaveSeconds = save;
		o.PowerOffSeconds = Math.Max(o.PowerOffSeconds, Math.Min(0xFFFF, save + 60));
		for (int i = 0; i < 5; i++)
		{
			o.SetButton(i, ButtonFor(p.Key("G" + (i + 1))) ?? OnboardProfile.Disabled());
		}
		for (int j = 0; j < 3; j++)
		{
			o.SetButton(5 + j, OnboardProfile.SelectProfile(j + 1));
		}
		return o;
	}

	// Эффект зоны с яркостью 1–100: у статичного цвета яркость — множитель цвета, у дыхания и перелива — параметр.
	private static byte[] Led(Lighting l, int brightness)
	{
		var (r, g, b) = LogiService.ParseColor(l.Color);
		switch (l.Effect)
		{
		case "off":
			return OnboardProfile.Off();
		case "breathing":
			return OnboardProfile.Breathe(r, g, b, 3000, brightness);
		case "cycle":
			return OnboardProfile.Cycle(5000, brightness);
		default:
			return OnboardProfile.Static((byte)(r * brightness / 100), (byte)(g * brightness / 100), (byte)(b * brightness / 100));
		}
	}

	private static byte Modifier(ushort vk)
	{
		return vk switch
		{
			0x11 or 0xA2 => 0x01,
			0xA3 => 0x10,
			0x10 or 0xA0 => 0x02,
			0xA1 => 0x20,
			0x12 or 0xA4 => 0x04,
			0xA5 => 0x40,
			0x5B => 0x08,
			0x5C => 0x80,
			_ => 0,
		};
	}

	private static ushort Consumer(ushort vk)
	{
		return vk switch
		{
			0xB3 => 0xCD,
			0xB0 => 0xB5,
			0xB1 => 0xB6,
			0xB2 => 0xB7,
			0xAD => 0xE2,
			0xAF => 0xE9,
			0xAE => 0xEA,
			_ => 0,
		};
	}

	// Виртуальная клавиша Windows → код HID (Keyboard/Keypad page).
	private static byte HidUsage(ushort vk)
	{
		if (vk >= 0x41 && vk <= 0x5A)
		{
			return (byte)(0x04 + vk - 0x41);
		}
		if (vk >= 0x31 && vk <= 0x39)
		{
			return (byte)(0x1E + vk - 0x31);
		}
		if (vk >= 0x70 && vk <= 0x7B)
		{
			return (byte)(0x3A + vk - 0x70);
		}
		if (vk >= 0x7C && vk <= 0x87)
		{
			return (byte)(0x68 + vk - 0x7C);
		}
		if (vk >= 0x61 && vk <= 0x69)
		{
			return (byte)(0x59 + vk - 0x61);
		}
		return vk switch
		{
			0x30 => 0x27,
			0x0D => 0x28,
			0x1B => 0x29,
			0x08 => 0x2A,
			0x09 => 0x2B,
			0x20 => 0x2C,
			0xBD => 0x2D,
			0xBB => 0x2E,
			0xDB => 0x2F,
			0xDD => 0x30,
			0xDC => 0x31,
			0xBA => 0x33,
			0xDE => 0x34,
			0xC0 => 0x35,
			0xBC => 0x36,
			0xBE => 0x37,
			0xBF => 0x38,
			0x14 => 0x39,
			0x2C => 0x46,
			0x91 => 0x47,
			0x13 => 0x48,
			0x2D => 0x49,
			0x24 => 0x4A,
			0x21 => 0x4B,
			0x2E => 0x4C,
			0x23 => 0x4D,
			0x22 => 0x4E,
			0x27 => 0x4F,
			0x25 => 0x50,
			0x28 => 0x51,
			0x26 => 0x52,
			0x60 => 0x62,
			0x5D => 0x65,
			_ => 0,
		};
	}
}
