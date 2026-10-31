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

	// PulseBar.exe --logi-backup <файл.json> — копия всей встроенной памяти каждого устройства с 0x8100 (только чтение).
	public static int Backup(string path)
	{
		int n = 0;
		foreach (HidppChannel ch in HidppChannel.OpenAll(Array.Empty<ushort>()))
		{
			using (ch)
			{
				for (byte slot = 1; slot <= (ch.IsReceiver ? 6 : 1); slot++)
				{
					string name = ch.IsReceiver ? LogiService.PairedName(ch, slot) : "";
					if (ch.IsReceiver && name.Length == 0)
					{
						continue;
					}
					HidppDevice? d = HidppDevice.Probe(ch, ch.IsReceiver ? slot : byte.MaxValue, name);
					Onboard? o = (d != null && d.Online) ? Onboard.Open(d) : null;
					if (o == null)
					{
						continue;
					}
					string file = (n == 0) ? path : Path.ChangeExtension(path, $".{n}.json");
					Console.WriteLine($"{d!.Name}: {o.Backup(file)} -> {file}");
					Log.Write($"logi: backup {d.Name} -> {file}");
					n++;
				}
			}
		}
		return (n > 0) ? 0 : 1;
	}

	// PulseBar.exe --logi-parse <копия.json> <отчёт.txt> — разбор копии без устройства: каталог, профили, CRC и проверка,
	// что разбор и сборка профиля дают те же байты.
	public static int Parse(string backup, string report)
	{
		StringBuilder sb = new StringBuilder();
		using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(backup));
		var root = doc.RootElement;
		sb.AppendLine($"{root.GetProperty("device").GetString()} {root.GetProperty("date").GetString()}, description {root.GetProperty("description").GetString()}");
		var sectors = root.GetProperty("sectors");
		if (sectors.TryGetProperty("0000", out var dirHex))
		{
			byte[] dir = Convert.FromHexString(dirHex.GetString()!);
			sb.AppendLine("directory: " + string.Join(", ", Onboard.ParseDirectory(dir).Select(e => $"{e.Sector:X4}{(e.Enabled ? "" : " (off)")}")) + $", CRC {(Onboard.CrcOk(dir) ? "ok" : "bad")}");
		}
		int bad = 0;
		foreach (var s in sectors.EnumerateObject())
		{
			if (s.Name == "0000")
			{
				continue;
			}
			byte[] raw = Convert.FromHexString(s.Value.GetString()!);
			OnboardProfile? p = OnboardProfile.Parse(raw);
			if (p == null)
			{
				sb.AppendLine($"[{s.Name}] size {raw.Length}");
				continue;
			}
			bool same = p.Serialize().AsSpan().SequenceEqual(raw);
			if (p.CrcOk && !same)
			{
				bad++;
			}
			bool rom = s.Name.StartsWith("01", StringComparison.Ordinal);
			if (!p.CrcOk && !rom)
			{
				sb.AppendLine(raw.All(x => x == 0xFF) ? $"[{s.Name}] empty" : $"[{s.Name}] CRC bad (not a profile)");
				continue;
			}
			if (rom)
			{
				same = true; // заводские профили хранятся без CRC
			}
			sb.AppendLine($"[{s.Name}] {(rom ? "factory " : "")}\"{p.Name}\" {(rom ? "no CRC" : "CRC ok")}, round-trip {(same ? "identical" : "DIFFERENT")}, dim after {p.PowerSaveSeconds} s, off after {p.PowerOffSeconds} s");
			sb.AppendLine($"    logo {OnboardProfile.DescribeLed(p.Led(OnboardProfile.LedLogo))} | keys {OnboardProfile.DescribeLed(p.Led(OnboardProfile.LedKeys))}");
			sb.AppendLine($"    dim: logo {OnboardProfile.DescribeLed(p.Led(OnboardProfile.LedLogoDim))} | keys {OnboardProfile.DescribeLed(p.Led(OnboardProfile.LedKeysDim))}");
			for (int i = 0; i < 8; i++)
			{
				sb.AppendLine($"    button {i}: {OnboardProfile.DescribeButton(p.Button(i))}");
			}
		}
		sb.AppendLine(bad == 0 ? "round-trip: all valid profiles identical" : $"round-trip: {bad} DIFFERENT");
		File.WriteAllText(report, sb.ToString());
		return (bad == 0) ? 0 : 2;
	}

	// PulseBar.exe --logi-build <копия.json> <отчёт.txt> — что было бы записано в клавиатуру из текущих профилей
	// (logitech.json) поверх секторов копии; без устройства, ничего не записывает.
	public static int BuildPreview(string backup, string report)
	{
		LogiConfig c = LogiConfig.Load();
		StringBuilder sb = new StringBuilder();
		using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(backup));
		var sectors = doc.RootElement.GetProperty("sectors");
		for (int i = 0; i < Math.Min(3, c.Profiles.Count); i++)
		{
			Profile p = c.Profiles[i];
			string key = (i + 1).ToString("X4");
			byte[] raw = Convert.FromHexString(sectors.GetProperty(key).GetString()!);
			OnboardProfile basis = OnboardProfile.Parse(raw)!;
			if (!basis.CrcOk)
			{
				raw = Convert.FromHexString(sectors.GetProperty((0x101 + i).ToString("X4")).GetString()!);
				basis = OnboardProfile.Parse(raw)!;
			}
			byte[] built = OnboardBuilder.Build(basis, p, c).Serialize();
			OnboardProfile o = OnboardProfile.Parse(built)!;
			int changed = 0;
			for (int k = 0; k < built.Length; k++)
			{
				if (built[k] != raw[k])
				{
					changed++;
				}
			}
			sb.AppendLine($"[{key}] \"{o.Name}\" CRC {(o.CrcOk ? "ok" : "BAD")}, {changed} bytes differ from the keyboard, host overlay needed: {OnboardBuilder.NeedsHost(p)} ({p.KeyColors.Count} key colours)");
			sb.AppendLine($"    keys {OnboardProfile.DescribeLed(o.Led(OnboardProfile.LedKeys))} | logo {OnboardProfile.DescribeLed(o.Led(OnboardProfile.LedLogo))}");
			sb.AppendLine($"    dim after {o.PowerSaveSeconds} s: keys {OnboardProfile.DescribeLed(o.Led(OnboardProfile.LedKeysDim))} | logo {OnboardProfile.DescribeLed(o.Led(OnboardProfile.LedLogoDim))}");
			for (int b = 0; b < 8; b++)
			{
				string src = (b < 5) ? $" <- {p.Key("G" + (b + 1)).Type} \"{p.Key("G" + (b + 1)).Value}\"" : "";
				sb.AppendLine($"    button {b}: {OnboardProfile.DescribeButton(o.Button(b))}{src}");
			}
		}
		File.WriteAllText(report, sb.ToString());
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
