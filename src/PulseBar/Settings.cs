using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PulseBar;

internal sealed class Settings
{
	public static class Defaults
	{
		public static readonly Limit CpuLoad = new Limit(60, 85);

		public static readonly Limit CpuTemp = new Limit(75, 90);

		public static readonly Limit GpuLoad = new Limit(60, 85);

		public static readonly Limit GpuTemp = new Limit(70, 83);

		public static readonly Limit Vram = new Limit(70, 85);

		public static readonly Limit Ram = new Limit(70, 85);

		public static readonly Limit KbBattery = new Limit(20, 10);

		public static readonly Limit MouseBattery = new Limit(20, 10);
	}

	public int Layout;

	public string Language = "en";

	public bool BlueNormal;

	public int TextSize = 1;

	public int IntervalMs = 1000;

	public bool HideFullscreen = true;

	public bool ShowRamGb = true;

	public bool ShowVram = true;

	public int TrayGap;

	public int GroupGap = 20;

	public string Gpu = "";

	public bool TrayIcon = true;

	public bool ShowBattery = true;

	public Limit CpuLoad = Defaults.CpuLoad;

	public Limit CpuTemp = Defaults.CpuTemp;

	public Limit GpuLoad = Defaults.GpuLoad;

	public Limit GpuTemp = Defaults.GpuTemp;

	public Limit Vram = Defaults.Vram;

	public Limit Ram = Defaults.Ram;

	public Limit KbBattery = Defaults.KbBattery;

	public Limit MouseBattery = Defaults.MouseBattery;

	public float TextScale => TextSize switch
	{
		0 => 0.88f, 
		2 => 1.15f, 
		_ => 1f, 
	};

	public int Version { get; private set; }

	private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar");

	private static string FilePath => Path.Combine(Dir, "settings.ini");

	public static Settings Load()
	{
		Settings settings = new Settings();
		try
		{
			if (!File.Exists(FilePath))
			{
				return settings;
			}
			string[] array = File.ReadAllLines(FilePath);
			foreach (string text in array)
			{
				int num = text.IndexOf('=');
				if (num > 0)
				{
					string text2 = text.Substring(0, num).Trim();
					string text3 = text;
					int num2 = num + 1;
					string text4 = text3.Substring(num2, text3.Length - num2).Trim();
					int.TryParse(text4, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result);
					switch (text2)
					{
					case "layout":
						settings.Layout = ((result != 0) ? 1 : 0);
						break;
					case "blueNormal":
						settings.BlueNormal = result != 0;
						break;
					case "textSize":
						settings.TextSize = Math.Clamp(result, 0, 2);
						break;
					case "intervalMs":
						settings.IntervalMs = Math.Clamp(result, 250, 10000);
						break;
					case "hideFullscreen":
						settings.HideFullscreen = result != 0;
						break;
					case "showRamGb":
						settings.ShowRamGb = result != 0;
						break;
					case "showVram":
						settings.ShowVram = result != 0;
						break;
					case "groupGap":
						settings.GroupGap = Math.Clamp(result, 0, 80);
						break;
					case "trayGap":
						settings.TrayGap = Math.Clamp((int)Math.Round((double)result / 10.0) * 10, 0, 100);
						break;
					case "gpu":
						settings.Gpu = text4;
						break;
					case "trayIcon":
						settings.TrayIcon = result != 0;
						break;
					case "showBattery":
						settings.ShowBattery = result != 0;
						break;
					case "language":
						settings.Language = text4;
						break;
					case "cpuLoad":
						settings.CpuLoad = ParseLimit(text4, settings.CpuLoad);
						break;
					case "cpuTemp":
						settings.CpuTemp = ParseLimit(text4, settings.CpuTemp);
						break;
					case "gpuLoad":
						settings.GpuLoad = ParseLimit(text4, settings.GpuLoad);
						break;
					case "gpuTemp":
						settings.GpuTemp = ParseLimit(text4, settings.GpuTemp);
						break;
					case "vram":
						settings.Vram = ParseLimit(text4, settings.Vram);
						break;
					case "ram":
						settings.Ram = ParseLimit(text4, settings.Ram);
						break;
					case "kbBattery":
						settings.KbBattery = ParseLow(text4, settings.KbBattery);
						break;
					case "mouseBattery":
						settings.MouseBattery = ParseLow(text4, settings.MouseBattery);
						break;
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log.Write("settings load: " + ex.Message);
		}
		return settings;
	}

	public void Save()
	{
		Version++;
		try
		{
			Directory.CreateDirectory(Dir);
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append("layout=").Append(Layout).AppendLine();
			stringBuilder.Append("blueNormal=").Append(BlueNormal ? 1 : 0).AppendLine();
			stringBuilder.Append("textSize=").Append(TextSize).AppendLine();
			stringBuilder.Append("intervalMs=").Append(IntervalMs).AppendLine();
			stringBuilder.Append("hideFullscreen=").Append(HideFullscreen ? 1 : 0).AppendLine();
			stringBuilder.Append("showRamGb=").Append(ShowRamGb ? 1 : 0).AppendLine();
			stringBuilder.Append("showVram=").Append(ShowVram ? 1 : 0).AppendLine();
			stringBuilder.Append("groupGap=").Append(GroupGap).AppendLine();
			stringBuilder.Append("trayGap=").Append(TrayGap).AppendLine();
			stringBuilder.Append("gpu=").Append(Gpu).AppendLine();
			stringBuilder.Append("trayIcon=").Append(TrayIcon ? 1 : 0).AppendLine();
			stringBuilder.Append("showBattery=").Append(ShowBattery ? 1 : 0).AppendLine();
			stringBuilder.Append("language=").Append(Language).AppendLine();
			stringBuilder.Append("cpuLoad=").Append(Format(CpuLoad)).AppendLine();
			stringBuilder.Append("cpuTemp=").Append(Format(CpuTemp)).AppendLine();
			stringBuilder.Append("gpuLoad=").Append(Format(GpuLoad)).AppendLine();
			stringBuilder.Append("gpuTemp=").Append(Format(GpuTemp)).AppendLine();
			stringBuilder.Append("vram=").Append(Format(Vram)).AppendLine();
			stringBuilder.Append("ram=").Append(Format(Ram)).AppendLine();
			stringBuilder.Append("kbBattery=").Append(Format(KbBattery)).AppendLine();
			stringBuilder.Append("mouseBattery=").Append(Format(MouseBattery)).AppendLine();
			File.WriteAllText(FilePath, stringBuilder.ToString());
		}
		catch (Exception ex)
		{
			Log.Write("settings save: " + ex.Message);
		}
	}

	private static string Format(Limit l)
	{
		return $"{l.Warn},{l.Hot}";
	}

	private static Limit ParseLow(string v, Limit fallback)
	{
		string[] array = v.Split(',');
		if (array.Length == 2 && int.TryParse(array[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && int.TryParse(array[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2) && result2 < result && result2 >= 0 && result <= 100)
		{
			return new Limit(result, result2);
		}
		return fallback;
	}

	private static Limit ParseLimit(string v, Limit fallback)
	{
		string[] array = v.Split(',');
		if (array.Length == 2 && int.TryParse(array[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && int.TryParse(array[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2) && result < result2)
		{
			return new Limit(result, result2);
		}
		return fallback;
	}
}
