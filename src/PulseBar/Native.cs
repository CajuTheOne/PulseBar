using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseBar;

internal static class Native
{
	public struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;

		public readonly int Width => Right - Left;

		public readonly int Height => Bottom - Top;
	}

	public struct POINT
	{
		public int X;

		public int Y;
	}

	public struct SIZE
	{
		public int Cx;

		public int Cy;
	}

	public struct BLENDFUNCTION
	{
		public byte BlendOp;

		public byte BlendFlags;

		public byte SourceConstantAlpha;

		public byte AlphaFormat;
	}

	public struct MONITORINFO
	{
		public int CbSize;

		public RECT Monitor;

		public RECT Work;

		public uint Flags;
	}

	public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

	public struct BITMAPINFOHEADER
	{
		public int Size;

		public int Width;

		public int Height;

		public short Planes;

		public short BitCount;

		public int Compression;

		public int SizeImage;

		public int XPelsPerMeter;

		public int YPelsPerMeter;

		public int ClrUsed;

		public int ClrImportant;
	}

	public struct MEMORYSTATUSEX
	{
		public uint Length;

		public uint MemoryLoad;

		public ulong TotalPhys;

		public ulong AvailPhys;

		public ulong TotalPageFile;

		public ulong AvailPageFile;

		public ulong TotalVirtual;

		public ulong AvailVirtual;

		public ulong AvailExtendedVirtual;
	}

	[StructLayout(LayoutKind.Explicit)]
	public struct PDH_FMT_COUNTERVALUE
	{
		[FieldOffset(0)]
		public uint CStatus;

		[FieldOffset(8)]
		public double DoubleValue;
	}

	public struct DEVPROPKEY
	{
		public Guid FmtId;

		public uint Pid;
	}

	public const int WS_POPUP = int.MinValue;

	public const int WS_EX_TOPMOST = 8;

	public const int WS_EX_TOOLWINDOW = 128;

	public const int WS_EX_LAYERED = 524288;

	public const int WS_EX_NOACTIVATE = 134217728;

	public static readonly nint HWND_TOPMOST = new IntPtr(-1);

	public const uint SWP_NOSIZE = 1u;

	public const uint SWP_NOMOVE = 2u;

	public const uint SWP_NOACTIVATE = 16u;

	public const uint SWP_NOOWNERZORDER = 512u;

	public const int SW_HIDE = 0;

	public const int SW_SHOWNOACTIVATE = 4;

	public const uint GW_HWNDPREV = 3u;

	public const int WM_TIMER = 275;

	public const int WM_SETTINGCHANGE = 26;

	public const uint EVENT_SYSTEM_FOREGROUND = 3u;

	public const uint WINEVENT_OUTOFCONTEXT = 0u;

	public const uint WINEVENT_SKIPOWNPROCESS = 2u;

	public const uint MONITOR_DEFAULTTONEAREST = 2u;

	public const uint ULW_ALPHA = 2u;

	public const uint MSGFLT_ALLOW = 1u;

	public const int GWL_EXSTYLE = -20;

	public const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

	private static readonly Guid DevPropGuid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern nint FindWindowW(string? cls, string? name);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern nint FindWindowExW(nint parent, nint after, string? cls, string? name);

	[DllImport("user32.dll")]
	public static extern int GetWindowRect(nint h, out RECT r);

	[DllImport("user32.dll")]
	public static extern int IsWindow(nint h);

	[DllImport("user32.dll")]
	public static extern int IsWindowVisible(nint h);

	[DllImport("user32.dll")]
	public static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	public static extern int SetForegroundWindow(nint h);

	[DllImport("user32.dll")]
	public static extern nint GetWindow(nint h, uint cmd);

	[DllImport("user32.dll")]
	public static extern nint GetWindowLongPtrW(nint h, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public unsafe static extern int GetClassNameW(nint h, char* buf, int max);

	[DllImport("user32.dll")]
	public static extern uint GetDpiForWindow(nint h);

	[DllImport("user32.dll")]
	public static extern nint MonitorFromWindow(nint h, uint flags);

	[DllImport("user32.dll")]
	public static extern int GetMonitorInfoW(nint mon, ref MONITORINFO mi);

	[DllImport("user32.dll")]
	public static extern int ShowWindow(nint h, int cmd);

	[DllImport("user32.dll")]
	public static extern int SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	public unsafe static extern int UpdateLayeredWindow(nint h, nint hdcDst, POINT* pptDst, SIZE* psize, nint hdcSrc, POINT* pptSrc, uint crKey, BLENDFUNCTION* pblend, uint flags);

	[DllImport("user32.dll")]
	public static extern nuint SetCoalescableTimer(nint h, nint id, uint elapse, nint proc, uint tolerance);

	[DllImport("user32.dll")]
	public static extern int KillTimer(nint h, nint id);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern uint RegisterWindowMessageW(string s);

	[DllImport("user32.dll")]
	public static extern int ChangeWindowMessageFilterEx(nint h, uint msg, uint action, nint info);

	[DllImport("user32.dll")]
	public static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc, uint pid, uint tid, uint flags);

	[DllImport("user32.dll")]
	public static extern int UnhookWinEvent(nint hook);

	public unsafe static string ClassName(nint h)
	{
		char* ptr = stackalloc char[64];
		int classNameW = GetClassNameW(h, ptr, 64);
		if (classNameW <= 0)
		{
			return "";
		}
		return new string(ptr, 0, classNameW);
	}

	[DllImport("dwmapi.dll")]
	public unsafe static extern int DwmSetWindowAttribute(nint h, int attr, int* value, int size);

	public unsafe static void UseDarkTitleBar(nint h)
	{
		int num = 1;
		DwmSetWindowAttribute(h, 20, &num, 4);
	}

	[DllImport("gdi32.dll")]
	public unsafe static extern nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* bmi, uint usage, out nint bits, nint section, uint offset);

	[DllImport("gdi32.dll")]
	public static extern nint CreateCompatibleDC(nint hdc);

	[DllImport("gdi32.dll")]
	public static extern nint SelectObject(nint hdc, nint obj);

	[DllImport("gdi32.dll")]
	public static extern int DeleteObject(nint obj);

	[DllImport("gdi32.dll")]
	public static extern int DeleteDC(nint hdc);

	[DllImport("gdi32.dll")]
	public unsafe static extern int D3DKMTEnumAdapters2(void* p);

	[DllImport("gdi32.dll")]
	public unsafe static extern int D3DKMTQueryAdapterInfo(void* p);

	[DllImport("gdi32.dll")]
	public unsafe static extern int D3DKMTQueryStatistics(void* p);

	[DllImport("gdi32.dll")]
	public unsafe static extern int D3DKMTCloseAdapter(void* p);

	[DllImport("kernel32.dll")]
	public unsafe static extern int GlobalMemoryStatusEx(MEMORYSTATUSEX* m);

	[DllImport("kernel32.dll")]
	public static extern int GetSystemTimes(out long idle, out long kernel, out long user);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

	[DllImport("kernel32.dll", SetLastError = true)]
	public unsafe static extern int DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, nint overlapped);

	[DllImport("kernel32.dll")]
	public static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll")]
	public static extern int SetProcessWorkingSetSize(nint process, nint min, nint max);

	public static void TrimMemory()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
	}

	[DllImport("kernel32.dll")]
	public unsafe static extern int SetProcessInformation(nint process, int infoClass, void* info, int size);

	public unsafe static void EnableEcoQoS()
	{
		uint* ptr = stackalloc uint[3];
		*ptr = 1u;
		ptr[1] = 1u;
		ptr[2] = 1u;
		SetProcessInformation(GetCurrentProcess(), 4, ptr, 12);
	}

	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	public static extern int PdhOpenQueryW(string? source, nint user, out nint query);

	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	public static extern int PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);

	[DllImport("pdh.dll")]
	public static extern int PdhCollectQueryData(nint query);

	[DllImport("pdh.dll")]
	public static extern int PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

	[DllImport("pdh.dll")]
	public static extern int PdhCloseQuery(nint query);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	public static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	public unsafe static extern int CM_Get_Device_ID_ListW(string filter, char* buffer, uint len, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	public static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);

	[DllImport("cfgmgr32.dll")]
	public unsafe static extern int CM_Get_DevNode_PropertyW(uint devInst, DEVPROPKEY* key, out uint type, void* buffer, ref uint size, uint flags);

	public unsafe static List<string> DisplayDeviceIds()
	{
		List<string> list = new List<string>();
		if (CM_Get_Device_ID_List_SizeW(out var len, "{4d36e968-e325-11ce-bfc1-08002be10318}", 768u) != 0 || len == 0)
		{
			return list;
		}
		char[] array = new char[len];
		fixed (char* buffer = array)
		{
			if (CM_Get_Device_ID_ListW("{4d36e968-e325-11ce-bfc1-08002be10318}", buffer, len, 768u) != 0)
			{
				return list;
			}
		}
		string[] array2 = new string(array).Split('\0', StringSplitOptions.RemoveEmptyEntries);
		foreach (string item in array2)
		{
			list.Add(item);
		}
		return list;
	}

	public unsafe static string? DeviceDescription(uint devInst)
	{
		DEVPROPKEY dEVPROPKEY = new DEVPROPKEY
		{
			FmtId = DevPropGuid,
			Pid = 2u
		};
		char* ptr = stackalloc char[256];
		uint size = 512u;
		if (CM_Get_DevNode_PropertyW(devInst, &dEVPROPKEY, out var _, ptr, ref size, 0u) != 0)
		{
			return null;
		}
		return new string(ptr).TrimEnd('\0');
	}

	public unsafe static int DevicePowerState(uint devInst)
	{
		DEVPROPKEY dEVPROPKEY = new DEVPROPKEY
		{
			FmtId = DevPropGuid,
			Pid = 32u
		};
		byte* ptr = stackalloc byte[64];
		uint size = 64u;
		if (CM_Get_DevNode_PropertyW(devInst, &dEVPROPKEY, out var _, ptr, ref size, 0u) != 0 || size < 8)
		{
			return 0;
		}
		return ((int*)ptr)[1];
	}
}
