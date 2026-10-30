using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseBar.Logi;

internal static class Hid
{
	private struct SP_DEVICE_INTERFACE_DATA
	{
		public int cbSize;

		public Guid InterfaceClassGuid;

		public int Flags;

		public nint Reserved;
	}

	private struct HIDD_ATTRIBUTES
	{
		public int Size;

		public ushort VendorID;

		public ushort ProductID;

		public ushort VersionNumber;
	}

	private struct HIDP_CAPS
	{
		public ushort Usage;

		public ushort UsagePage;

		public ushort InputReportByteLength;

		public ushort OutputReportByteLength;

		public ushort FeatureReportByteLength;

		public unsafe fixed ushort Reserved[17];

		public unsafe fixed ushort Counts[10];
	}

	public static List<HidInfo> Enumerate(ushort vid)
	{
		return Enumerate((string path) => path.Contains("vid_" + vid.ToString("x4"), StringComparison.OrdinalIgnoreCase));
	}

	public unsafe static List<HidInfo> Enumerate(Func<string, bool> pathFilter)
	{
		List<HidInfo> list = new List<HidInfo>();
		HidD_GetHidGuid(out var g);
		nint num = SetupDiGetClassDevsW(ref g, null, 0, 18);
		if (num == -1)
		{
			return list;
		}
		try
		{
			SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA
			{
				cbSize = sizeof(SP_DEVICE_INTERFACE_DATA)
			};
			for (uint num2 = 0u; SetupDiEnumDeviceInterfaces(num, 0, ref g, num2, ref d); num2++)
			{
				SetupDiGetDeviceInterfaceDetailW(num, ref d, 0, 0, out var need, 0);
				if (need <= 4)
				{
					continue;
				}
				nint num3 = Marshal.AllocHGlobal(need);
				try
				{
					*(int*)num3 = ((IntPtr.Size == 8) ? 8 : 6);
					if (!SetupDiGetDeviceInterfaceDetailW(num, ref d, num3, need, out var _, 0))
					{
						continue;
					}
					string text = new string((char*)(num3 + 4));
					if (pathFilter(text))
					{
						HidInfo hidInfo = Query(text);
						if (hidInfo != null)
						{
							list.Add(hidInfo);
						}
					}
				}
				finally
				{
					Marshal.FreeHGlobal(num3);
				}
			}
			return list;
		}
		finally
		{
			SetupDiDestroyDeviceInfoList(num);
		}
	}

	private unsafe static HidInfo? Query(string path)
	{
		using SafeFileHandle safeFileHandle = CreateFileW(path, 0u, 3u, 0, 3u, 0u, 0);
		if (safeFileHandle.IsInvalid)
		{
			return null;
		}
		HIDD_ATTRIBUTES a = new HIDD_ATTRIBUTES
		{
			Size = sizeof(HIDD_ATTRIBUTES)
		};
		if (!HidD_GetAttributes(safeFileHandle, ref a))
		{
			return null;
		}
		HIDP_CAPS caps = default(HIDP_CAPS);
		if (HidD_GetPreparsedData(safeFileHandle, out var pp))
		{
			HidP_GetCaps(pp, out caps);
			HidD_FreePreparsedData(pp);
		}
		string product = "";
		char* ptr = stackalloc char[128];
		if (HidD_GetProductString(safeFileHandle, ptr, 256))
		{
			product = new string(ptr).Trim();
		}
		return new HidInfo(path, a.VendorID, a.ProductID, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength, product);
	}

	public static SafeFileHandle OpenSync(string path)
	{
		return CreateFileW(path, 3221225472u, 3u, 0, 3u, 0u, 0);
	}

	public unsafe static bool SetFeature(SafeFileHandle h, byte[] report)
	{
		fixed (byte* buf = report)
		{
			return HidD_SetFeature(h, buf, report.Length);
		}
	}

	public unsafe static bool GetFeature(SafeFileHandle h, byte[] report)
	{
		fixed (byte* buf = report)
		{
			return HidD_GetFeature(h, buf, report.Length);
		}
	}

	[DllImport("hid.dll")]
	private unsafe static extern bool HidD_GetProductString(SafeFileHandle h, char* buf, int size);

	[DllImport("hid.dll")]
	private unsafe static extern bool HidD_SetFeature(SafeFileHandle h, byte* buf, int size);

	[DllImport("hid.dll")]
	private unsafe static extern bool HidD_GetFeature(SafeFileHandle h, byte* buf, int size);

	public static FileStream? Open(string path)
	{
		SafeFileHandle safeFileHandle = CreateFileW(path, 3221225472u, 3u, 0, 3u, 1073741824u, 0);
		if (safeFileHandle.IsInvalid)
		{
			safeFileHandle.Dispose();
			return null;
		}
		try
		{
			return new FileStream(safeFileHandle, FileAccess.ReadWrite, 0, isAsync: true);
		}
		catch
		{
			safeFileHandle.Dispose();
			return null;
		}
	}

	[DllImport("hid.dll")]
	private static extern void HidD_GetHidGuid(out Guid g);

	[DllImport("hid.dll")]
	private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);

	[DllImport("hid.dll")]
	private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out nint pp);

	[DllImport("hid.dll")]
	private static extern bool HidD_FreePreparsedData(nint pp);

	[DllImport("hid.dll")]
	private static extern int HidP_GetCaps(nint pp, out HIDP_CAPS caps);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
	private static extern nint SetupDiGetClassDevsW(ref Guid g, string? e, nint hwnd, int flags);

	[DllImport("setupapi.dll")]
	private static extern bool SetupDiEnumDeviceInterfaces(nint info, nint dev, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA d);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
	private static extern bool SetupDiGetDeviceInterfaceDetailW(nint info, ref SP_DEVICE_INTERFACE_DATA d, nint detail, int size, out int need, nint devInfo);

	[DllImport("setupapi.dll")]
	private static extern bool SetupDiDestroyDeviceInfoList(nint info);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disp, uint flags, nint tmpl);
}
