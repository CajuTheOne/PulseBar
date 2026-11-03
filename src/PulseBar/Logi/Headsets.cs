using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PulseBar.Logi;

// Наушники (2.4.0-beta.10): заряд Bluetooth-гарнитур — тот, что Windows показывает в «Bluetooth и устройства».
// Гарнитура сообщает его сама (профиль Hands-Free), Windows хранит его в свойстве узла устройства
// DEVPKEY_Bluetooth_Battery ({104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2, байт 0–100); связь — {83DA6326-…} 15.
// Читаются только свойства Windows, наушникам ничего не отправляется. Наушники — устройства Bluetooth со службой звука
// (A2DP 110B, Hands-Free 111E, Headset 1108); узлы одного устройства собираются по адресу Bluetooth.
internal static class Headsets
{
	internal sealed class Headset
	{
		// "bt:<адрес>"
		public string Id = "";

		public string Name = "";

		public string Brand = "";

		public int Battery = -1;

		public bool Online;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DEVPROPKEY
	{
		public Guid Fmtid;

		public uint Pid;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SP_DEVINFO_DATA
	{
		public int cbSize;

		public Guid ClassGuid;

		public uint DevInst;

		public nint Reserved;
	}

	private static readonly DEVPROPKEY Battery = new DEVPROPKEY { Fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Pid = 2 };

	private static readonly DEVPROPKEY Connected = new DEVPROPKEY { Fmtid = new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), Pid = 15 };

	private static readonly DEVPROPKEY FriendlyName = new DEVPROPKEY { Fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };

	private static readonly string[] AudioServices = new string[3] { "{0000110B-", "{0000111E-", "{00001108-" };

	// Производитель из VID в имени узла: _VID&0002xxxx — код USB-IF, _VID&0001xxxx — код Bluetooth SIG.
	private static readonly Dictionary<string, string> Vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["0002054C"] = "Sony", ["000205AC"] = "Apple", ["0002046D"] = "Logitech", ["00021532"] = "Razer", ["00020951"] = "HyperX",
		["000203F0"] = "HyperX", ["00021038"] = "SteelSeries", ["00021B1C"] = "Corsair", ["0001012D"] = "Sony", ["0001004C"] = "Apple",
		["0001009E"] = "Bose", ["00010057"] = "JBL", ["00010075"] = "Samsung", ["00010006"] = "Microsoft", ["000100E0"] = "Google",
		["0001038F"] = "Xiaomi", ["0001027D"] = "Huawei", ["00010103"] = "Bang & Olufsen", ["00010094"] = "Sennheiser",
		["00010494"] = "Sennheiser", ["00010087"] = "Garmin", ["000100D7"] = "Qualcomm", ["0001000A"] = "Qualcomm"
	};

	public static List<Headset> Find()
	{
		// Адрес → узлы: (идентификатор, имя, заряд, связь).
		Dictionary<string, List<(string Instance, string Name, int Battery, bool? Online)>> byAddress = new Dictionary<string, List<(string, string, int, bool?)>>();
		foreach (string enumerator in new string[2] { "BTHENUM", "BTHLE" })
		{
			nint set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
			if (set == -1)
			{
				continue;
			}
			try
			{
				SP_DEVINFO_DATA d = new SP_DEVINFO_DATA
				{
					cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>()
				};
				for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
				{
					string instance = InstanceId(set, ref d);
					string? address = Address(instance);
					if (address == null)
					{
						continue;
					}
					string name = (Get(set, ref d, FriendlyName) as string) ?? "";
					int battery = (Get(set, ref d, Battery) is byte b) ? b : -1;
					bool? online = (Get(set, ref d, Connected) is bool c) ? c : null;
					if (!byAddress.TryGetValue(address, out var nodes))
					{
						nodes = (byAddress[address] = new List<(string, string, int, bool?)>());
					}
					nodes.Add((instance, name, battery, online));
				}
			}
			finally
			{
				SetupDiDestroyDeviceInfoList(set);
			}
		}
		List<Headset> list = new List<Headset>();
		foreach (var (address, nodes) in byAddress)
		{
			if (!nodes.Any(n => AudioServices.Any(s => n.Instance.Contains(s, StringComparison.OrdinalIgnoreCase))))
			{
				continue;
			}
			// Имя — у узла самого устройства (BTHENUM\DEV_<адрес>), иначе у любого узла без «Hands-Free» и «AVRCP».
			string name = nodes.FirstOrDefault(n => n.Instance.Contains("\\DEV_", StringComparison.OrdinalIgnoreCase) && n.Name.Length > 0).Name ?? nodes.Select(n => n.Name).FirstOrDefault(n => n.Length > 0) ?? address;
			Match vid = Regex.Match(string.Join(" ", nodes.Select(n => n.Instance)), "_VID&([0-9A-F]{8})", RegexOptions.IgnoreCase);
			list.Add(new Headset
			{
				Id = "bt:" + address,
				Name = name,
				Brand = (vid.Success && Vendors.TryGetValue(vid.Groups[1].Value, out string? brand)) ? brand : "",
				Battery = nodes.Select(n => n.Battery).Where(b => b >= 0).DefaultIfEmpty(-1).Max(),
				Online = nodes.Any(n => n.Online == true)
			});
		}
		return list.OrderByDescending(h => h.Online).ThenBy(h => h.Name).ToList();
	}

	// Адрес Bluetooth (12 hex) из идентификатора узла: BTHENUM\DEV_8099E74265A2\…, …&0&8099E74265A2_C00000000,
	// BTHLE\DEV_…; null — узел без адреса (например, перечислитель).
	internal static string? Address(string instance)
	{
		Match m = Regex.Match(instance, "(?:DEV_|&)([0-9A-F]{12})(?:_|\\\\|$)", RegexOptions.IgnoreCase);
		return (m.Success && m.Groups[1].Value != "000000000000") ? m.Groups[1].Value.ToUpperInvariant() : null;
	}

	private static string InstanceId(nint set, ref SP_DEVINFO_DATA d)
	{
		char[] buf = new char[512];
		return SetupDiGetDeviceInstanceIdW(set, ref d, buf, buf.Length, out int len) ? new string(buf, 0, Math.Max(0, len - 1)) : "";
	}

	// Значение свойства: BYTE → byte, BOOLEAN → bool, STRING → string; иначе null.
	private static object? Get(nint set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
	{
		byte[] buf = new byte[1024];
		if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out uint type, buf, buf.Length, out int size, 0))
		{
			return null;
		}
		return type switch
		{
			0x03 => buf[0],
			0x11 => buf[0] != 0,
			0x12 => System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, size - 2)),
			_ => null
		};
	}

	private const int DIGCF_PRESENT = 0x2;

	private const int DIGCF_ALLCLASSES = 0x4;

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
	private static extern nint SetupDiGetClassDevsW(nint classGuid, string enumerator, nint hwnd, int flags);

	[DllImport("setupapi.dll", SetLastError = true)]
	private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA data);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref SP_DEVINFO_DATA data, char[] buffer, int size, out int required);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetupDiGetDevicePropertyW(nint set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, int size, out int required, int flags);

	[DllImport("setupapi.dll")]
	private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
