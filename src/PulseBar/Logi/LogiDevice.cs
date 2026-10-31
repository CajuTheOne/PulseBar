using System;
using System.Collections.Generic;
using System.Text;

namespace PulseBar.Logi;

// Устройство HID++ на канале (2.3): номер на приёмнике, карта функций (id → индекс и версия), имя, тип, заряд.
// Только чтение сведений об устройстве; управление подсветкой, клавишами и памятью — в своих классах.
internal sealed class LogiDevice
{
	public const ushort Root = 0x0000;

	public const ushort FeatureSet = 0x0001;

	public const ushort DeviceName = 0x0005;

	public const ushort BatteryStatus = 0x1000;

	public const ushort BatteryVoltage = 0x1001;

	public const ushort UnifiedBattery = 0x1004;

	public const ushort WirelessStatus = 0x1D4B;

	public const ushort KeyboardLayout = 0x4540;

	public const ushort GKeys = 0x8010;

	public const ushort MKeys = 0x8020;

	public const ushort MacroRecord = 0x8030;

	public const ushort Brightness = 0x8040;

	public const ushort RgbEffects = 0x8071;

	public const ushort PerKeyLighting = 0x8081;

	public const ushort OnboardProfiles = 0x8100;

	// Тип из DeviceName (0x0005, функция 2).
	public enum DeviceKind
	{
		Unknown = -1,
		Keyboard = 0,
		Mouse = 3,
		Trackpad = 4,
		Trackball = 5,
		Headset = 8
	}

	private static readonly Dictionary<ushort, string> Names = new Dictionary<ushort, string>
	{
		[0x0000] = "Root", [0x0001] = "FeatureSet", [0x0003] = "FirmwareInfo", [0x0005] = "DeviceName",
		[0x0007] = "DeviceFriendlyName", [0x0020] = "ConfigChange", [0x1000] = "BatteryStatus", [0x1001] = "BatteryVoltage",
		[0x1004] = "UnifiedBattery", [0x1814] = "ChangeHost", [0x1815] = "HostsInfo", [0x1B04] = "ReprogControlsV4",
		[0x1D4B] = "WirelessDeviceStatus", [0x2201] = "AdjustableDpi", [0x40A3] = "FnInversion", [0x4522] = "DisableKeys",
		[0x4540] = "KeyboardLayout2", [0x8010] = "GKeys", [0x8020] = "MKeys", [0x8030] = "MacroRecord",
		[0x8040] = "BrightnessControl", [0x8060] = "ReportRate", [0x8061] = "ExtendedReportRate", [0x8070] = "ColorLedEffects",
		[0x8071] = "RgbEffects", [0x8080] = "PerKeyLighting", [0x8081] = "PerKeyLightingV2", [0x8100] = "OnboardProfiles",
		[0x8110] = "MouseButtonSpy"
	};

	public static string FeatureName(ushort id)
	{
		return Names.TryGetValue(id, out var n) ? n : "?";
	}

	private readonly Dictionary<ushort, (byte Index, byte Version)> _features = new Dictionary<ushort, (byte, byte)>();

	public HidppLink Link { get; }

	public byte Index { get; }

	public string Name { get; private set; }

	public int Protocol { get; private set; }

	public DeviceKind Kind { get; private set; } = DeviceKind.Unknown;

	public IReadOnlyDictionary<ushort, (byte Index, byte Version)> Features => _features;

	public bool IsKeyboard => Kind == DeviceKind.Keyboard || Has(GKeys) || Has(PerKeyLighting);

	private LogiDevice(HidppLink link, byte index, string name)
	{
		Link = link;
		Index = index;
		Name = name;
	}

	public bool Has(ushort feature)
	{
		return _features.ContainsKey(feature);
	}

	public byte Feat(ushort feature)
	{
		return _features.TryGetValue(feature, out var f) ? f.Index : (byte)0;
	}

	public byte Version(ushort feature)
	{
		return _features.TryGetValue(feature, out var f) ? f.Version : (byte)0;
	}

	// id функции по её индексу у устройства (для уведомлений); 0xFFFF — неизвестный индекс.
	public ushort FeatureAt(byte index)
	{
		foreach (var (id, f) in _features)
		{
			if (f.Index == index)
			{
				return id;
			}
		}
		return 0xFFFF;
	}

	public byte[]? Call(ushort feature, byte fn, ReadOnlySpan<byte> args, out int error, int attempts = 1, int timeoutMs = 1000)
	{
		if (!_features.TryGetValue(feature, out var f))
		{
			error = HidppLink.ErrUnsupported;
			return null;
		}
		return Link.Call(Index, f.Index, fn, args, out error, attempts, timeoutMs);
	}

	public byte[]? Call(ushort feature, byte fn, params byte[] args)
	{
		return Call(feature, fn, args, out _, 3);
	}

	// Имя устройства, сохранённое в приёмнике при сопряжении (регистр 0xB5, страница 0x40+n) — есть и у спящего.
	public static string PairedName(HidppLink link, byte slot)
	{
		byte[]? r = link.Register(0xFF, 0x83, 0xB5, new byte[1] { (byte)(0x40 + slot - 1) }, out _);
		if (r == null || r.Length < 3)
		{
			return "";
		}
		int count = Math.Min(r[1], r.Length - 2);
		return Encoding.ASCII.GetString(r, 2, count).Trim('\0', ' ');
	}

	// Включить у приёмника уведомления о подключении устройств (регистр 0x00, биты «беспроводные» и «программные»).
	public static void EnableReceiverNotifications(HidppLink link)
	{
		byte[]? r = link.Register(0xFF, 0x81, 0x00, ReadOnlySpan<byte>.Empty, out _);
		byte b0 = (byte)((r != null) ? r[0] : 0);
		byte b1 = (byte)((r != null) ? r[1] : 0);
		byte b2 = (byte)((r != null) ? r[2] : 0);
		link.Register(0xFF, 0x80, 0x00, new byte[3] { b0, (byte)(b1 | 0x09), b2 }, out _);
	}

	// Опросить устройство: протокол, все функции, имя, тип. null — устройство не ответило (спит или выключено);
	// тогда pairedName остаётся именем, а функции будут прочитаны при подключении (Refresh).
	public static LogiDevice? Open(HidppLink link, byte index, string pairedName)
	{
		LogiDevice d = new LogiDevice(link, index, pairedName);
		return d.Refresh() ? d : null;
	}

	// Спящее устройство: только номер и имя из приёмника; функции — после подключения (Refresh).
	public static LogiDevice Offline(HidppLink link, byte index, string pairedName)
	{
		return new LogiDevice(link, index, pairedName);
	}

	public bool Refresh()
	{
		// Root.ping (функция 1) с меткой 0x5A: HID++ 2.0 отвечает версией протокола, 1.0 — ошибкой 1.
		byte[]? ping = Link.Call(Index, 0, 1, new byte[3] { 0, 0, 0x5A }, out int error, 2, 1500);
		if (ping == null)
		{
			if (error == 1)
			{
				Protocol = 1;
				return true;
			}
			return false;
		}
		Protocol = ping[0];
		byte[]? fs = Link.Call(Index, 0, 0, new byte[2] { (byte)(FeatureSet >> 8), (byte)FeatureSet }, out _, 3);
		if (fs == null || fs[0] == 0)
		{
			return true;
		}
		byte fsIndex = fs[0];
		byte[]? count = Link.Call(Index, fsIndex, 0, ReadOnlySpan<byte>.Empty, out _, 3);
		if (count == null)
		{
			return true;
		}
		_features.Clear();
		_features[Root] = (0, 0);
		for (int i = 1; i <= count[0]; i++)
		{
			byte[]? f = Link.Call(Index, fsIndex, 1, new byte[1] { (byte)i }, out _, 3);
			if (f != null)
			{
				_features[(ushort)((f[0] << 8) | f[1])] = ((byte)i, f[3]);
			}
		}
		ReadName();
		byte[]? type = Call(DeviceName, 2, ReadOnlySpan<byte>.Empty, out _, 3);
		if (type != null)
		{
			Kind = Enum.IsDefined(typeof(DeviceKind), (int)type[0]) ? (DeviceKind)type[0] : DeviceKind.Unknown;
		}
		return true;
	}

	private void ReadName()
	{
		byte[]? len = Call(DeviceName, 0, ReadOnlySpan<byte>.Empty, out _, 3);
		if (len == null)
		{
			return;
		}
		StringBuilder sb = new StringBuilder();
		while (sb.Length < len[0])
		{
			byte[]? part = Call(DeviceName, 1, new byte[1] { (byte)sb.Length }, out _, 3);
			if (part == null)
			{
				break;
			}
			int before = sb.Length;
			foreach (byte b in part)
			{
				if (b == 0 || sb.Length >= len[0])
				{
					break;
				}
				sb.Append((char)b);
			}
			if (sb.Length == before)
			{
				break;
			}
		}
		if (sb.Length > 0)
		{
			Name = sb.ToString().Trim();
		}
	}

	// Заряд в процентах (-1 — неизвестно) и признак зарядки.
	public int ReadBattery(out bool charging)
	{
		charging = false;
		if (Protocol == 1)
		{
			byte[]? r = Link.Register(Index, 0x81, 0x0D, ReadOnlySpan<byte>.Empty, out _);
			if (r != null && r[0] > 0 && r[0] <= 100)
			{
				return r[0];
			}
			r = Link.Register(Index, 0x81, 0x07, ReadOnlySpan<byte>.Empty, out _);
			return (r != null && r[0] > 0) ? Math.Clamp(r[0] * 100 / 7, 5, 100) : (-1);
		}
		if (Has(UnifiedBattery))
		{
			byte[]? r = Call(UnifiedBattery, 1, ReadOnlySpan<byte>.Empty, out _, 3);
			if (r == null)
			{
				return -1;
			}
			charging = r[2] == 1 || r[2] == 2;
			if (r[0] > 0)
			{
				return r[0];
			}
			return r[1] switch
			{
				8 => 90,
				4 => 50,
				2 => 20,
				1 => 5,
				_ => -1
			};
		}
		if (Has(BatteryVoltage))
		{
			byte[]? r = Call(BatteryVoltage, 0, ReadOnlySpan<byte>.Empty, out _, 3);
			if (r == null)
			{
				return -1;
			}
			charging = (r[2] & 0x80) != 0;
			return VoltageToPercent((r[0] << 8) | r[1]);
		}
		if (Has(BatteryStatus))
		{
			byte[]? r = Call(BatteryStatus, 0, ReadOnlySpan<byte>.Empty, out _, 3);
			if (r == null)
			{
				return -1;
			}
			charging = r[2] >= 1 && r[2] <= 3;
			return r[0];
		}
		return -1;
	}

	// Кривая разряда Li-ion (как в G HUB для 0x1001): напряжение в мВ → проценты.
	public static int VoltageToPercent(int mv)
	{
		ReadOnlySpan<(int Mv, int Pct)> t = [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];
		if (mv >= t[0].Mv)
		{
			return 100;
		}
		for (int i = 1; i < t.Length; i++)
		{
			if (mv >= t[i].Mv)
			{
				var (mv1, p1) = t[i - 1];
				var (mv2, p2) = t[i];
				return p2 + (mv - mv2) * (p1 - p2) / (mv1 - mv2);
			}
		}
		return 0;
	}
}
