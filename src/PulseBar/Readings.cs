namespace PulseBar;

internal struct Readings
{
	public float CpuLoad;

	public float CpuTemp;

	public float GpuLoad;

	public float GpuTemp;

	public float VramLoad;

	public float VramUsedGb;

	public float RamLoad;

	public float RamUsedGb;

	public bool GpuAsleep;

	public float KbBattery;

	public float MouseBattery;

	public bool KbCharging;

	public bool MouseCharging;

	public bool ClaudeShown;

	public float ClaudeFiveHour;

	public float ClaudeSevenDay;

	public bool ClaudeStale;
}
