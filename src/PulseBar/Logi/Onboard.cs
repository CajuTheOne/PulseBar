using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PulseBar.Logi;

// Встроенная память клавиатуры (HID++ 0x8100 OnboardProfiles). Формат и порядок записи — по libratbag (hidpp20.c,
// hidpp-generic.c) и Solaar (hidpp20.py), сверено с чтением G915:
//   fn0 описание; fn1/fn2 режим (1 — встроенный, 2 — управляет программа); fn3/fn4 текущий профиль (сектор);
//   fn5 чтение 16 байт [сектор BE16, смещение BE16] (смещение ≤ размер−16); fn6 начало записи [сектор, 0, размер],
//   fn7 по 16 байт, fn8 конец записи. Сектор 0 — каталог профилей, 1…N — пользовательские профили и макросы,
//   0x0101… — заводские (только чтение). Контрольная сумма сектора — CRC-16/CCITT-FALSE в двух последних байтах (BE).
internal sealed class Onboard
{
	public const ushort Feature = 0x8100;

	public const int ModeOnboard = 1;

	public const int ModeHost = 2;

	public readonly HidppDevice Device;

	public int MemoryModel { get; private set; }

	public int ProfileFormat { get; private set; }

	public int MacroFormat { get; private set; }

	public int ProfileCount { get; private set; }

	public int RomProfileCount { get; private set; }

	public int ButtonCount { get; private set; }

	public int SectorCount { get; private set; }

	public int SectorSize { get; private set; }

	public string Description { get; private set; } = "";

	private Onboard(HidppDevice device)
	{
		Device = device;
	}

	// null — функции нет или устройство не ответило.
	public static Onboard? Open(HidppDevice device)
	{
		if (!device.Has(Feature))
		{
			return null;
		}
		Onboard onboard = new Onboard(device);
		byte[]? d = onboard.Call(0);
		if (d == null)
		{
			return null;
		}
		onboard.MemoryModel = d[0];
		onboard.ProfileFormat = d[1];
		onboard.MacroFormat = d[2];
		onboard.ProfileCount = d[3];
		onboard.RomProfileCount = d[4];
		onboard.ButtonCount = d[5];
		onboard.SectorCount = d[6];
		onboard.SectorSize = (d[7] << 8) | d[8];
		onboard.Description = Convert.ToHexString(d, 0, 11);
		return (onboard.SectorSize >= 16) ? onboard : null;
	}

	private byte[]? Call(byte fn, params byte[] args)
	{
		byte feat = Device.Feat(Feature);
		if (feat == 0)
		{
			return null;
		}
		return Device.Channel.CallRetry(Device.Index, feat, fn, args, out _);
	}

	public int GetMode()
	{
		return Call(2)?[0] ?? -1;
	}

	public bool SetMode(int mode)
	{
		return Call(1, (byte)mode) != null;
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

	// Запись сектора целиком и проверка обратным чтением. Заводские секторы (≥ 0x100) и каталог отдельно не пишутся.
	public bool WriteSector(int sector, byte[] data, out string error)
	{
		error = "";
		if (sector < 0 || sector >= SectorCount || data.Length != SectorSize)
		{
			error = $"bad sector {sector} or size {data.Length}";
			return false;
		}
		byte feat = Device.Feat(Feature);
		if (Device.Channel.CallRetry(Device.Index, feat, 6, new byte[6] { (byte)(sector >> 8), (byte)sector, 0, 0, (byte)(SectorSize >> 8), (byte)SectorSize }, out int e) == null)
		{
			error = $"write start: error {e}";
			return false;
		}
		for (int offset = 0; offset < SectorSize; offset += 16)
		{
			byte[] chunk = new byte[16];
			Array.Copy(data, offset, chunk, 0, Math.Min(16, SectorSize - offset));
			if (Device.Channel.Call(Device.Index, feat, 7, chunk, out e, 1500) == null)
			{
				error = $"write at {offset}: error {e}";
				return false;
			}
		}
		if (Device.Channel.Call(Device.Index, feat, 8, Array.Empty<byte>(), out e, 3000) == null)
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

	public static bool CrcOk(byte[] sector)
	{
		int n = sector.Length;
		return n > 2 && Crc(sector.AsSpan(0, n - 2)) == ((sector[n - 2] << 8) | sector[n - 1]);
	}

	public static void SetCrc(byte[] sector)
	{
		int n = sector.Length;
		ushort crc = Crc(sector.AsSpan(0, n - 2));
		sector[n - 2] = (byte)(crc >> 8);
		sector[n - 1] = (byte)crc;
	}

	// Каталог (сектор 0): записи [сектор BE16, включён, 0], конец — FFFF.
	public static List<(int Sector, bool Enabled)> ParseDirectory(byte[] dir)
	{
		List<(int, bool)> list = new List<(int, bool)>();
		for (int i = 0; i + 3 < dir.Length; i += 4)
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

	// Резервная копия: описание, каталог, все пользовательские и заводские секторы — в JSON (секторы в hex).
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
			list.Add(0x100 + j);
		}
		int failed = 0;
		foreach (int item in list)
		{
			byte[]? s = ReadSector(item);
			if (s == null)
			{
				failed++;
				continue;
			}
			sectors[item.ToString("X4")] = Convert.ToHexString(s);
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
		return $"{sectors.Count} sectors saved, {failed} unreadable";
	}
}
