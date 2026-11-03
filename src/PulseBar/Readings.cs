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

	// Наушники (NaN — нет на связи). BatteryKinds — что в блоке заряда: 1 — клавиатура, 2 — мышь, 4 — наушники.
	public float HeadsetBattery;

	public bool HeadsetCharging;

	public int BatteryKinds;

	public bool ClaudeShown;

	public float ClaudeFiveHour;

	public float ClaudeSevenDay;

	public bool ClaudeStale;

	// Минут до сброса лимитов (NaN — неизвестно).
	public float ClaudeFiveHourLeft;

	public float ClaudeSevenDayLeft;

	// Строка ИИ: ChatGPT (5 ч и неделя, %), Grok (расход, %), DeepSeek (баланс и валюта); Stale — данные устарели.
	public float GptFirst;

	public float GptSecond;

	public bool GptStale;

	// Минут до сброса окон ChatGPT (NaN — неизвестно).
	public float GptFirstLeft;

	public float GptSecondLeft;

	public float GrokUsed;

	public bool GrokStale;

	public float DsBalance;

	public string? DsCurrency;

	public bool DsStale;
}
