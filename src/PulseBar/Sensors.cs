using System;

namespace PulseBar;

internal sealed class Sensors : IDisposable
{
	public readonly CpuLoadSensor CpuLoad = new CpuLoadSensor();

	public readonly CpuTempSensor CpuTemp = new CpuTempSensor();

	public readonly GpuSensor Gpu = new GpuSensor();

	public Sensors(string gpuKey)
	{
		Gpu.Open(gpuKey);
	}

	public Readings Read()
	{
		Readings r = new Readings
		{
			CpuLoad = CpuLoad.Read(),
			CpuTemp = CpuTemp.Read()
		};
		Gpu.Read(ref r);
		ReadRam(ref r);
		return r;
	}

	private unsafe static void ReadRam(ref Readings r)
	{
		Native.MEMORYSTATUSEX mEMORYSTATUSEX = new Native.MEMORYSTATUSEX
		{
			Length = (uint)sizeof(Native.MEMORYSTATUSEX)
		};
		if (Native.GlobalMemoryStatusEx(&mEMORYSTATUSEX) == 0 || mEMORYSTATUSEX.TotalPhys == 0L)
		{
			r.RamLoad = (r.RamUsedGb = float.NaN);
			return;
		}
		ulong num = mEMORYSTATUSEX.TotalPhys - mEMORYSTATUSEX.AvailPhys;
		r.RamLoad = (float)((double)num * 100.0 / (double)mEMORYSTATUSEX.TotalPhys);
		r.RamUsedGb = (float)((double)num / 1073741824.0);
	}

	public void Dispose()
	{
		CpuLoad.Dispose();
		CpuTemp.Dispose();
		Gpu.Dispose();
	}
}
