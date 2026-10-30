using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PulseBar.Logi;

internal sealed class LogiConfig
{
	public static readonly string[] KeyNames = new string[6] { "G1", "G2", "G3", "G4", "G5", "MR" };

	public bool Enabled { get; set; } = true;

	public int Active { get; set; }

	public List<Profile> Profiles { get; set; } = new List<Profile>();

	public int IdleMinutes { get; set; } = 3;

	public int IdleBrightness { get; set; } = 10;

	private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseBar");

	private static string FilePath => Path.Combine(Dir, "logitech.json");

	public static LogiConfig Load()
	{
		LogiConfig logiConfig = null;
		try
		{
			if (File.Exists(FilePath))
			{
				logiConfig = JsonSerializer.Deserialize(File.ReadAllText(FilePath), LogiJson.Default.LogiConfig);
			}
		}
		catch (Exception ex)
		{
			Log.Write("logi: config load: " + ex.Message);
		}
		if (logiConfig == null)
		{
			logiConfig = new LogiConfig();
		}
		if (logiConfig.Profiles.Count == 0)
		{
			logiConfig.Profiles.Add(new Profile
			{
				Name = "M1",
				Keyboard = 
				{
					Color = "#00A0FF"
				},
				Logo = 
				{
					Color = "#00A0FF"
				}
			});
			logiConfig.Profiles.Add(new Profile
			{
				Name = "M2",
				Keyboard = 
				{
					Color = "#FF4000"
				},
				Logo = 
				{
					Color = "#FF4000"
				}
			});
			logiConfig.Profiles.Add(new Profile
			{
				Name = "M3",
				Keyboard = 
				{
					Color = "#FFFFFF"
				},
				Logo = 
				{
					Color = "#FFFFFF"
				}
			});
		}
		foreach (Profile profile in logiConfig.Profiles)
		{
			Lighting[] array = new Lighting[2] { profile.Keyboard, profile.Logo };
			foreach (Lighting lighting in array)
			{
				if (lighting.Effect != "off")
				{
					lighting.Effect = "static";
				}
			}
		}
		logiConfig.IdleMinutes = Math.Clamp(logiConfig.IdleMinutes, 0, 240);
		logiConfig.IdleBrightness = Math.Clamp(logiConfig.IdleBrightness, 0, 100);
		logiConfig.Active = Math.Clamp(logiConfig.Active, 0, logiConfig.Profiles.Count - 1);
		return logiConfig;
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Dir);
			string text = FilePath + ".tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(this, LogiJson.Default.LogiConfig));
			File.Move(text, FilePath, overwrite: true);
		}
		catch (Exception ex)
		{
			Log.Write("logi: config save: " + ex.Message);
		}
	}
}
