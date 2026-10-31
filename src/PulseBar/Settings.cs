using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

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

		public static readonly Limit Claude = new Limit(70, 90);
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

	public bool Embed = true;

	public bool Beta;

	// Блок Claude: 0 — выключен, 1 — компактный (между блоками), 2 — большой (полосы 5h и 7d).
	public int ClaudeStyle = 1;

	// Чьи лимиты Claude: true — аккаунта, открытого в приложении Claude; false — аккаунта Claude Code.
	public bool ClaudeFollowApp = true;

	// Большой стиль Claude: длина и высота полосы, px при 100 %; сегментов: 0 — сплошная полоса.
	public int ClaudeBarLength = 78;

	public int ClaudeBarHeight = 8;

	public int ClaudeSegments = 10;

	public bool ShowClaude
	{
		get
		{
			return ClaudeStyle != 0;
		}
		set
		{
			if (!value)
			{
				ClaudeStyle = 0;
			}
			else if (ClaudeStyle == 0)
			{
				ClaudeStyle = 1;
			}
		}
	}

	// Расположение элементов на панели: группы прижатых друг к другу элементов; X — расстояние от трея до правого
	// края группы, px при 100 %. null — по умолчанию (всё в ряд у трея). Разделители — элементы sep1, sep2, …
	public sealed class Group
	{
		public int X;

		public List<string> Items = new List<string>();

		public Group Clone()
		{
			return new Group
			{
				X = X,
				Items = new List<string>(Items)
			};
		}
	}

	public List<Group>? Groups;

	// Привязка при перетаскивании и расстояния между прилипшими элементами (у разделителя — своё), px при 100 %.
	public bool Snap = true;

	public int SnapGap = 20;

	public int SepGap = 10;

	public bool ShowCpu = true;

	// Подсказка при наведении и график в ней (график копит замеры — по умолчанию выключен, чтобы строка была легче).
	public bool HoverTip = true;

	public bool HoverChart;

	public bool ShowGpu = true;

	public bool ShowRam = true;

	public static readonly string[] AllBlocks = new string[6] { "cpu", "gpu", "vram", "ram", "claude", "battery" };

	// Порядок блоков на строке (через запятую); неизвестные ключи пропускаются, недостающие добавляются в конец.
	public string BlockOrder = string.Join(",", AllBlocks);

	public int ClaudeIntervalMin = 5;

	public Limit CpuLoad = Defaults.CpuLoad;

	public Limit CpuTemp = Defaults.CpuTemp;

	public Limit GpuLoad = Defaults.GpuLoad;

	public Limit GpuTemp = Defaults.GpuTemp;

	public Limit Vram = Defaults.Vram;

	public Limit Ram = Defaults.Ram;

	public Limit KbBattery = Defaults.KbBattery;

	public Limit MouseBattery = Defaults.MouseBattery;

	public Limit Claude = Defaults.Claude;

	public float TextScale => TextSize switch
	{
		0 => 0.88f, 
		2 => 1.15f, 
		_ => 1f, 
	};

	public int Version { get; private set; }

	public string[] Order()
	{
		List<string> list = new List<string>();
		foreach (string item in BlockOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (AllBlocks.Contains(item) && !list.Contains(item))
			{
				list.Add(item);
			}
		}
		list.AddRange(AllBlocks.Where((string b) => !list.Contains(b)));
		return list.ToArray();
	}

	public bool IsShown(string block)
	{
		return block switch
		{
			"cpu" => ShowCpu,
			"gpu" => ShowGpu,
			"vram" => ShowVram,
			"ram" => ShowRam,
			"claude" => ShowClaude,
			"battery" => ShowBattery,
			_ => false,
		};
	}

	public void SetShown(string block, bool shown)
	{
		switch (block)
		{
		case "cpu":
			ShowCpu = shown;
			break;
		case "gpu":
			ShowGpu = shown;
			break;
		case "vram":
			ShowVram = shown;
			break;
		case "ram":
			ShowRam = shown;
			break;
		case "claude":
			ShowClaude = shown;
			break;
		case "battery":
			ShowBattery = shown;
			break;
		}
	}

	// Видимые блоки в порядке отрисовки.
	public string[] Blocks()
	{
		return Order().Where(IsShown).ToArray();
	}

	public List<Group> LayoutGroups => Groups ?? (Groups = DefaultGroups());

	// «Всё в ряд у трея»: порядок блоков из 1.x/2.0, в одну строку — с разделителями между блоками.
	public List<Group> DefaultGroups()
	{
		Group group = new Group
		{
			X = Math.Clamp(TrayGap, 0, 400)
		};
		int sep = 0;
		foreach (string item in Order())
		{
			if (group.Items.Count > 0 && Layout == 0)
			{
				group.Items.Add("sep" + ++sep);
			}
			group.Items.Add(item);
		}
		return new List<Group> { group };
	}

	public void ResetLayout()
	{
		Groups = DefaultGroups();
	}

	public string NewSeparatorId()
	{
		int max = 0;
		foreach (Group group in LayoutGroups)
		{
			foreach (string item in group.Items)
			{
				if (item.StartsWith("sep", StringComparison.Ordinal) && int.TryParse(item.AsSpan(3), out int n))
				{
					max = Math.Max(max, n);
				}
			}
		}
		return "sep" + (max + 1);
	}

	// Каждый блок — ровно в одной группе; потерянные (из старых настроек) — в начало ближайшей к трею группы.
	private void NormalizeGroups()
	{
		if (Groups == null)
		{
			return;
		}
		HashSet<string> seen = new HashSet<string>();
		foreach (Group group in Groups)
		{
			group.Items.RemoveAll((string id) => (!AllBlocks.Contains(id) && !id.StartsWith("sep", StringComparison.Ordinal)) || !seen.Add(id));
		}
		Groups.RemoveAll((Group g) => g.Items.Count == 0);
		if (Groups.Count == 0)
		{
			Groups = null;
			return;
		}
		Group nearest = Groups.OrderBy((Group g) => g.X).First();
		foreach (string block in AllBlocks)
		{
			if (!seen.Contains(block))
			{
				nearest.Items.Insert(0, block);
			}
		}
	}

	private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar");

	private static string JsonPath => Path.Combine(Dir, "settings.json");

	private static string IniPath => Path.Combine(Dir, "settings.ini");

	// С 2.1 настройки хранятся в settings.json. settings.ini из 1.x/2.0 переносится один раз и остаётся как settings.ini.bak.
	public static Settings Load()
	{
		Settings settings = new Settings();
		try
		{
			if (File.Exists(JsonPath))
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(JsonPath));
				bool snapGap = false;
				foreach (JsonProperty item in jsonDocument.RootElement.EnumerateObject())
				{
					if (item.Name == "groups")
					{
						settings.Groups = ReadGroups(item.Value);
						continue;
					}
					snapGap |= item.Name == "snapGap";
					settings.Apply(item.Name, JsonText(item.Value));
				}
				if (!snapGap)
				{
					settings.SnapGap = settings.GroupGap;
				}
				settings.NormalizeGroups();
			}
			else if (File.Exists(IniPath))
			{
				foreach (string text in File.ReadAllLines(IniPath))
				{
					int num = text.IndexOf('=');
					if (num > 0)
					{
						settings.Apply(text.Substring(0, num).Trim(), text.Substring(num + 1).Trim());
					}
				}
				if (settings.Save())
				{
					File.Move(IniPath, IniPath + ".bak", overwrite: true);
					Log.Write("settings: settings.ini moved to settings.json");
				}
			}
		}
		catch (Exception ex)
		{
			Log.Write("settings load: " + ex.Message);
		}
		return settings;
	}

	private static List<Group>? ReadGroups(JsonElement v)
	{
		if (v.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		List<Group> list = new List<Group>();
		foreach (JsonElement item in v.EnumerateArray())
		{
			Group group = new Group
			{
				X = (item.TryGetProperty("x", out JsonElement x) && x.TryGetInt32(out int n)) ? Math.Clamp(n, 0, 4000) : 0
			};
			if (item.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement id in items.EnumerateArray())
				{
					if (id.ValueKind == JsonValueKind.String)
					{
						group.Items.Add(id.GetString()!);
					}
				}
			}
			list.Add(group);
		}
		return list;
	}

	// Значение JSON в том же виде, что в settings.ini: true → 1, [60, 85] → 60,85.
	private static string JsonText(JsonElement v)
	{
		return v.ValueKind switch
		{
			JsonValueKind.True => "1",
			JsonValueKind.False => "0",
			JsonValueKind.String => v.GetString() ?? "",
			JsonValueKind.Array => string.Join(",", v.EnumerateArray().Select((JsonElement x) => x.GetRawText())),
			_ => v.GetRawText()
		};
	}

	private void Apply(string text2, string text4)
	{
		int.TryParse(text4, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result);
		switch (text2)
		{
		case "layout":
			this.Layout = ((result != 0) ? 1 : 0);
			break;
		case "blueNormal":
			this.BlueNormal = result != 0;
			break;
		case "textSize":
			this.TextSize = Math.Clamp(result, 0, 2);
			break;
		case "intervalMs":
			this.IntervalMs = Math.Clamp(result, 250, 10000);
			break;
		case "hideFullscreen":
			this.HideFullscreen = result != 0;
			break;
		case "showRamGb":
			this.ShowRamGb = result != 0;
			break;
		case "showVram":
			this.ShowVram = result != 0;
			break;
		case "groupGap":
			this.GroupGap = Math.Clamp(result, 0, 80);
			break;
		case "trayGap":
			this.TrayGap = Math.Clamp(result, 0, 4000);
			break;
		case "gpu":
			this.Gpu = text4;
			break;
		case "trayIcon":
			this.TrayIcon = result != 0;
			break;
		case "showBattery":
			this.ShowBattery = result != 0;
			break;
		case "embed":
			this.Embed = result != 0;
			break;
		case "beta":
			this.Beta = result != 0;
			break;
		case "showClaude":
			this.ShowClaude = result != 0;
			break;
		case "claudeFollowApp":
			this.ClaudeFollowApp = result != 0;
			break;
		case "claudeStyle":
			this.ClaudeStyle = Math.Clamp(result, 0, 2);
			break;
		case "claudeBarLength":
			this.ClaudeBarLength = Math.Clamp(result, 40, 240);
			break;
		case "claudeBarHeight":
			this.ClaudeBarHeight = Math.Clamp(result, 4, 16);
			break;
		case "claudeSegments":
			this.ClaudeSegments = (result <= 0) ? 0 : Math.Clamp(result, 5, 20);
			break;
		case "snap":
			this.Snap = result != 0;
			break;
		case "snapGap":
			this.SnapGap = Math.Clamp(result, 0, 80);
			break;
		case "sepGap":
			this.SepGap = Math.Clamp(result, 0, 80);
			break;
		case "hoverTip":
			this.HoverTip = result != 0;
			break;
		case "hoverChart":
			this.HoverChart = result != 0;
			break;
		case "showCpu":
			this.ShowCpu = result != 0;
			break;
		case "showGpu":
			this.ShowGpu = result != 0;
			break;
		case "showRam":
			this.ShowRam = result != 0;
			break;
		case "blocks":
			this.BlockOrder = text4;
			break;
		case "claudeInterval":
			this.ClaudeIntervalMin = Math.Clamp(result, 1, 15);
			break;
		case "claude":
			this.Claude = ParseLimit(text4, this.Claude);
			break;
		case "language":
			this.Language = text4;
			break;
		case "cpuLoad":
			this.CpuLoad = ParseLimit(text4, this.CpuLoad);
			break;
		case "cpuTemp":
			this.CpuTemp = ParseLimit(text4, this.CpuTemp);
			break;
		case "gpuLoad":
			this.GpuLoad = ParseLimit(text4, this.GpuLoad);
			break;
		case "gpuTemp":
			this.GpuTemp = ParseLimit(text4, this.GpuTemp);
			break;
		case "vram":
			this.Vram = ParseLimit(text4, this.Vram);
			break;
		case "ram":
			this.Ram = ParseLimit(text4, this.Ram);
			break;
		case "kbBattery":
			this.KbBattery = ParseLow(text4, this.KbBattery);
			break;
		case "mouseBattery":
			this.MouseBattery = ParseLow(text4, this.MouseBattery);
			break;
		}
	}

	public bool Save()
	{
		Version++;
		try
		{
			Directory.CreateDirectory(Dir);
			using MemoryStream memoryStream = new MemoryStream();
			using (Utf8JsonWriter w = new Utf8JsonWriter(memoryStream, new JsonWriterOptions
			{
				Indented = true,
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			}))
			{
				w.WriteStartObject();
				w.WriteNumber("layout", Layout);
				w.WriteBoolean("blueNormal", BlueNormal);
				w.WriteNumber("textSize", TextSize);
				w.WriteNumber("intervalMs", IntervalMs);
				w.WriteBoolean("hideFullscreen", HideFullscreen);
				w.WriteBoolean("showRamGb", ShowRamGb);
				w.WriteBoolean("showVram", ShowVram);
				w.WriteNumber("groupGap", GroupGap);
				w.WriteNumber("trayGap", TrayGap);
				w.WriteString("gpu", Gpu);
				w.WriteBoolean("trayIcon", TrayIcon);
				w.WriteBoolean("showBattery", ShowBattery);
				w.WriteBoolean("embed", Embed);
				w.WriteBoolean("beta", Beta);
				w.WriteNumber("claudeStyle", ClaudeStyle);
				w.WriteBoolean("claudeFollowApp", ClaudeFollowApp);
				w.WriteNumber("claudeBarLength", ClaudeBarLength);
				w.WriteNumber("claudeBarHeight", ClaudeBarHeight);
				w.WriteNumber("claudeSegments", ClaudeSegments);
				w.WriteBoolean("snap", Snap);
				w.WriteNumber("snapGap", SnapGap);
				w.WriteNumber("sepGap", SepGap);
				if (Groups != null)
				{
					w.WriteStartArray("groups");
					foreach (Group group in Groups)
					{
						w.WriteStartObject();
						w.WriteNumber("x", group.X);
						w.WriteStartArray("items");
						foreach (string item in group.Items)
						{
							w.WriteStringValue(item);
						}
						w.WriteEndArray();
						w.WriteEndObject();
					}
					w.WriteEndArray();
				}
				w.WriteBoolean("hoverTip", HoverTip);
				w.WriteBoolean("hoverChart", HoverChart);
				w.WriteBoolean("showCpu", ShowCpu);
				w.WriteBoolean("showGpu", ShowGpu);
				w.WriteBoolean("showRam", ShowRam);
				w.WriteString("blocks", string.Join(",", Order()));
				w.WriteNumber("claudeInterval", ClaudeIntervalMin);
				w.WriteString("language", Language);
				WriteLimit(w, "cpuLoad", CpuLoad);
				WriteLimit(w, "cpuTemp", CpuTemp);
				WriteLimit(w, "gpuLoad", GpuLoad);
				WriteLimit(w, "gpuTemp", GpuTemp);
				WriteLimit(w, "vram", Vram);
				WriteLimit(w, "ram", Ram);
				WriteLimit(w, "kbBattery", KbBattery);
				WriteLimit(w, "mouseBattery", MouseBattery);
				WriteLimit(w, "claude", Claude);
				w.WriteEndObject();
			}
			File.WriteAllBytes(JsonPath, memoryStream.ToArray());
			return true;
		}
		catch (Exception ex)
		{
			Log.Write("settings save: " + ex.Message);
			return false;
		}
	}

	private static void WriteLimit(Utf8JsonWriter w, string name, Limit l)
	{
		w.WriteStartArray(name);
		w.WriteNumberValue(l.Warn);
		w.WriteNumberValue(l.Hot);
		w.WriteEndArray();
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
