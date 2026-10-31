using System;
using System.Collections.Generic;

namespace PulseBar.Logi;

// Профиль PulseBar → сектор встроенной памяти (формат 4), 2.3. В памяти — то, что клавиатура выполнит без PulseBar:
// эффект клавиш и логотипа (режим «по клавишам» — его базовый цвет), яркость профиля, приглушение при простое, имя,
// G-клавиши с одним сочетанием или медиаклавишей, M1–M3 — выбор профиля. Текст, программы, макросы без PulseBar молчат.
internal static class OnboardPlan
{
	// Перелив цветов из заводского профиля G HUB у G915: [04, …, яркость в байте 9, …].
	private static readonly byte[] WaveTemplate = new byte[11] { 0x04, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x34, 0x01, 0x64, 0x08 };

	public const int DefaultBreathePeriod = 3000;

	public const int DefaultCyclePeriod = 5000;

	// Основа для сборки: свой сектор профиля, если он цел, иначе заводской (как делает G HUB при первом сохранении).
	public static OnboardSector Basis(byte[]? user, byte[]? rom)
	{
		if (user != null && OnboardMemory.CrcOk(user))
		{
			return new OnboardSector(user);
		}
		if (rom != null && rom.Length == OnboardSector.Size)
		{
			return new OnboardSector(rom);
		}
		throw new InvalidOperationException("no basis sector");
	}

	public static OnboardSector Build(OnboardSector basis, Profile p, LogiConfig c)
	{
		OnboardSector o = new OnboardSector(basis.Serialize());
		o.Name = p.Name;
		int bright = Math.Clamp(p.Brightness, 1, 100);
		Lighting keys = p.PerKey ? new Lighting
		{
			Effect = "static",
			Color = p.Keyboard.Color
		} : p.Keyboard;
		byte[] keysLed = Entry(keys, bright, basis.Led(OnboardSector.LedKeys));
		byte[] logoLed = Entry(p.Logo, bright, basis.Led(OnboardSector.LedLogo));
		o.SetLed(OnboardSector.LedKeys, keysLed);
		o.SetLed(OnboardSector.LedLogo, logoLed);
		if (c.IdleMinutes > 0)
		{
			int dim = Math.Min(bright, c.IdleBrightness);
			o.SetLed(OnboardSector.LedKeysDim, (dim > 0) ? Entry(keys, dim, basis.Led(OnboardSector.LedKeys)) : OnboardSector.Off());
			o.SetLed(OnboardSector.LedLogoDim, (dim > 0) ? Entry(p.Logo, dim, basis.Led(OnboardSector.LedLogo)) : OnboardSector.Off());
			o.PowerSaveSeconds = Math.Clamp(c.IdleMinutes * 60, 10, 0xFFFF);
		}
		else
		{
			// «Не приглушать»: приглушённые копии равны обычным.
			o.SetLed(OnboardSector.LedKeysDim, keysLed);
			o.SetLed(OnboardSector.LedLogoDim, logoLed);
		}
		o.PowerOffSeconds = Math.Max(o.PowerOffSeconds, Math.Min(0xFFFF, o.PowerSaveSeconds + 60));
		for (int i = 0; i < 5; i++)
		{
			o.SetButton(i, ButtonFor(p.Key("G" + (i + 1))) ?? OnboardSector.Disabled());
		}
		for (int j = 0; j < 3; j++)
		{
			o.SetButton(5 + j, OnboardSector.SelectProfile(j + 1));
		}
		return o;
	}

	// Запись подсветки [режим, 10 параметров] с яркостью 1–100: у статичного цвета яркость — множитель цвета, у эффектов —
	// параметр. Тот же вид у эффектов 0x8071 в режиме PulseBar (режим = id эффекта, параметры те же).
	public static byte[] Entry(Lighting l, int brightness, byte[]? basis = null)
	{
		Rgb color = Rgb.Parse(l.Color);
		switch (l.Effect)
		{
		case "off":
			return OnboardSector.Off();
		case "breathing":
			return OnboardSector.Breathe(color, (l.Period > 0) ? l.Period : DefaultBreathePeriod, brightness);
		case "cycle":
			return OnboardSector.Cycle((l.Period > 0) ? l.Period : DefaultCyclePeriod, brightness);
		case "wave":
		{
			byte[] w = (byte[])((basis != null && basis[0] == 0x04) ? basis.Clone() : WaveTemplate.Clone());
			w[9] = (byte)Math.Clamp(brightness, 1, 100);
			return w;
		}
		default:
			return OnboardSector.Static(color.Scale(brightness));
		}
	}

	// Действие выполняет только PulseBar (в память не попадает).
	public static bool HostOnly(KeyAction a)
	{
		return a.Type != "none" && !string.IsNullOrWhiteSpace(a.Value) && ButtonFor(a) == null;
	}

	// Запись кнопки во встроенной памяти; null — клавиатура сама такое не выполнит.
	public static byte[]? ButtonFor(KeyAction a)
	{
		if (a.Type == "none" || string.IsNullOrWhiteSpace(a.Value))
		{
			return OnboardSector.Disabled();
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
			return (mods == 0) ? OnboardSector.Consumer(consumer) : null;
		}
		byte usage = HidUsage(rest[0]);
		return (usage == 0) ? null : OnboardSector.Key(mods, usage);
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
			_ => 0
		};
	}

	// Медиаклавиши Windows → Consumer page.
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
			_ => 0
		};
	}

	// Виртуальная клавиша Windows → код HID (Keyboard/Keypad page).
	public static byte HidUsage(ushort vk)
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
			_ => 0
		};
	}
}
