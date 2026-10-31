using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PulseBar.Logi;

// Диагностика: PulseBar.exe --logi-probe <файл.txt> — карта функций HID++ всех устройств Logitech.
// Только чтение: таблица функций, сведения о встроенных профилях (0x8100), подсветке и G/M-клавишах.
// Ничего не записывает в устройства (не включает уведомления приёмника, не меняет режим).
internal static class LogiProbe
{
	private static readonly (ushort Id, string Name)[] Known = new (ushort, string)[]
	{
		(0x0000, "Root"), (0x0001, "FeatureSet"), (0x0003, "FirmwareInfo"), (0x0005, "DeviceName"),
		(0x0007, "DeviceFriendlyName"), (0x0020, "ConfigChange"), (0x1000, "BatteryStatus"), (0x1001, "BatteryVoltage"),
		(0x1004, "UnifiedBattery"), (0x1814, "ChangeHost"), (0x1815, "HostsInfo"), (0x1b04, "ReprogControlsV4"),
		(0x1d4b, "WirelessDeviceStatus"), (0x2201, "AdjustableDpi"), (0x40a3, "FnInversion"), (0x4522, "DisableKeys"),
		(0x4540, "KeyboardLayout2"), (0x8010, "GKeys"), (0x8020, "MKeys"), (0x8030, "MacroRecord"),
		(0x8040, "BrightnessControl"), (0x8060, "ReportRate"), (0x8061, "ExtendedReportRate"), (0x8070, "ColorLedEffects"),
		(0x8071, "RgbEffects"), (0x8080, "PerKeyLighting"), (0x8081, "PerKeyLightingV2"), (0x8100, "OnboardProfiles"),
		(0x8110, "MouseButtonSpy")
	};

	public static int Run(string path)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"PulseBar {Updater.Current} logi-probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}, G HUB running: {LogiService.GHubRunning()}");
		foreach (HidppChannel ch in HidppChannel.OpenAll(Array.Empty<ushort>()))
		{
			using (ch)
			{
				sb.AppendLine($"== channel PID {ch.Pid:X4}, receiver: {ch.IsReceiver}");
				if (ch.IsReceiver)
				{
					for (byte slot = 1; slot <= 6; slot++)
					{
						string name = LogiService.PairedName(ch, slot);
						if (name.Length > 0)
						{
							Describe(sb, HidppDevice.Probe(ch, slot, name), slot);
						}
					}
				}
				else
				{
					Describe(sb, HidppDevice.Probe(ch, byte.MaxValue, ""), byte.MaxValue);
				}
			}
		}
		File.WriteAllText(path, sb.ToString());
		return 0;
	}

	private static void Describe(StringBuilder sb, HidppDevice? d, byte slot)
	{
		if (d == null)
		{
			sb.AppendLine($"  [{slot}] no answer");
			return;
		}
		sb.AppendLine($"  [{slot}] {d.Name}: HID++ {d.Protocol}, {(d.Online ? "online" : "asleep")}, features: {d.Features.Count}");
		foreach (var (id, index) in d.Features.OrderBy(f => f.Value))
		{
			string name = Known.FirstOrDefault(k => k.Id == id).Name ?? "?";
			sb.AppendLine($"    #{index,2} 0x{id:X4} {name}");
		}
		Dump(sb, d, 0x8100, 0, "OnboardProfiles getDescription");
		Dump(sb, d, 0x8100, 2, "OnboardProfiles getOnboardMode");
		Dump(sb, d, 0x8100, 4, "OnboardProfiles getCurrentProfile");
		Dump(sb, d, 0x8071, 0, "RgbEffects getInfo");
		Dump(sb, d, 0x8070, 0, "ColorLedEffects getCount");
		Dump(sb, d, 0x8081, 0, "PerKeyLightingV2 getInfo");
		Dump(sb, d, 0x8010, 0, "GKeys getCount");
		Dump(sb, d, 0x8020, 0, "MKeys getCount");
		Dump(sb, d, 0x8040, 0, "Brightness getInfo");
	}

	private static void Dump(StringBuilder sb, HidppDevice d, ushort feature, byte fn, string what)
	{
		if (!d.Has(feature))
		{
			return;
		}
		byte[]? r = d.Call(feature, fn);
		sb.AppendLine($"    {what}: {(r == null ? "no answer" : Convert.ToHexString(r))}");
	}
}
