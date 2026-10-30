namespace PulseBar;

// Канал между службой PulseBarSensor (права LocalSystem) и строкой (без прав).
// Запрос — один байт RequestTemp. Ответ — float температуры CPU в °C (NaN — нет данных),
// байт длины и состояние датчика в UTF-8 (английский ключ для L.T).
internal static class SensorPipe
{
	public const string Name = "PulseBar.Sensor";

	public const byte RequestTemp = (byte)'T';

	public const string ServiceName = "PulseBarSensor";
}
