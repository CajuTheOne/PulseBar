using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PulseBar.Logi;

// Настройки модуля Logitech: %APPDATA%\PulseBar\logitech.json (2.3, версия 2).
// Active — профиль, выбранный вручную последним (M1–M3 или в окне настроек); автопереключение по программе его не меняет.
// Файл 2.2 и раньше (без "version") переносится: цвета клавиш → режим «по клавишам», ровно 3 профиля; прежний файл
// сохраняется как logitech.v1.json.
internal sealed class LogiConfig
{
	public const int CurrentVersion = 2;

	public const int ProfileCount = 3;

	public static readonly string[] KeyNames = new string[6] { "G1", "G2", "G3", "G4", "G5", "MR" };

	public int Version { get; set; }

	public bool Enabled { get; set; } = true;

	public int Active { get; set; }

	public List<Profile> Profiles { get; set; } = new List<Profile>();

	public int IdleMinutes { get; set; } = 3;

	public int IdleBrightness { get; set; } = 10;

	private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar");

	public static string FilePath => Path.Combine(Dir, "logitech.json");

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
			try
			{
				File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "logitech.v1.json"), overwrite: false);
			}
			catch (IOException)
			{
			}
			foreach (Profile p in c.Profiles)
			{
				p.PerKey = p.KeyColors.Count > 0;
			}
			c.Version = CurrentVersion;
			Log.Write("logi: logitech.json migrated to version 2");
		}
		c.Normalize();
		if (migrate)
		{
			c.Save(path);
		}
		return c;
	}

	// Ровно 3 профиля, известные эффекты, разумные числа.
	public void Normalize()
	{
		string[] defaults = new string[3] { "#00A0FF", "#FF4000", "#FFFFFF" };
		if (Profiles.Count > ProfileCount)
		{
			Profiles.RemoveRange(ProfileCount, Profiles.Count - ProfileCount);
		}
		while (Profiles.Count < ProfileCount)
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
			p.Brightness = Math.Clamp(p.Brightness, 1, 100);
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
		IdleMinutes = Math.Clamp(IdleMinutes, 0, 240);
		IdleBrightness = Math.Clamp(IdleBrightness, 0, 100);
		Active = Math.Clamp(Active, 0, ProfileCount - 1);
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
