using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PulseBar;

// Температура CPU через драйвер PawnIO и его подписанные модули (namazso/PawnIO.Modules, LGPL-2.1):
// AMD Ryzen — регистр Tctl/Tdie через SMN (AMDFamily17), Intel — пакетная температура из MSR (IntelMSR).
// Драйвер PawnIO запускается по требованию (Start=3): при загрузке Windows его может ещё никто не запустить. Тогда
// служба запускает его сама, а если и так не вышло — пробует снова раз в 30 секунд (Read), а не остаётся без
// температуры до перезагрузки.
internal sealed class CpuTemp : IDisposable
{
	private const uint IoctlLoadBinary = 0xA1B2_2084;

	private const uint IoctlExecute = 0xA1B2_2104;

	private const long ThmTconCurTmp = 0x5_9800;

	private const long MsrTemperatureTarget = 0x1A2;

	private const long MsrPackageThermStatus = 0x1B1;

	private const int RetryMs = 30_000;

	private enum Kind { None, Amd, Intel }

	private readonly object _lock = new();

	private SafeFileHandle? _h;

	private Kind _kind;

	private readonly Kind _cpu;

	private long _nextTry;

	private float _tjMax = 100f;

	public string Status { get; private set; } = "";

	public CpuTemp()
	{
		_cpu = Detect();
		if (_cpu == Kind.None)
		{
			Status = "unsupported CPU";
			return;
		}
		Open();
	}

	private void Open()
	{
		Kind kind = _cpu;
		_nextTry = Environment.TickCount64 + RetryMs;
		SafeFileHandle h = OpenDevice(out int error);
		if (h.IsInvalid && (error is 2 or 3) && StartDriver())
		{
			h.Dispose();
			h = OpenDevice(out error);
		}
		if (h.IsInvalid)
		{
			h.Dispose();
			Status = error is 2 or 3 ? "PawnIO is not installed" : error == 5 ? "administrator rights required" : $"PawnIO driver unavailable (error {error})";
			return;
		}
		string module = kind == Kind.Amd ? "AMDFamily17.bin" : "IntelMSR.bin";
		if (!LoadModule(h, module, out int loadError))
		{
			h.Dispose();
			Status = $"PawnIO rejected the module (error {loadError})";
			return;
		}
		_h = h;
		_kind = kind;
		if (kind == Kind.Intel && TryExecute("ioctl_read_msr", MsrTemperatureTarget, out long target))
		{
			int tj = (int)((target >> 16) & 0xFF);
			if (tj > 0)
			{
				_tjMax = tj;
			}
		}
		if (float.IsNaN(Read()))
		{
			Status = "sensor not responding";
			Dispose();
		}
		else
		{
			Status = kind == Kind.Amd ? "PawnIO, Tctl/Tdie" : $"PawnIO, Intel package (TjMax {_tjMax:0})";
		}
	}

	public float Read()
	{
		lock (_lock)
		{
			if (_h == null)
			{
				if (_cpu == Kind.None || Environment.TickCount64 < _nextTry)
				{
					return float.NaN;
				}
				string was = Status;
				Open();
				if (_h == null)
				{
					return float.NaN;
				}
				ServiceLog.Write($"{Status} (before: {was})");
			}
			float t;
			if (_kind == Kind.Amd)
			{
				if (!TryExecute("ioctl_read_smn", ThmTconCurTmp, out long v))
				{
					return float.NaN;
				}
				uint raw = (uint)v;
				t = (raw >> 21) * 0.125f;
				if ((raw & 0x80000) != 0 || (raw & 0x30000) == 0x30000)
				{
					t -= 49f;
				}
			}
			else
			{
				if (!TryExecute("ioctl_read_msr", MsrPackageThermStatus, out long v))
				{
					return float.NaN;
				}
				t = _tjMax - ((v >> 16) & 0x7F);
			}
			return t > -20f && t < 150f ? t : float.NaN;
		}
	}

	private unsafe bool TryExecute(string function, long arg, out long result)
	{
		result = 0;
		byte* input = stackalloc byte[40];
		new Span<byte>(input, 40).Clear();
		Encoding.ASCII.GetBytes(function, new Span<byte>(input, 31));
		((long*)input)[4] = arg;
		long output = 0;
		if (DeviceIoControl(_h!, IoctlExecute, input, 40, &output, 8, out int returned, IntPtr.Zero) == 0 || returned < 8)
		{
			return false;
		}
		result = output;
		return true;
	}

	private static unsafe bool LoadModule(SafeFileHandle h, string name, out int error)
	{
		error = 0;
		using Stream stream = typeof(CpuTemp).Assembly.GetManifestResourceStream(name)!;
		byte[] blob = new byte[stream.Length];
		stream.ReadExactly(blob);
		fixed (byte* p = blob)
		{
			if (DeviceIoControl(h, IoctlLoadBinary, p, blob.Length, null, 0, out _, IntPtr.Zero) == 0)
			{
				error = Marshal.GetLastWin32Error();
				return false;
			}
		}
		return true;
	}

	private static SafeFileHandle OpenDevice(out int error)
	{
		SafeFileHandle h = CreateFileW("\\\\?\\GLOBALROOT\\Device\\PawnIO", 0xC000_0000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
		error = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
		return h;
	}

	// Служба работает от LocalSystem и может запустить драйвер; false — драйвер не установлен или не запустился.
	private static bool StartDriver()
	{
		try
		{
			using ServiceController sc = new("PawnIO");
			if (sc.Status == ServiceControllerStatus.Running)
			{
				return true;
			}
			// StartPending — драйвер в этот момент запускает другая программа: только ждём.
			bool starting = sc.Status == ServiceControllerStatus.StartPending;
			if (!starting)
			{
				sc.Start();
			}
			sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
			ServiceLog.Write(starting ? "the PawnIO driver started" : "started the PawnIO driver");
			return true;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
		{
			return false;
		}
	}

	private static Kind Detect()
	{
		if (!X86Base.IsSupported)
		{
			return Kind.None;
		}
		(int _, int ebx, int ecx, int edx) = X86Base.CpuId(0, 0);
		Span<byte> vendor = stackalloc byte[12];
		MemoryMarshal.Write(vendor, in ebx);
		MemoryMarshal.Write(vendor[4..], in edx);
		MemoryMarshal.Write(vendor[8..], in ecx);
		string v = Encoding.ASCII.GetString(vendor);
		if (v == "GenuineIntel")
		{
			return Kind.Intel;
		}
		if (v != "AuthenticAMD")
		{
			return Kind.None;
		}
		int eax = X86Base.CpuId(1, 0).Eax;
		int family = (eax >> 8) & 0xF;
		if (family == 0xF)
		{
			family += (eax >> 20) & 0xFF;
		}
		return family >= 0x17 ? Kind.Amd : Kind.None;
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_h?.Dispose();
			_h = null;
		}
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern unsafe int DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr overlapped);
}
