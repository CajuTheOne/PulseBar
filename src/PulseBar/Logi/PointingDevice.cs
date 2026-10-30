namespace PulseBar.Logi;

internal sealed class PointingDevice
{
	public string Name = "";

	public ushort Vid;

	public ushort Pid;

	public int BatteryPercent = -1;

	public bool Charging;

	public string? RazerPath;

	public byte RazerTid;
}
