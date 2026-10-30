using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32.SafeHandles;

namespace PulseBar;

internal sealed class CpuTempSensor : IDisposable
{
	private const uint IoctlLoadBinary = 2712805508u;

	private const uint IoctlExecute = 2712805636u;

	private const long ThmTconCurTmp = 366592L;

	private SafeFileHandle? _h;

	private string _status = "";

	private int _error;

	public string Status
	{
		get
		{
			if (_error == 0)
			{
				return L.T(_status);
			}
			return L.F(_status, _error);
		}
	}

	public bool Available => _h != null;

	public unsafe CpuTempSensor()
	{
		if (!IsAmdZen())
		{
			_status = "AMD Ryzen only";
			return;
		}
		SafeFileHandle safeFileHandle = Native.CreateFileW("\\\\?\\GLOBALROOT\\Device\\PawnIO", 3221225472u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (safeFileHandle.IsInvalid)
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			if (lastWin32Error != 5)
			{
				int error = lastWin32Error;
				_status = "PawnIO driver unavailable (error {0})";
				_error = error;
			}
			else
			{
				_status = "administrator rights required";
				_error = 0;
			}
			safeFileHandle.Dispose();
			return;
		}
		using (Stream stream = typeof(CpuTempSensor).Assembly.GetManifestResourceStream("AMDFamily17.bin"))
		{
			byte[] array = new byte[stream.Length];
			stream.ReadExactly(array);
			fixed (byte* inBuf = array)
			{
				if (Native.DeviceIoControl(safeFileHandle, 2712805508u, inBuf, array.Length, null, 0, out var returned, IntPtr.Zero) == 0)
				{
					returned = Marshal.GetLastWin32Error();
					_status = "PawnIO rejected the module (error {0})";
					_error = returned;
					safeFileHandle.Dispose();
					return;
				}
			}
		}
		_h = safeFileHandle;
		if (float.IsNaN(Read()))
		{
			_status = "sensor not responding";
			Dispose();
		}
		else
		{
			_status = "PawnIO, Tctl/Tdie";
		}
	}

	public unsafe float Read()
	{
		if (_h == null)
		{
			return float.NaN;
		}
		byte* ptr = stackalloc byte[40];
		new Span<byte>(ptr, 40).Clear();
		"ioctl_read_smn"u8.CopyTo(new Span<byte>(ptr, 32));
		((long*)ptr)[4] = 366592L;
		long num = 0L;
		if (Native.DeviceIoControl(_h, 2712805636u, ptr, 40, &num, 8, out var returned, IntPtr.Zero) == 0 || returned < 8)
		{
			return float.NaN;
		}
		uint num2 = (uint)num;
		float num3 = (float)(num2 >> 21) * 0.125f;
		if ((num2 & 0x80000) != 0 || (num2 & 0x30000) == 196608)
		{
			num3 -= 49f;
		}
		if (!(num3 > -20f) || !(num3 < 150f))
		{
			return float.NaN;
		}
		return num3;
	}

	private static bool IsAmdZen()
	{
		if (!X86Base.IsSupported)
		{
			return false;
		}
		(int Eax, int Ebx, int Ecx, int Edx) tuple = X86Base.CpuId(0, 0);
		int item = tuple.Ebx;
		int item2 = tuple.Ecx;
		int item3 = tuple.Edx;
		if (item != 1752462657 || item3 != 1769238117 || item2 != 1145913699)
		{
			return false;
		}
		int item4 = X86Base.CpuId(1, 0).Eax;
		int num = (item4 >> 8) & 0xF;
		if (num == 15)
		{
			num += (item4 >> 20) & 0xFF;
		}
		return num >= 23;
	}

	public void Dispose()
	{
		_h?.Dispose();
		_h = null;
	}
}
