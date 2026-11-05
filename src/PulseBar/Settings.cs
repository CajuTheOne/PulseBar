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

		public static readonly Limit HeadsetBattery = new Limit(20, 10);

		public static readonly Limit Claude = new Limit(70, 90);

		public static readonly Limit ChatGpt = new Limit(70, 90);

		public static readonly Limit Grok = new Limit(70, 90);

		// Баланс DeepSeek: жёлтый при остатке не больше 5, красный — не больше 1 (в валюте счёта).
		public static readonly Limit DeepSeek = new Limit(5, 1);
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

	// «Собирать данные для отладки» (2.4.0-beta.12, см. DebugReports): выключено — ничего не собирается и не уходит.
	public bool DebugReports;

	// Блок Claude: 0 — выключен, 1 — компактный (между блоками), 2 — большой (полосы 5h и 7d). Выключенный блок помнит
	// свой вид (ClaudeOnStyle): плитка в «Виде» включает его таким, каким он был (пользователь, 02.11.2026).
	public int ClaudeStyle
	{
		get
		{
			return _claudeStyle;
		}
		set
		{
			if (value != 0)
			{
				ClaudeOnStyle = value;
			}
			_claudeStyle = value;
		}
	}

	private int _claudeStyle = 1;

	public int ClaudeOnStyle = 1;

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
				ClaudeStyle = ClaudeOnStyle;
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

	// Строка ИИ (2.4): ChatGPT (лимиты Codex), Grok (Grok CLI), DeepSeek (баланс API); Gemini — пока без данных.
	// Блок ChatGPT — как Claude: 0 — выключен, 1 — компактный, 2 — большой (полосы 5h и 7d); свои размеры полос.
	public int GptStyle
	{
		get
		{
			return _gptStyle;
		}
		set
		{
			if (value != 0)
			{
				GptOnStyle = value;
			}
			_gptStyle = value;
		}
	}

	private int _gptStyle;

	public int GptOnStyle = 1;

	public int GptBarLength = 78;

	public int GptBarHeight = 8;

	public int GptSegments = 10;

	public bool ShowChatGpt
	{
		get
		{
			return GptStyle != 0;
		}
		set
		{
			if (!value)
			{
				GptStyle = 0;
			}
			else if (GptStyle == 0)
			{
				GptStyle = GptOnStyle;
			}
		}
	}

	// Свои пресеты вида: имя и снимок настроек вида (JSON с ключами AppearanceKeys).
	public sealed record LookPreset(string Name, string Json);

	public List<LookPreset> Presets = new List<LookPreset>();

	// Что входит в «вид»: раскладка, элементы, размеры, цвета; язык, видеокарта, ключи и пороги — нет.
	private static readonly HashSet<string> AppearanceKeys = new HashSet<string>
	{
		"layout", "blueNormal", "textSize", "showRamGb", "showVram", "groupGap", "trayGap", "showBattery", "claudeStyle", "claudeOnStyle",
		"gptOnStyle", "batteryOrder",
		"claudeBarLength", "claudeBarHeight", "claudeSegments", "snap", "snapGap", "sepGap", "groups", "hoverTip", "hoverChart",
		"showCpu", "showGpu", "showRam", "blocks", "showChatGpt", "gptStyle", "gptBarLength", "gptBarHeight", "gptSegments",
		"showGrok", "showDeepSeek", "showGemini", "aiTiles", "normalColor", "warnColor", "hotColor"
	};

	// Снимок вида для пресета.
	public string AppearanceJson()
	{
		using JsonDocument doc = JsonDocument.Parse(ToJson(presets: false));
		using MemoryStream ms = new MemoryStream();
		using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
		{
			w.WriteStartObject();
			foreach (JsonProperty p in doc.RootElement.EnumerateObject())
			{
				if (AppearanceKeys.Contains(p.Name))
				{
					p.WriteTo(w);
				}
			}
			w.WriteEndObject();
		}
		return System.Text.Encoding.UTF8.GetString(ms.ToArray());
	}

	// Загрузить вид из пресета (остальные настройки не меняются).
	public void ApplyAppearance(string json)
	{
		using JsonDocument doc = JsonDocument.Parse(json);
		foreach (JsonProperty p in doc.RootElement.EnumerateObject())
		{
			if (!AppearanceKeys.Contains(p.Name))
			{
				continue;
			}
			if (p.Name == "groups")
			{
				Groups = ReadGroups(p.Value);
			}
			else
			{
				Apply(p.Name, JsonText(p.Value));
			}
		}
		if (!doc.RootElement.TryGetProperty("groups", out _))
		{
			Groups = null;
		}
		NormalizeGroups();
	}

	public bool ShowGrok;

	public bool ShowDeepSeek;

	public bool ShowGemini;

	// Ключ API DeepSeek, зашифрованный DPAPI текущего пользователя (Ai.Secret).
	public string DeepSeekKey = "";

	// Палитра показателей: норма, предупреждение, перегрузка (#RRGGBB; пусто — цвета темы).
	public string NormalColor = "";

	public string WarnColor = "";

	public string HotColor = "";

	public static readonly string[] AllBlocks = new string[9] { "cpu", "gpu", "vram", "ram", "claude", "chatgpt", "grok", "deepseek", "battery" };

	// Плитки строки ИИ в окне настроек (Gemini — без данных, только в меню «…»).
	public static readonly string[] AiBlocks = new string[5] { "claude", "chatgpt", "grok", "gemini", "deepseek" };

	// Какие плитки стоят в строке ИИ окна настроек (меню «…»).
	public string AiTiles = "claude,chatgpt,grok,deepseek";

	public List<string> AiTileList()
	{
		return AiTiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(AiBlocks.Contains).Distinct().ToList();
	}

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

	public Limit HeadsetBattery = Defaults.HeadsetBattery;

	// Порядок значков в блоке заряда (2.4.0-beta.11, пользователь: «надо уметь делать наушники + мышь, наушники +
	// клавиатура»): в «Двух строках» 1-е над 2-м, 3-е рядом; выключенные и отключённые пропускаются, пары — заново.
	public static readonly string[] BatteryDevices = new string[3] { "keyboard", "mouse", "headset" };

	public string BatteryOrder = "keyboard,mouse,headset";

	public string[] BatteryOrderList()
	{
		return NormalizeBatteryOrder(BatteryOrder);
	}

	// Каждое устройство ровно один раз; неизвестные — прочь, недостающие — в конец.
	public static string[] NormalizeBatteryOrder(string text)
	{
		List<string> list = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where((string x) => BatteryDevices.Contains(x)).Distinct().ToList();
		list.AddRange(BatteryDevices.Where((string x) => !list.Contains(x)));
		return list.ToArray();
	}

	public Limit Claude = Defaults.Claude;

	public Limit ChatGpt = Defaults.ChatGpt;

	public Limit Grok = Defaults.Grok;

	public Limit DeepSeek = Defaults.DeepSeek;

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
			"chatgpt" => ShowChatGpt,
			"grok" => ShowGrok,
			"deepseek" => ShowDeepSeek,
			"gemini" => ShowGemini,
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
		case "chatgpt":
			ShowChatGpt = shown;
			break;
		case "grok":
			ShowGrok = shown;
			break;
		case "deepseek":
			ShowDeepSeek = shown;
			break;
		case "gemini":
			ShowGemini = shown;
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
					if (item.Name == "presets")
					{
						settings.Presets = ReadPresets(item.Value);
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

	private static List<LookPreset> ReadPresets(JsonElement v)
	{
		List<LookPreset> list = new List<LookPreset>();
		if (v.ValueKind != JsonValueKind.Array)
		{
			return list;
		}
		foreach (JsonElement item in v.EnumerateArray())
		{
			if (item.TryGetProperty("name", out JsonElement n) && item.TryGetProperty("look", out JsonElement look) && look.ValueKind == JsonValueKind.Object)
			{
				list.Add(new LookPreset(n.GetString() ?? "", look.GetRawText()));
			}
		}
		return list;
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
		case "debugReports":
			this.DebugReports = result != 0;
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
		case "claudeOnStyle":
			this.ClaudeOnStyle = Math.Clamp(result, 1, 2);
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
		case "headsetBattery":
			this.HeadsetBattery = ParseLow(text4, this.HeadsetBattery);
			break;
		case "batteryOrder":
			this.BatteryOrder = string.Join(",", NormalizeBatteryOrder(text4));
			break;
		case "showChatGpt":
			this.ShowChatGpt = result != 0;
			break;
		case "showGrok":
			this.ShowGrok = result != 0;
			break;
		case "showDeepSeek":
			this.ShowDeepSeek = result != 0;
			break;
		case "showGemini":
			this.ShowGemini = result != 0;
			break;
		case "deepSeekKey":
			this.DeepSeekKey = text4;
			break;
		case "aiTiles":
			this.AiTiles = text4;
			break;
		case "gptStyle":
			this.GptStyle = Math.Clamp(result, 0, 2);
			break;
		case "gptOnStyle":
			this.GptOnStyle = Math.Clamp(result, 1, 2);
			break;
		case "gptBarLength":
			this.GptBarLength = Math.Clamp(result, 40, 240);
			break;
		case "gptBarHeight":
			this.GptBarHeight = Math.Clamp(result, 4, 16);
			break;
		case "gptSegments":
			this.GptSegments = (result <= 0) ? 0 : Math.Clamp(result, 5, 20);
			break;
		case "chatgpt":
			this.ChatGpt = ParseLimit(text4, this.ChatGpt);
			break;
		case "grok":
			this.Grok = ParseLimit(text4, this.Grok);
			break;
		case "deepseek":
			this.DeepSeek = ParseLow(text4, this.DeepSeek);
			break;
		case "normalColor":
			this.NormalColor = ColorOrEmpty(text4);
			break;
		case "warnColor":
			this.WarnColor = ColorOrEmpty(text4);
			break;
		case "hotColor":
			this.HotColor = ColorOrEmpty(text4);
			break;
		}
	}

	public bool Save()
	{
		Version++;
		try
		{
			Directory.CreateDirectory(Dir);
			File.WriteAllBytes(JsonPath, ToJson(presets: true));
			return true;
		}
		catch (Exception ex)
		{
			Log.Write("settings save: " + ex.Message);
			return false;
		}
	}

	// Настройки в JSON; presets = false — без своих пресетов вида (для снимка вида в пресет).
	private byte[] ToJson(bool presets)
	{
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
			w.WriteBoolean("debugReports", DebugReports);
			w.WriteNumber("claudeStyle", ClaudeStyle);
			w.WriteNumber("claudeOnStyle", ClaudeOnStyle);
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
			WriteLimit(w, "headsetBattery", HeadsetBattery);
			w.WriteString("batteryOrder", BatteryOrder);
			WriteLimit(w, "claude", Claude);
			w.WriteBoolean("showChatGpt", ShowChatGpt);
			w.WriteNumber("gptStyle", GptStyle);
			w.WriteNumber("gptOnStyle", GptOnStyle);
			w.WriteNumber("gptBarLength", GptBarLength);
			w.WriteNumber("gptBarHeight", GptBarHeight);
			w.WriteNumber("gptSegments", GptSegments);
			w.WriteBoolean("showGrok", ShowGrok);
			w.WriteBoolean("showDeepSeek", ShowDeepSeek);
			w.WriteBoolean("showGemini", ShowGemini);
			w.WriteString("deepSeekKey", DeepSeekKey);
			w.WriteString("aiTiles", AiTiles);
			WriteLimit(w, "chatgpt", ChatGpt);
			WriteLimit(w, "grok", Grok);
			WriteLimit(w, "deepseek", DeepSeek);
			w.WriteString("normalColor", NormalColor);
			w.WriteString("warnColor", WarnColor);
			w.WriteString("hotColor", HotColor);
			if (presets && Presets.Count > 0)
			{
				w.WriteStartArray("presets");
				foreach (LookPreset lp in Presets)
				{
					w.WriteStartObject();
					w.WriteString("name", lp.Name);
					w.WritePropertyName("look");
					using (JsonDocument look = JsonDocument.Parse(lp.Json))
					{
						look.RootElement.WriteTo(w);
					}
					w.WriteEndObject();
				}
				w.WriteEndArray();
			}
			w.WriteEndObject();
		}
		return memoryStream.ToArray();
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

	// #RRGGBB или пусто (цвет темы).
	public static string ColorOrEmpty(string v)
	{
		v = v.Trim();
		if (v.Length == 7 && v[0] == '#' && int.TryParse(v.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
		{
			return v.ToUpperInvariant();
		}
		return "";
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
