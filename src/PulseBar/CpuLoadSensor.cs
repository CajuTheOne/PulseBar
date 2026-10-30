using System;

namespace PulseBar;

internal sealed class CpuLoadSensor : IDisposable
{
	private nint _query;

	private nint _counter;

	private long _idle;

	private long _kernel;

	private long _user;

	public CpuLoadSensor()
	{
		if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0 || Native.PdhAddEnglishCounterW(_query, "\\Processor Information(_Total)\\% Processor Utility", IntPtr.Zero, out _counter) != 0 || Native.PdhCollectQueryData(_query) != 0)
		{
			if (_query != IntPtr.Zero)
			{
				Native.PdhCloseQuery(_query);
			}
			_query = IntPtr.Zero;
			Native.GetSystemTimes(out _idle, out _kernel, out _user);
		}
	}

	public float Read()
	{
		if (_query != IntPtr.Zero)
		{
			if (Native.PdhCollectQueryData(_query) == 0 && Native.PdhGetFormattedCounterValue(_counter, 33280u, out var _, out var value) == 0 && value.CStatus <= 1)
			{
				return (float)Math.Clamp(value.DoubleValue, 0.0, 100.0);
			}
			return float.NaN;
		}
		Native.GetSystemTimes(out var idle, out var kernel, out var user);
		long num = idle - _idle;
		long num2 = kernel - _kernel + user - _user;
		long idle2 = idle;
		long kernel2 = kernel;
		long user2 = user;
		_idle = idle2;
		_kernel = kernel2;
		_user = user2;
		if (num2 <= 0)
		{
			return float.NaN;
		}
		return (float)Math.Clamp((double)(num2 - num) * 100.0 / (double)num2, 0.0, 100.0);
	}

	public void Dispose()
	{
		if (_query != IntPtr.Zero)
		{
			Native.PdhCloseQuery(_query);
		}
		_query = IntPtr.Zero;
	}
}
