using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PulseBar.Logi;

// Настройки модуля Logitech: %APPDATA%\PulseBar\logitech.json (версия 3, 2.3.0-beta.5).
// Профилей сколько угодно; Slots — какой профиль на M1, M2, M3 (номера в Profiles); остальные профили включаются по
// программам. Active — профиль, выбранный вручную последним (M1–M3 или в окне настроек); автопереключение по программе
// его не меняет. У подсветки клавиш статичный режим — базовый цвет и свои цвета клавиш (PerKey = статичный режим).
// Перенос: файл 2.2 и раньше (без "version") — цвета клавиш → режим «по клавишам», прежний файл — logitech.v1.json;
// версия 2 → слоты M1–M3 = профили 1–3, «по клавишам» → статичный режим.
internal sealed class LogiConfig
{
	public const int CurrentVersion = 3;

	// Слотов M1–M3 и профилей во встроенной памяти клавиатуры.
	public const int SlotCount = 3;

	public static readonly string[] KeyNames = new string[6] { "G1", "G2", "G3", "G4", "G5", "MR" };

	public int Version { get; set; }

	public bool Enabled { get; set; } = true;

	public int Active { get; set; }

	public List<Profile> Profiles { get; set; } = new List<Profile>();

	public int[] Slots { get; set; } = new int[3] { 0, 1, 2 };

	// Простой: через IdleSeconds без ввода — приглушить до IdleBrightness, через OffMinutes — погасить; 0 — никогда.
	public int IdleSeconds { get; set; } = 30;

	public int OffMinutes { get; set; } = 10;

	// G915 при 10 % и ниже не светится (пользователь, 01.11.2026): приглушение — 0 (погасить) или 11–100.
	public int IdleBrightness { get; set; } = 20;

	// «Приглушать через N минут» до 2.4.0-beta.4 — только для переноса.
	[System.Text.Json.Serialization.JsonPropertyName("idleMinutes")]
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
	public int LegacyIdleMinutes { get; set; }

	// Яркость 10 % и ниже на G915 — темно; светится с 11 %.
	public const int MinBrightness = 11;

	private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar");

	// Снимки окна настроек (--shot-settings) работают с временной копией, чтобы не переносить и не менять файл пользователя.
	internal static string? PathOverride;

	public static string FilePath => PathOverride ?? Path.Combine(Dir, "logitech.json");

	public static LogiConfig Load()
	{
		return Load(FilePath);
	}

	public static LogiConfig Load(string path)
	{
		LogiConfig? c = null;
		try
		{
			if (File.Exists(path))
			{
				c = JsonSerializer.Deserialize(File.ReadAllText(path), LogiJson.Default.LogiConfig);
			}
		}
		catch (Exception ex)
		{
			Log.Write("logi: config load: " + ex.Message);
		}
		bool migrate = c != null && c.Version < CurrentVersion;
		c ??= new LogiConfig
		{
			Version = CurrentVersion
		};
		if (migrate)
		{
			string copy = (c.Version < 2) ? "logitech.v1.json" : $"logitech.v{c.Version}.json";
			try
			{
				File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, copy), overwrite: false);
			}
			catch (IOException)
			{
			}
			if (c.Version < 2)
			{
				foreach (Profile p in c.Profiles)
				{
					p.LegacyPerKey = p.KeyColors.Count > 0;
				}
			}
			foreach (Profile p in c.Profiles)
			{
				// Был «Цвет» без своих цветов клавиш — старые цвета клавиш не всплывают; был «По клавишам» — статичный режим.
				if (p.LegacyPerKey)
				{
					p.Keyboard.Effect = "static";
				}
				else if (p.Keyboard.Effect == "static")
				{
					p.KeyColors.Clear();
				}
				p.LegacyPerKey = false;
			}
			c.Slots = new int[3] { 0, 1, 2 };
			Log.Write($"logi: logitech.json migrated from version {c.Version} to {CurrentVersion}");
			c.Version = CurrentVersion;
		}
		// Один порог простоя (минуты) → два: приглушить через 30 с, погасить через 10 минут (предложение пользователя).
		bool idle = c.LegacyIdleMinutes != 0;
		if (idle)
		{
			c.IdleSeconds = 30;
			c.OffMinutes = 10;
			c.LegacyIdleMinutes = 0;
			Log.Write("logi: idle: dim after 30 s, off after 10 min");
		}
		c.Normalize();
		if (migrate || idle)
		{
			c.Save(path);
		}
		return c;
	}

	// Хотя бы один профиль (новый файл — три), слоты на существующие профили, известные эффекты, разумные числа.
	public void Normalize()
	{
		string[] defaults = new string[3] { "#00A0FF", "#FF4000", "#FFFFFF" };
		int min = (Profiles.Count == 0) ? SlotCount : 1;
		while (Profiles.Count < min)
		{
			string color = defaults[Profiles.Count];
			Profiles.Add(new Profile
			{
				Name = "M" + (Profiles.Count + 1),
				Keyboard = new Lighting
				{
					Color = color
				},
				Logo = new Lighting
				{
					Color = color
				}
			});
		}
		foreach (Profile p in Profiles)
		{
			p.Keyboard ??= new Lighting();
			p.Logo ??= new Lighting();
			p.Keys ??= new Dictionary<string, KeyAction>();
			p.KeyColors ??= new Dictionary<string, string>();
			// 0 — погашено кнопкой яркости на клавиатуре.
			p.Brightness = (p.Brightness <= 0) ? 0 : Math.Clamp(p.Brightness, MinBrightness, 100);
			p.Name = string.IsNullOrWhiteSpace(p.Name) ? ("Profile " + (Profiles.IndexOf(p) + 1)) : p.Name.Trim();
			foreach (Lighting l in new Lighting[2] { p.Keyboard, p.Logo })
			{
				l.Effect = string.IsNullOrWhiteSpace(l.Effect) ? "static" : l.Effect.Trim().ToLowerInvariant();
				l.Color = Rgb.Parse(l.Color, new Rgb(0, 0xA0, 0xFF)).ToString();
				l.Period = Math.Clamp(l.Period, 0, 60000);
			}
			foreach (string k in p.KeyColors.Keys.ToList())
			{
				if (!byte.TryParse(k, System.Globalization.NumberStyles.HexNumber, null, out _) || Rgb.Parse(p.KeyColors[k], new Rgb(1, 2, 3)) == new Rgb(1, 2, 3))
				{
					p.KeyColors.Remove(k);
				}
			}
		}
		IdleSeconds = Math.Clamp(IdleSeconds, 0, 3600);
		OffMinutes = Math.Clamp(OffMinutes, 0, 240);
		IdleBrightness = (IdleBrightness < MinBrightness) ? 0 : Math.Min(IdleBrightness, 100);
		Active = Math.Clamp(Active, 0, Profiles.Count - 1);
		int[] slots = new int[SlotCount];
		for (int i = 0; i < SlotCount; i++)
		{
			int s = (Slots != null && i < Slots.Length) ? Slots[i] : i;
			slots[i] = Math.Clamp(s, 0, Profiles.Count - 1);
		}
		Slots = slots;
	}

	// Первый слот M1–M3 с этим профилем; -1 — профиль только для программ.
	public int SlotOf(int profile)
	{
		return Array.IndexOf(Slots, profile);
	}

	// Удалить профиль: слоты и Active, указывавшие на него, переходят на первый профиль, номера после него сдвигаются.
	public void RemoveProfile(int index)
	{
		if (Profiles.Count <= 1 || index < 0 || index >= Profiles.Count)
		{
			return;
		}
		Profiles.RemoveAt(index);
		int Fix(int v) => (v == index) ? 0 : ((v > index) ? (v - 1) : v);
		for (int i = 0; i < Slots.Length; i++)
		{
			Slots[i] = Fix(Slots[i]);
		}
		Active = Fix(Active);
	}

	public void Save()
	{
		Save(FilePath);
	}

	public void Save(string path)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			string tmp = path + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(this, LogiJson.Default.LogiConfig));
			File.Move(tmp, path, overwrite: true);
		}
		catch (Exception ex)
		{
			Log.Write("logi: config save: " + ex.Message);
		}
	}
}
