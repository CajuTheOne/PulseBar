using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PulseBar.Logi;

namespace PulseBar.Tests;

// Память клавиатуры: разбор и сборка секторов на копии памяти настоящей G915 (Data\g915-onboard-backup.json).
[TestClass]
public class OnboardTests
{
	private static Dictionary<int, byte[]> Backup()
	{
		return OnboardMemory.LoadBackup(Path.Combine(AppContext.BaseDirectory, "Data", "g915-onboard-backup.json"));
	}

	private static LogiConfig Config(Action<Profile>? edit = null)
	{
		LogiConfig c = new LogiConfig
		{
			Version = LogiConfig.CurrentVersion,
			IdleMinutes = 5,
			IdleBrightness = 10
		};
		c.Normalize();
		edit?.Invoke(c.Profiles[0]);
		return c;
	}

	[TestMethod]
	public void DirectoryListsThreeProfiles()
	{
		byte[] dir = Backup()[0];
		Assert.IsTrue(OnboardMemory.CrcOk(dir));
		CollectionAssert.AreEqual(new[] { (1, true), (2, true), (3, true) }, OnboardMemory.ParseDirectory(dir));
	}

	[TestMethod]
	public void UserSectorsRoundTrip()
	{
		Dictionary<int, byte[]> b = Backup();
		for (int s = 1; s <= 3; s++)
		{
			OnboardSector o = new OnboardSector(b[s]);
			Assert.IsTrue(o.CrcOk, $"sector {s}");
			CollectionAssert.AreEqual(b[s], o.Serialize(), $"sector {s}");
		}
	}

	[TestMethod]
	public void FactoryProfilesAsReadFromKeyboard()
	{
		Dictionary<int, byte[]> b = Backup();
		OnboardSector p2 = new OnboardSector(b[2]);
		Assert.AreEqual("breathe #00DCFF 3000 ms, 100%", OnboardSector.DescribeLed(p2.Led(OnboardSector.LedKeys)));
		Assert.AreEqual("special 0D 01", OnboardSector.DescribeButton(p2.Button(5)));
		Assert.AreEqual(0x04, new OnboardSector(b[1]).Led(OnboardSector.LedKeys)[0]);
		Assert.AreEqual(60, p2.PowerSaveSeconds);
		Assert.AreEqual(300, p2.PowerOffSeconds);
	}

	[TestMethod]
	public void PerKeyProfileStoresBaseColorScaledByBrightness()
	{
		LogiConfig c = Config(p =>
		{
			p.Name = "Work";
			p.PerKey = true;
			p.Keyboard.Color = "#DE4AFD";
			p.KeyColors["17"] = "#FF8000";
			p.Brightness = 50;
		});
		OnboardSector o = OnboardPlan.Build(new OnboardSector(Backup()[1]), c.Profiles[0], c);
		Assert.AreEqual("static #6F257E", OnboardSector.DescribeLed(o.Led(OnboardSector.LedKeys)));
		Assert.AreEqual("static #160719", OnboardSector.DescribeLed(o.Led(OnboardSector.LedKeysDim)), "dim = 10 % of the colour");
		Assert.AreEqual("Work", o.Name);
		Assert.AreEqual(300, o.PowerSaveSeconds);
		Assert.IsTrue(o.PowerOffSeconds >= 360);
		Assert.IsTrue(OnboardMemory.CrcOk(o.Serialize()));
	}

	[TestMethod]
	public void EffectsMapToOnboardEntries()
	{
		LogiConfig c = Config(p =>
		{
			p.Keyboard.Effect = "breathing";
			p.Keyboard.Color = "#00FF00";
			p.Keyboard.Period = 2000;
			p.Logo.Effect = "cycle";
		});
		OnboardSector o = OnboardPlan.Build(new OnboardSector(Backup()[2]), c.Profiles[0], c);
		Assert.AreEqual("breathe #00FF00 2000 ms, 100%", OnboardSector.DescribeLed(o.Led(OnboardSector.LedKeys)));
		Assert.AreEqual("cycle 5000 ms, 100%", OnboardSector.DescribeLed(o.Led(OnboardSector.LedLogo)));
		Assert.AreEqual("breathe #00FF00 2000 ms, 10%", OnboardSector.DescribeLed(o.Led(OnboardSector.LedKeysDim)));
	}

	[TestMethod]
	public void WaveKeepsFirmwareParameters()
	{
		LogiConfig c = Config(p => p.Keyboard.Effect = "wave");
		byte[] basis = Backup()[1];
		OnboardSector o = OnboardPlan.Build(new OnboardSector(basis), c.Profiles[0], c);
		byte[] led = o.Led(OnboardSector.LedKeys);
		byte[] was = new OnboardSector(basis).Led(OnboardSector.LedKeys);
		Assert.AreEqual(0x04, led[0]);
		CollectionAssert.AreEqual(was[..9], led[..9]);
		Assert.AreEqual(100, led[9]);
		Assert.AreEqual(10, o.Led(OnboardSector.LedKeysDim)[9]);
	}

	[TestMethod]
	public void ButtonsFollowActions()
	{
		LogiConfig c = Config(p =>
		{
			p.Key("G1").Type = "keys";
			p.Key("G1").Value = "Ctrl+Shift+F5";
			p.Key("G2").Type = "keys";
			p.Key("G2").Value = "PlayPause";
			p.Key("G3").Type = "text";
			p.Key("G3").Value = "Hello";
		});
		OnboardSector o = OnboardPlan.Build(new OnboardSector(Backup()[1]), c.Profiles[0], c);
		Assert.AreEqual("key mod 03 usage 3E", OnboardSector.DescribeButton(o.Button(0)));
		Assert.AreEqual("consumer 00CD", OnboardSector.DescribeButton(o.Button(1)));
		Assert.AreEqual("disabled", OnboardSector.DescribeButton(o.Button(2)), "text works only with PulseBar");
		Assert.AreEqual("disabled", OnboardSector.DescribeButton(o.Button(3)), "no action");
		Assert.AreEqual("special 0D 02", OnboardSector.DescribeButton(o.Button(6)));
		Assert.IsTrue(OnboardPlan.HostOnly(c.Profiles[0].Key("G3")));
		Assert.IsFalse(OnboardPlan.HostOnly(c.Profiles[0].Key("G1")));
	}

	[TestMethod]
	public void NeverDimKeepsDimCopiesEqual()
	{
		LogiConfig c = Config();
		c.IdleMinutes = 0;
		OnboardSector o = OnboardPlan.Build(new OnboardSector(Backup()[3]), c.Profiles[0], c);
		CollectionAssert.AreEqual(o.Led(OnboardSector.LedKeys), o.Led(OnboardSector.LedKeysDim));
		CollectionAssert.AreEqual(o.Led(OnboardSector.LedLogo), o.Led(OnboardSector.LedLogoDim));
	}

	[TestMethod]
	public void BasisFallsBackToFactorySector()
	{
		byte[] empty = new byte[OnboardSector.Size];
		Array.Fill(empty, (byte)0xFF);
		byte[] rom = Backup()[2];
		byte[] built = OnboardPlan.Basis(empty, rom).Serialize();
		// Заводские секторы хранятся без CRC; собранный из них сектор пользователя получает свою CRC.
		CollectionAssert.AreEqual(rom[..^2], built[..^2]);
		Assert.IsTrue(OnboardMemory.CrcOk(built));
	}
}
