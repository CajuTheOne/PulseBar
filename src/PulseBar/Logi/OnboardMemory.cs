using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PulseBar.Logi;

// Встроенная память клавиатуры (HID++ 0x8100 OnboardProfiles), 2.3. Формат — по libratbag (hidpp20.c) и Solaar,
// сверено с чтением G915:
//   fn0 описание; fn1/fn2 режим (1 — профили из памяти, 2 — управляет программа); fn3/fn4 текущий профиль (сектор BE16);
//   fn5 чтение 16 байт [сектор BE16, смещение BE16] (смещение ≤ размер−16); fn6 начало записи [сектор BE16, 0, 0,
//   размер BE16], fn7 по 16 байт, fn8 конец записи. Сектор 0 — каталог профилей, 1…N — профили пользователя,
//   0x0101… — заводские (только чтение, без CRC). CRC сектора — CRC-16/CCITT-FALSE в двух последних байтах (BE).
internal sealed class OnboardMemory
{
	public const byte ModeOnboard = 1;

	public const byte ModeHost = 2;

	public const int RomBase = 0x100;

	public LogiDevice Device { get; }

	public int ProfileFormat { get; private set; }

	public int ProfileCount { get; private set; }

	public int RomProfileCount { get; private set; }

	public int ButtonCount { get; private set; }

	public int SectorCount { get; private set; }

	public int SectorSize { get; private set; }

	public string Description { get; private set; } = "";

	private OnboardMemory(LogiDevice device)
	{
		Device = device;
	}

	// null — функции нет, устройство не ответило или формат не поддержан (нужен формат профиля 4, как у G915/G913).
	public static OnboardMemory? Open(LogiDevice device)
	{
		byte[]? d = device.Call(LogiDevice.OnboardProfiles, 0, ReadOnlySpan<byte>.Empty, out _, 3);
		if (d == null)
		{
			return null;
		}
		OnboardMemory m = new OnboardMemory(device)
		{
			ProfileFormat = d[1],
			ProfileCount = d[3],
			RomProfileCount = d[4],
			ButtonCount = d[5],
			SectorCount = d[6],
			SectorSize = (d[7] << 8) | d[8],
			Description = Convert.ToHexString(d, 0, 11)
		};
		return (m.SectorSize == OnboardSector.Size) ? m : null;
	}

	private byte[]? Call(byte fn, params byte[] args)
	{
		return Device.Call(LogiDevice.OnboardProfiles, fn, args, out _, 3);
	}

	public int GetMode()
	{
		return Call(2)?[0] ?? (-1);
	}

	public bool SetMode(byte mode)
	{
		return Call(1, mode) != null;
	}

	public int GetCurrentProfile()
	{
		byte[]? r = Call(4);
		return (r == null) ? (-1) : ((r[0] << 8) | r[1]);
	}

	public bool SetCurrentProfile(int sector)
	{
		return Call(3, (byte)(sector >> 8), (byte)sector) != null;
	}

	public byte[]? ReadSector(int sector)
	{
		byte[] data = new byte[SectorSize];
		for (int offset = 0; offset < SectorSize; offset += 16)
		{
			int at = Math.Min(offset, SectorSize - 16);
			byte[]? r = Call(5, (byte)(sector >> 8), (byte)sector, (byte)(at >> 8), (byte)at);
			if (r == null || r.Length < 16)
			{
				return null;
			}
			Array.Copy(r, 0, data, at, 16);
		}
		return data;
	}

	// Запись сектора пользователя целиком и проверка обратным чтением; заводские секторы не пишутся.
	public bool WriteSector(int sector, byte[] data, out string error)
	{
		error = "";
		if (sector < 0 || sector >= SectorCount || data.Length != SectorSize)
		{
			error = $"bad sector {sector} or size {data.Length}";
			return false;
		}
		if (Device.Call(LogiDevice.OnboardProfiles, 6, new byte[6] { (byte)(sector >> 8), (byte)sector, 0, 0, (byte)(SectorSize >> 8), (byte)SectorSize }, out int e, 3) == null)
		{
			error = $"write start: error {e}";
			return false;
		}
		// Куски данных не повторяются: повтор после потерянного ответа сдвинул бы запись. При ошибке — весь сектор заново.
		for (int offset = 0; offset < SectorSize; offset += 16)
		{
			byte[] chunk = new byte[16];
			Array.Copy(data, offset, chunk, 0, Math.Min(16, SectorSize - offset));
			if (Device.Call(LogiDevice.OnboardProfiles, 7, chunk, out e, 1, 1500) == null)
			{
				error = $"write at {offset}: error {e}";
				return false;
			}
		}
		if (Device.Call(LogiDevice.OnboardProfiles, 8, ReadOnlySpan<byte>.Empty, out e, 1, 3000) == null)
		{
			error = $"write end: error {e}";
			return false;
		}
		byte[]? back = ReadSector(sector);
		if (back == null || !back.AsSpan().SequenceEqual(data))
		{
			error = "read-back does not match";
			return false;
		}
		return true;
	}

	public static ushort Crc(ReadOnlySpan<byte> data)
	{
		ushort crc = 0xFFFF;
		foreach (byte b in data)
		{
			crc ^= (ushort)(b << 8);
			for (int i = 0; i < 8; i++)
			{
				crc = (ushort)(((crc & 0x8000) != 0) ? ((crc << 1) ^ 0x1021) : (crc << 1));
			}
		}
		return crc;
	}

	public static bool CrcOk(ReadOnlySpan<byte> sector)
	{
		int n = sector.Length;
		return n > 2 && Crc(sector.Slice(0, n - 2)) == ((sector[n - 2] << 8) | sector[n - 1]);
	}

	public static void SetCrc(Span<byte> sector)
	{
		int n = sector.Length;
		ushort crc = Crc(sector.Slice(0, n - 2));
		sector[n - 2] = (byte)(crc >> 8);
		sector[n - 1] = (byte)crc;
	}

	// Каталог (сектор 0): записи [сектор BE16, включён, 0], конец — FFFF; CRC в конце сектора.
	public static List<(int Sector, bool Enabled)> ParseDirectory(ReadOnlySpan<byte> dir)
	{
		List<(int, bool)> list = new List<(int, bool)>();
		for (int i = 0; i + 3 < dir.Length - 2; i += 4)
		{
			int sector = (dir[i] << 8) | dir[i + 1];
			if (sector == 0xFFFF)
			{
				break;
			}
			list.Add((sector, dir[i + 2] != 0));
		}
		return list;
	}

	// Каталог с профилями 1…count, все включены (как пишет G HUB).
	public byte[] BuildDirectory(int count)
	{
		byte[] dir = new byte[SectorSize];
		Array.Fill(dir, (byte)0xFF);
		for (int i = 0; i < count; i++)
		{
			dir[i * 4] = 0;
			dir[i * 4 + 1] = (byte)(i + 1);
			dir[i * 4 + 2] = 1;
			dir[i * 4 + 3] = 0;
		}
		SetCrc(dir);
		return dir;
	}

	// Полная копия: каталог, секторы пользователя и заводские — JSON, секторы в hex (тот же формат, что в 2.2).
	public string Backup(string path)
	{
		Dictionary<string, string> sectors = new Dictionary<string, string>();
		List<int> list = new List<int>();
		for (int i = 0; i < SectorCount; i++)
		{
			list.Add(i);
		}
		for (int j = 1; j <= RomProfileCount; j++)
		{
			list.Add(RomBase + j);
		}
		int failed = 0;
		foreach (int n in list)
		{
			byte[]? s = ReadSector(n);
			if (s == null)
			{
				failed++;
				continue;
			}
			sectors[n.ToString("X4")] = Convert.ToHexString(s);
		}
		if (failed > 0)
		{
			throw new IOException($"{failed} sectors unreadable");
		}
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, JsonSerializer.Serialize(new
		{
			device = Device.Name,
			date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
			description = Description,
			sectorSize = SectorSize,
			sectors
		}, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
		return $"{sectors.Count} sectors";
	}

	// Секторы из копии (для «Вернуть заводские» и тестов): номер → байты.
	public static Dictionary<int, byte[]> LoadBackup(string path)
	{
		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
		Dictionary<int, byte[]> map = new Dictionary<int, byte[]>();
		foreach (JsonProperty p in doc.RootElement.GetProperty("sectors").EnumerateObject())
		{
			map[Convert.ToInt32(p.Name, 16)] = Convert.FromHexString(p.Value.GetString()!);
		}
		return map;
	}
}
