using System;
using System.Text;

namespace PulseBar.Logi;

// Профиль встроенной памяти, формат 4 (G915/G913; libratbag hidpp20.h, union hidpp20_internal_profile). Меняются только
// поля, которыми управляет PulseBar; остальные байты остаются как прочитаны, поэтому разбор и сборка без изменений
// дают тот же сектор.
//   0x1C/0x1E — через сколько секунд приглушить подсветку / выключить (LE16)
//   0x20 — 16 кнопок по 4 байта (у G915: G1–G5, M1–M3)
//   0xA0 — имя, 48 байт UTF-16LE
//   0xD0 логотип, 0xDB клавиши, 0xE6 / 0xF1 — их приглушённые копии; по 11 байт: режим + 10 параметров
//   0xFD — CRC (BE) по байтам 0…0xFC
internal sealed class OnboardProfile
{
	public const int Size = 255;

	public const int LedLogo = 0xD0;

	public const int LedKeys = 0xDB;

	public const int LedLogoDim = 0xE6;

	public const int LedKeysDim = 0xF1;

	// Режимы подсветки (байт 0 записи LED).
	public const byte LedOff = 0x00;

	public const byte LedStatic = 0x01;

	public const byte LedCycle = 0x03;

	public const byte LedBreathe = 0x0A;

	public readonly byte[] Raw;

	private OnboardProfile(byte[] raw)
	{
		Raw = raw;
	}

	public static OnboardProfile? Parse(byte[] sector)
	{
		if (sector.Length != Size)
		{
			return null;
		}
		return new OnboardProfile((byte[])sector.Clone());
	}

	public bool CrcOk => Onboard.CrcOk(Raw);

	public int PowerSaveSeconds
	{
		get
		{
			return Raw[0x1C] | (Raw[0x1D] << 8);
		}
		set
		{
			Raw[0x1C] = (byte)value;
			Raw[0x1D] = (byte)(value >> 8);
		}
	}

	public int PowerOffSeconds
	{
		get
		{
			return Raw[0x1E] | (Raw[0x1F] << 8);
		}
		set
		{
			Raw[0x1E] = (byte)value;
			Raw[0x1F] = (byte)(value >> 8);
		}
	}

	public string Name
	{
		get
		{
			string s = Encoding.Unicode.GetString(Raw, 0xA0, 48);
			int end = s.IndexOfAny(new char[2] { '\0', '￿' });
			return (end < 0) ? s : s.Substring(0, end);
		}
		set
		{
			byte[] bytes = Encoding.Unicode.GetBytes(value.Length > 23 ? value.Substring(0, 23) : value);
			Array.Fill(Raw, (byte)0, 0xA0, 48);
			Array.Copy(bytes, 0, Raw, 0xA0, bytes.Length);
		}
	}

	public byte[] Button(int i)
	{
		return Raw.AsSpan(0x20 + i * 4, 4).ToArray();
	}

	public void SetButton(int i, ReadOnlySpan<byte> entry)
	{
		entry.Slice(0, 4).CopyTo(Raw.AsSpan(0x20 + i * 4, 4));
	}

	public byte[] Led(int offset)
	{
		return Raw.AsSpan(offset, 11).ToArray();
	}

	public void SetLed(int offset, ReadOnlySpan<byte> entry)
	{
		entry.Slice(0, 11).CopyTo(Raw.AsSpan(offset, 11));
	}

	// Записи LED: статичный цвет; дыхание (период мс, форма волны 1 — как в заводском профиле, яркость 1–100);
	// перелив цветов (период мс, яркость); выключено.
	public static byte[] Static(byte r, byte g, byte b)
	{
		return new byte[11] { LedStatic, r, g, b, 0, 0, 0, 0, 0, 0, 0 };
	}

	public static byte[] Breathe(byte r, byte g, byte b, int periodMs, int intensity)
	{
		return new byte[11] { LedBreathe, r, g, b, (byte)(periodMs >> 8), (byte)periodMs, 1, (byte)Math.Clamp(intensity, 1, 100), 0, 0, 0 };
	}

	public static byte[] Cycle(int periodMs, int intensity)
	{
		return new byte[11] { LedCycle, 0, 0, 0, 0, 0, (byte)(periodMs >> 8), (byte)periodMs, (byte)Math.Clamp(intensity, 1, 100), 0, 0 };
	}

	public static byte[] Off()
	{
		return new byte[11];
	}

	// Кнопки: клавиша клавиатуры [80 02 модификаторы код], мультимедиа [80 03 usage BE16], выбор профиля
	// [90 0D FF номер], отключена [FF FF FF FF].
	public static byte[] Key(byte modifiers, byte hidUsage)
	{
		return new byte[4] { 0x80, 0x02, modifiers, hidUsage };
	}

	public static byte[] Consumer(ushort usage)
	{
		return new byte[4] { 0x80, 0x03, (byte)(usage >> 8), (byte)usage };
	}

	public static byte[] SelectProfile(int n)
	{
		return new byte[4] { 0x90, 0x0D, 0xFF, (byte)n };
	}

	public static byte[] Disabled()
	{
		return new byte[4] { 0xFF, 0xFF, 0xFF, 0xFF };
	}

	public byte[] Serialize()
	{
		byte[] array = (byte[])Raw.Clone();
		Onboard.SetCrc(array);
		return array;
	}

	public static string DescribeLed(byte[] e)
	{
		return e[0] switch
		{
			LedOff => "off",
			LedStatic => $"static #{e[1]:X2}{e[2]:X2}{e[3]:X2}",
			LedCycle => $"cycle {(e[6] << 8) | e[7]} ms, {e[8]}%",
			LedBreathe => $"breathe #{e[1]:X2}{e[2]:X2}{e[3]:X2} {(e[4] << 8) | e[5]} ms, {e[7]}%",
			_ => $"mode 0x{e[0]:X2} {Convert.ToHexString(e, 1, 10)}",
		};
	}

	public static string DescribeButton(byte[] b)
	{
		if (b[0] == 0x80 && b[1] == 0x02)
		{
			return $"key mod {b[2]:X2} usage {b[3]:X2}";
		}
		if (b[0] == 0x80 && b[1] == 0x03)
		{
			return $"consumer {(b[2] << 8) | b[3]:X4}";
		}
		if (b[0] == 0x80 && b[1] == 0x01)
		{
			return $"mouse {(b[2] << 8) | b[3]:X4}";
		}
		if (b[0] == 0x90)
		{
			return $"special {b[1]:X2} {b[3]:X2}";
		}
		if (b[0] == 0xFF)
		{
			return "disabled";
		}
		if (b[0] >> 4 == 0)
		{
			return $"macro sector {((b[0] & 0x0F) << 8) | b[1]} at {(b[2] << 8) | b[3]}";
		}
		return Convert.ToHexString(b);
	}
}
