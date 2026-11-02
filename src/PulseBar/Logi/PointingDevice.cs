namespace PulseBar.Logi;

internal sealed class PointingDevice
{
	public string Name = "";

	public ushort Vid;

	public ushort Pid;

	// Клавиатура другой фирмы (2.4.0-beta.8: только для списка «Определить» — PulseBar показывает её настройки Windows).
	public bool Keyboard;

	public int BatteryPercent = -1;

	public bool Charging;

	public string? RazerPath;

	public byte RazerTid;

	// Последний итог чтения заряда (0 — прочитан, иначе причина; см. OtherMice.Reason) — в журнал идёт только смена.
	public int RazerStatus = int.MinValue;
}
