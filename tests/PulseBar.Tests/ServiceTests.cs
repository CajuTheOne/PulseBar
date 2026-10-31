using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PulseBar.Logi;

namespace PulseBar.Tests;

// Служба Logitech целиком на модели G915: режим PulseBar, M1–M3, G-клавиши, автопереключение, приглушение, G HUB,
// выход, пробуждение, память клавиатуры.
[TestClass]
public class ServiceTests
{
	private sealed class Rig : IDisposable
	{
		public readonly FakeG915 Kb;

		public readonly LogiService Svc;

		public readonly string Dir;

		public readonly string ConfigPath;

		public readonly List<string> Actions = new List<string>();

		public volatile bool GHub;

		public Rig(Action<LogiConfig>? edit = null, Action<FakeG915>? before = null)
		{
			Dir = Path.Combine(Path.GetTempPath(), "PulseBarTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Dir);
			ConfigPath = Path.Combine(Dir, "logitech.json");
			Kb = new FakeG915(OnboardMemory.LoadBackup(Path.Combine(AppContext.BaseDirectory, "Data", "g915-onboard-backup.json")));
			before?.Invoke(Kb);
			LogiConfig c = new LogiConfig
			{
				Version = LogiConfig.CurrentVersion,
				IdleMinutes = 0
			};
			c.Normalize();
			edit?.Invoke(c);
			c.Save(ConfigPath);
			LogiEnv env = new LogiEnv
			{
				OpenLinks = () => new List<HidppLink> { new HidppLink(Kb, 0xC541, receiver: true, "fake") },
				PresentPaths = () => new string[1] { "fake" },
				GHubRunning = () => GHub,
				FindMice = () => new List<PointingDevice>(),
				RunAction = a => Actions.Add(a.Value),
				ConfigPath = ConfigPath,
				AppData = Dir,
				SystemEvents = false,
				Timers = false,
				FirstSyncMs = int.MaxValue / 2
			};
			Svc = new LogiService(LogiConfig.Load(ConfigPath), readOnly: false, env);
			Svc.Start();
			Settle();
		}

		// Уведомления идут через поток событий канала, затем через поток службы.
		public void Settle()
		{
			for (int i = 0; i < 3; i++)
			{
				Thread.Sleep(30);
				Svc.Flush();
			}
		}

		public void Dispose()
		{
			Svc.Dispose();
			try
			{
				Directory.Delete(Dir, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	private static Rgb KeysColor(FakeG915 kb)
	{
		byte[] p = kb.Effects[0].Params;
		return new Rgb(p[0], p[1], p[2]);
	}

	[TestMethod]
	public void StartsInPulseBarModeWithActiveProfile()
	{
		using Rig r = new Rig(c => c.Profiles[0].Keyboard.Color = "#102030");
		Assert.AreEqual(2, r.Kb.Mode);
		Assert.IsTrue(r.Kb.Diverted);
		Assert.AreEqual(1, r.Kb.MLeds);
		Assert.AreEqual(100, r.Kb.Brightness);
		Assert.AreEqual(0x01, r.Kb.Effects[0].Effect);
		Assert.AreEqual(new Rgb(0x10, 0x20, 0x30), KeysColor(r.Kb));
		Assert.AreEqual("host", r.Svc.Mode);
	}

	[TestMethod]
	public void MKeysSwitchProfilesWithoutLeavingPulseBarMode()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Keyboard.Color = "#FF0000";
			c.Profiles[1].Keyboard.Color = "#00FF00";
			c.Profiles[2].Keyboard.Color = "#0000FF";
		});
		r.Kb.PressM(2);
		r.Settle();
		Assert.AreEqual(2, r.Kb.MLeds);
		Assert.AreEqual(new Rgb(0, 0xFF, 0), KeysColor(r.Kb));
		// 2.2: повторное M1 включало M2 — теперь M1 дважды оставляет M1.
		r.Kb.PressM(1);
		r.Settle();
		r.Kb.PressM(1);
		r.Settle();
		Assert.AreEqual(1, r.Kb.MLeds);
		Assert.AreEqual(0, r.Svc.Current);
		Assert.AreEqual(new Rgb(0xFF, 0, 0), KeysColor(r.Kb));
		CollectionAssert.DoesNotContain(r.Kb.ModeChanges, 1, "never back to keyboard memory while running");
		Assert.AreEqual(0, LogiConfig.Load(r.ConfigPath).Active, "manual choice is saved");
	}

	[TestMethod]
	public void PerKeyProfilePaintsBaseAndKeyColours()
	{
		using Rig r = new Rig(c =>
		{
			Profile p = c.Profiles[0];
			p.PerKey = true;
			p.Keyboard.Color = "#102030";
			p.KeyColors["17"] = "#FF8000";
			p.Logo.Color = "#00FF00";
		});
		Assert.AreEqual(new Rgb(0xFF, 0x80, 0), r.Kb.Frame[0x17], "W");
		Assert.AreEqual(new Rgb(0x10, 0x20, 0x30), r.Kb.Frame[0x01], "A");
		Assert.IsFalse(r.Kb.Frame.ContainsKey(KeyLayout.Logo), "logo keeps its own zone");
		Assert.AreEqual(0x01, r.Kb.Effects[1].Effect);
		Assert.IsTrue(r.Svc.KeyIds.Count > 90);
	}

	[TestMethod]
	public void SameFrameIsNotSentTwice()
	{
		using Rig r = new Rig();
		int calls = r.Kb.Calls.Count;
		r.Svc.SelectProfile(0);
		r.Settle();
		Assert.AreEqual(calls, r.Kb.Calls.Count);
	}

	[TestMethod]
	public void GKeysAndMrRunTheirActionOnceWhenHeld()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Key("G1").Type = "text";
			c.Profiles[0].Key("G1").Value = "hello";
			c.Profiles[0].Key("MR").Type = "run";
			c.Profiles[0].Key("MR").Value = "notepad";
		});
		r.Kb.PressG(1, repeats: 5);
		r.Kb.PressMR();
		r.Settle();
		CollectionAssert.AreEqual(new[] { "hello", "notepad" }, r.Actions);
	}

	[TestMethod]
	public void ProgramProfileAndManualOverride()
	{
		using Rig r = new Rig(c => c.Profiles[2].Apps = "game.exe");
		r.Svc.Foreground("game.exe");
		r.Settle();
		Assert.AreEqual(2, r.Svc.Current);
		Assert.AreEqual(4, r.Kb.MLeds);
		// M2 при открытой игре действует, пока игра в фокусе.
		r.Kb.PressM(2);
		r.Settle();
		Assert.AreEqual(1, r.Svc.Current);
		// Ушла из фокуса — профиль, выбранный вручную последним (M2).
		r.Svc.Foreground("explorer.exe");
		r.Settle();
		Assert.AreEqual(1, r.Svc.Current);
		// Вернулась — снова её профиль.
		r.Svc.Foreground("game.exe");
		r.Settle();
		Assert.AreEqual(2, r.Svc.Current);
	}

	[TestMethod]
	public void ScreenOffTurnsTheLightOff()
	{
		using Rig r = new Rig(c => c.Profiles[0].Brightness = 80);
		r.Svc.DisplayChanged(on: false);
		r.Settle();
		Assert.AreEqual(0, r.Kb.Brightness);
		r.Svc.DisplayChanged(on: true);
		r.Settle();
		Assert.AreEqual(80, r.Kb.Brightness);
	}

	[TestMethod]
	public void LowBatteryCapsBrightness()
	{
		using Rig r = new Rig(null, kb => kb.BatteryMv = 3660);
		Assert.IsTrue(r.Svc.KeyboardBattery < 15);
		Assert.AreEqual(30, r.Kb.Brightness);
	}

	[TestMethod]
	public void GHubTakesTheKeyboardAndGivesItBack()
	{
		using Rig r = new Rig();
		r.Kb.PressM(3);
		r.Settle();
		r.GHub = true;
		r.Svc.CheckNow();
		Assert.AreEqual(1, r.Kb.Mode);
		Assert.AreEqual(3, r.Kb.Profile);
		Assert.IsFalse(r.Kb.Diverted);
		Assert.AreEqual("ghub", r.Svc.Mode);
		r.Kb.PressM(1);
		r.Settle();
		Assert.AreEqual(2, r.Svc.Current, "M keys belong to G HUB now");
		r.GHub = false;
		r.Svc.CheckNow();
		r.Settle();
		Assert.AreEqual(2, r.Kb.Mode);
		Assert.IsTrue(r.Kb.Diverted);
	}

	[TestMethod]
	public void ExitReturnsTheKeyboardToItsMemory()
	{
		Rig r = new Rig();
		r.Kb.PressM(2);
		r.Settle();
		r.Dispose();
		Assert.AreEqual(1, r.Kb.Mode);
		Assert.AreEqual(2, r.Kb.Profile);
		Assert.IsFalse(r.Kb.Diverted);
	}

	[TestMethod]
	public void WakeSetsTheKeyboardUpAgain()
	{
		using Rig r = new Rig();
		r.Kb.PressM(2);
		r.Settle();
		r.Kb.Sleep();
		r.Settle();
		Assert.AreEqual("offline", r.Svc.Mode);
		r.Kb.Wake();
		r.Settle();
		Assert.AreEqual(2, r.Kb.Mode);
		Assert.IsTrue(r.Kb.Diverted);
		Assert.AreEqual(2, r.Kb.MLeds);
		Assert.IsTrue(r.Kb.Effects.ContainsKey(0));
	}

	[TestMethod]
	public void ReloadKeepsTheManualProfile()
	{
		using Rig r = new Rig();
		r.Kb.PressM(2);
		r.Settle();
		LogiConfig c = LogiConfig.Load(r.ConfigPath);
		c.Active = 0;
		c.Profiles[1].Keyboard.Color = "#ABCDEF";
		c.Save(r.ConfigPath);
		r.Svc.ReloadConfig();
		r.Settle();
		Assert.AreEqual(1, r.Svc.Current);
		Assert.AreEqual(new Rgb(0xAB, 0xCD, 0xEF), KeysColor(r.Kb));
	}

	[TestMethod]
	public void KeyboardMemoryIsWrittenOnceAndOnlyWhenChanged()
	{
		using Rig r = new Rig(c => c.Profiles[0].Keyboard.Color = "#123456");
		r.Svc.SyncOnboardNow();
		CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, r.Kb.SectorWrites);
		Assert.IsTrue(Directory.EnumerateFiles(r.Dir, "logitech-backup-*.json").Any(), "backup before the first write");
		OnboardSector s1 = new OnboardSector(r.Kb.Sectors[1]);
		Assert.IsTrue(s1.CrcOk);
		Assert.AreEqual("static #123456", OnboardSector.DescribeLed(s1.Led(OnboardSector.LedKeys)));
		Assert.AreEqual(2, r.Kb.Mode, "writing memory does not leave PulseBar mode");
		r.Kb.SectorWrites.Clear();
		r.Svc.SyncOnboardNow();
		Assert.AreEqual(0, r.Kb.SectorWrites.Count);
	}
}

[TestClass]
public class KeyEdgesTests
{
	[TestMethod]
	public void OnlyTheEdgeOfAPressCounts()
	{
		KeyEdges e = new KeyEdges();
		CollectionAssert.AreEqual(new[] { "M1" }, e.Feed(LogiDevice.MKeys, 0, new byte[2] { 1, 0 }));
		Assert.AreEqual(0, e.Feed(LogiDevice.MKeys, 0, new byte[2] { 1, 0 }).Count, "same state again");
		Assert.AreEqual(0, e.Feed(LogiDevice.MKeys, 0, new byte[2]).Count, "release");
		CollectionAssert.AreEqual(new[] { "M1" }, e.Feed(LogiDevice.MKeys, 0, new byte[2] { 1, 0 }));
		CollectionAssert.AreEqual(new[] { "G2", "G5" }, e.Feed(LogiDevice.GKeys, 0, new byte[2] { 0x12, 0 }));
		CollectionAssert.AreEqual(new[] { "G1" }, e.Feed(LogiDevice.GKeys, 0, new byte[2] { 0x13, 0 }));
		CollectionAssert.AreEqual(new[] { "MR" }, e.Feed(LogiDevice.MacroRecord, 0, new byte[2] { 1, 0 }));
		Assert.AreEqual(0, e.Feed(LogiDevice.MacroRecord, 0, new byte[2] { 1, 0 }).Count);
	}
}
