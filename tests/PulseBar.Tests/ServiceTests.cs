using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PulseBar.Logi;

namespace PulseBar.Tests;

// Служба Logitech целиком на модели G915: режим PulseBar, M1–M3, G-клавиши, автопереключение, приглушение, G HUB,
// выход, пробуждение, память клавиатуры, кнопка яркости, чужая подсветка.
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

		public volatile bool DynamicLighting;

		// Часы анимации, мс.
		public long Now;

		// Время с последнего ввода в Windows, мс.
		public long IdleMs = 60000;

		// Сколько раз открывали каналы HID++; мыши и клавиатуры других фирм, которые «найдёт» служба.
		public int Opens;

		public readonly List<PointingDevice> Mice = new List<PointingDevice>();

		public readonly List<PointingDevice> Keyboards = new List<PointingDevice>();

		public Rig(Action<LogiConfig>? edit = null, Action<FakeG915>? before = null)
		{
			Dir = Path.Combine(Path.GetTempPath(), "PulseBarTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Dir);
			ConfigPath = Path.Combine(Dir, "logitech.json");
			Kb = new FakeG915(OnboardMemory.LoadBackup(Path.Combine(AppContext.BaseDirectory, "Data", "g915-onboard-backup.json")));
			before?.Invoke(Kb);
			LogiKeyboard.ForgetKnown();
			LogiConfig c = new LogiConfig
			{
				Version = LogiConfig.CurrentVersion,
				IdleSeconds = 0,
				OffMinutes = 0
			};
			c.Normalize();
			edit?.Invoke(c);
			c.Save(ConfigPath);
			LogiEnv env = new LogiEnv
			{
				OpenLinks = delegate
				{
					Interlocked.Increment(ref Opens);
					return new List<HidppLink> { new HidppLink(Kb, 0xC541, receiver: true, "fake") };
				},
				PresentPaths = () => new string[1] { "fake" },
				GHubRunning = () => GHub,
				FindMice = () => Mice.ToList(),
				FindKeyboards = () => Keyboards.ToList(),
				RunAction = a => Actions.Add(a.Value),
				ConfigPath = ConfigPath,
				AppData = Dir,
				SystemEvents = false,
				Timers = false,
				FirstSyncMs = int.MaxValue / 2,
				DynamicLighting = () => DynamicLighting,
				Now = () => Interlocked.Read(ref Now),
				IdleMs = () => Interlocked.Read(ref IdleMs)
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
		// Клавиша A (номер 1).
		return kb.Frame.TryGetValue(1, out Rgb c) ? c : Rgb.Black;
	}

	[TestMethod]
	public void StartsInPulseBarModeWithActiveProfile()
	{
		using Rig r = new Rig(c => c.Profiles[0].Keyboard.Color = "#102030");
		Assert.AreEqual(2, r.Kb.Mode);
		Assert.IsTrue(r.Kb.Diverted);
		Assert.AreEqual(1, r.Kb.MLeds);
		Assert.AreEqual(100, r.Kb.Brightness);
		Assert.AreEqual(3, r.Kb.SwMode, "software lighting control");
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
			p.Keyboard.Effect = "static";
			p.Keyboard.Color = "#102030";
			p.KeyColors["17"] = "#FF8000";
			p.Logo.Color = "#00FF00";
		});
		Assert.AreEqual(new Rgb(0xFF, 0x80, 0), r.Kb.Frame[0x17], "W");
		Assert.AreEqual(new Rgb(0x10, 0x20, 0x30), r.Kb.Frame[0x01], "A");
		Assert.AreEqual(new Rgb(0, 0xFF, 0), r.Kb.Frame[KeyLayout.Logo], "logo has its own colour");
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
		Assert.IsTrue(r.Kb.Frame.ContainsKey(1), "frame painted again");
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

	[TestMethod]
	public void SoftwareLightingControlIsTurnedOnAndGivenBack()
	{
		Rig r = new Rig(c => c.Profiles[0].Keyboard.Color = "#102030");
		Assert.AreEqual(3, r.Kb.SwMode, "bits 0 and 1");
		Assert.AreEqual(new Rgb(0x10, 0x20, 0x30), KeysColor(r.Kb));
		// Служба LampArray сбросила режим — следующий кадр включает его снова.
		r.Kb.SwMode = 2;
		LogiConfig c = LogiConfig.Load(r.ConfigPath);
		c.Profiles[0].Keyboard.Color = "#405060";
		c.Save(r.ConfigPath);
		r.Svc.ReloadConfig();
		r.Settle();
		Assert.AreEqual(new Rgb(0x40, 0x50, 0x60), KeysColor(r.Kb));
		Assert.AreEqual(3, r.Kb.SwMode, "set again");
		r.Dispose();
		Assert.AreEqual(2, r.Kb.SwMode, "given back on exit");
		Assert.AreEqual(6, r.Kb.SwFlags);
	}

	private static int PerKeyWrites(FakeG915 kb)
	{
		return kb.Calls.Count(x => x.Feature == 0x8081 && x.Fn == 6);
	}

	[TestMethod]
	public void BreathingIsDrawnFrameByFrame()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Keyboard.Effect = "breathing";
			c.Profiles[0].Keyboard.Color = "#FF0000";
			c.Profiles[0].Keyboard.Period = 2000;
		});
		Assert.AreEqual(Rgb.Black, KeysColor(r.Kb), "t = 0: breath out");
		r.Now = 500;
		r.Svc.AnimateNow();
		r.Settle();
		Assert.IsTrue(Math.Abs(KeysColor(r.Kb).R - 128) <= 1 && KeysColor(r.Kb).G == 0, $"quarter period: half ({KeysColor(r.Kb)})");
		r.Now = 1000;
		r.Svc.AnimateNow();
		r.Settle();
		Assert.AreEqual(new Rgb(255, 0, 0), KeysColor(r.Kb), "half period: full colour");
		int writes = PerKeyWrites(r.Kb);
		r.Svc.AnimateNow();
		r.Settle();
		Assert.AreEqual(writes, PerKeyWrites(r.Kb), "nothing changed, nothing sent");
		Assert.IsTrue(writes < 40, $"one colour for all keys goes in groups of 13 ({writes} writes)");
	}

	[TestMethod]
	public void WaveRunsAcrossTheKeyboardAndCycleChangesHue()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Keyboard.Effect = "wave";
			c.Profiles[0].Logo.Effect = "cycle";
			c.Profiles[0].Logo.Period = 3000;
		});
		r.Now = 700;
		r.Svc.AnimateNow();
		r.Settle();
		// Esc (номер 38) слева, Pause (69) справа — разные оттенки; логотип — оттенок по кругу.
		Assert.AreNotEqual(r.Kb.Frame[38], r.Kb.Frame[69]);
		Rgb logo = r.Kb.Frame[KeyLayout.Logo];
		r.Now = 1700;
		r.Svc.AnimateNow();
		r.Settle();
		Assert.AreNotEqual(logo, r.Kb.Frame[KeyLayout.Logo]);
	}

	[TestMethod]
	public void AnimationStopsWhenTheLightIsOffAndComesBack()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Keyboard.Effect = "cycle";
			c.Profiles[0].Keyboard.Color = "#123456";
		});
		r.Svc.DisplayChanged(on: false);
		r.Settle();
		Assert.AreEqual(new Rgb(0x12, 0x34, 0x56), KeysColor(r.Kb), "static colour while the screen is off");
		int writes = PerKeyWrites(r.Kb);
		r.Now = 1234;
		r.Svc.AnimateNow();
		r.Settle();
		Assert.AreEqual(writes, PerKeyWrites(r.Kb), "no frames while the light is off");
		r.Svc.DisplayChanged(on: true);
		r.Settle();
		Assert.AreNotEqual(new Rgb(0x12, 0x34, 0x56), KeysColor(r.Kb), "animated again");
	}

	[TestMethod]
	public void SlotsPutAnyProfileOnTheMKeys()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles.Add(new Profile
			{
				Name = "Game",
				Keyboard = new Lighting { Color = "#ABCDEF" }
			});
			c.Profiles[0].Apps = "editor.exe";
			c.Slots = new int[3] { 3, 1, 2 };
			c.Active = 3;
		});
		Assert.AreEqual(3, r.Svc.Current);
		Assert.AreEqual(1, r.Kb.MLeds, "profile 4 sits on M1");
		Assert.AreEqual(new Rgb(0xAB, 0xCD, 0xEF), KeysColor(r.Kb));
		r.Kb.PressM(2);
		r.Settle();
		Assert.AreEqual(1, r.Svc.Current);
		r.Svc.Foreground("editor.exe");
		r.Settle();
		Assert.AreEqual(0, r.Svc.Current, "profile only for a program");
		Assert.AreEqual(0, r.Kb.MLeds, "no M lamp for it");
		r.Svc.SyncOnboardNow();
		OnboardSector s1 = new OnboardSector(r.Kb.Sectors[1]);
		Assert.AreEqual("static #ABCDEF", OnboardSector.DescribeLed(s1.Led(OnboardSector.LedKeys)), "memory slot 1 = profile on M1");
	}

	[TestMethod]
	public void M1PressThatTouchesM2StaysOnM1()
	{
		using Rig r = new Rig(c => c.Active = 2);
		r.Kb.PressM1TouchingM2();
		r.Settle();
		Assert.AreEqual(0, r.Svc.Current);
		Assert.AreEqual(1, r.Kb.MLeds);
	}

	[TestMethod]
	public void EffectListsComeFromTheirZones()
	{
		using Rig r = new Rig();
		CollectionAssert.AreEqual(new[] { "off", "static", "breathing", "cycle", "wave" }, r.Svc.Effects(0).ToArray(), "keys");
		CollectionAssert.AreEqual(new[] { "off", "static", "breathing", "cycle" }, r.Svc.Effects(1).ToArray(), "logo");
	}

	[TestMethod]
	public void KeyListFollowsTheLayout()
	{
		// Пользователь, 02.11.2026 (фото русской G915): «\» слева от большого Enter оставалась синей на всех профилях.
		using (Rig r = new Rig())
		{
			Assert.IsTrue(r.Svc.KeyIds.Contains(KeyLayout.IsoHash), "Russian: backslash left of the two-row Enter");
			Assert.IsFalse(r.Svc.KeyIds.Contains(KeyLayout.AnsiBackslash));
			Assert.IsFalse(r.Svc.KeyIds.Contains(KeyLayout.IsoExtra), "long left Shift");
			Assert.IsTrue(r.Svc.KeyIds.Contains((byte)180), "G1");
			Assert.IsFalse(r.Svc.KeyIds.Contains((byte)0), "number 0 is accepted by the keyboard but is not a key");
			Assert.IsTrue(r.Kb.Frame.ContainsKey(KeyLayout.IsoHash), "painted");
			KeyDef[] editor = KeyLayout.For(r.Svc.KeyIds.ToArray());
			Assert.AreEqual(2f, editor.Single((KeyDef k) => k.Id == 37).H, "Enter in two rows");
			Assert.AreEqual("\\", editor.Single((KeyDef k) => k.Id == KeyLayout.IsoHash).Label);
		}
		using (Rig r = new Rig(null, kb => kb.Layout = 1))
		{
			Assert.IsTrue(r.Svc.KeyIds.Contains(KeyLayout.AnsiBackslash), "US layout is ANSI");
			Assert.IsFalse(r.Svc.KeyIds.Contains(KeyLayout.IsoHash));
		}
		using (Rig r = new Rig(null, kb => kb.Layout = 4))
		{
			Assert.IsTrue(r.Svc.KeyIds.Contains(KeyLayout.IsoExtra), "German layout is ISO");
			Assert.IsTrue(r.Svc.KeyIds.Contains(KeyLayout.IsoHash));
			Assert.IsFalse(r.Svc.KeyIds.Contains(KeyLayout.AnsiBackslash));
		}
	}

	[TestMethod]
	public void BrightnessKeySetsTheProfileBrightness()
	{
		using Rig r = new Rig(c => c.Profiles[0].Brightness = 25);
		int calls = r.Kb.Calls.Count(x => x.Feature == 0x8040 && x.Fn == 2);
		r.Kb.PressBrightness(12);
		r.Settle();
		Assert.AreEqual(12, r.Svc.Config.Profiles[0].Brightness);
		Assert.AreEqual(12, LogiConfig.Load(r.ConfigPath).Profiles[0].Brightness, "saved");
		Assert.AreEqual(calls, r.Kb.Calls.Count(x => x.Feature == 0x8040 && x.Fn == 2), "the keyboard already has it");
		r.Svc.DisplayChanged(on: false);
		r.Settle();
		r.Svc.DisplayChanged(on: true);
		r.Settle();
		Assert.AreEqual(12, r.Kb.Brightness);
	}

	// Пользователь, 02.11.2026: клавиатуру и мышь можно выключить целиком — «не всем эти опции нужны».
	[TestMethod]
	public void KeyboardOffReturnsItToItsMemoryAndHidesIt()
	{
		using Rig r = new Rig();
		Assert.AreEqual(2, r.Kb.Mode);
		Assert.IsFalse(float.IsNaN(r.Svc.KeyboardBattery));
		LogiConfig c = LogiConfig.Load(r.ConfigPath);
		c.KeyboardOn = false;
		c.Save(r.ConfigPath);
		r.Svc.ReloadConfig();
		r.Settle();
		Assert.AreEqual(1, r.Kb.Mode, "keyboard memory");
		Assert.AreEqual("disabled", r.Svc.Mode);
		Assert.IsTrue(float.IsNaN(r.Svc.KeyboardBattery), "no charge on the bar");
		Assert.AreEqual("", r.Svc.KeyboardId);
		Assert.IsFalse(r.Svc.Found.Any((FoundDevice d) => d.Keyboard));
		c.KeyboardOn = true;
		c.Save(r.ConfigPath);
		r.Svc.ReloadConfig();
		r.Settle();
		Assert.AreEqual(2, r.Kb.Mode, "PulseBar mode again");
		Assert.AreEqual("host", r.Svc.Mode);
	}

	[TestMethod]
	public void KeyboardAndMouseOffOpenNothing()
	{
		using Rig r = new Rig(c =>
		{
			c.KeyboardOn = false;
			c.MouseOn = false;
		});
		r.Mice.Add(new PointingDevice { Name = "Some Mouse", Vid = 0x1234, Pid = 1 });
		r.Svc.Detect();
		r.Settle();
		Assert.AreEqual(0, r.Opens, "no HID++ channel opened");
		Assert.AreEqual(0, r.Svc.Found.Count);
		Assert.AreEqual(1, r.Kb.Mode, "keyboard untouched");
		Assert.AreEqual("", r.Svc.CurrentMouse.Name);
	}

	// «Определить»: все клавиатуры и мыши с тем, что PulseBar с ними умеет; выбор мыши для строки.
	[TestMethod]
	public void DetectListsDevicesAndTheChosenMouseIsShown()
	{
		using Rig r = new Rig();
		r.Mice.Add(new PointingDevice { Name = "Razer Naga V3 Pro", Vid = 0x1532, Pid = 0x00E8, RazerPath = "no-such-device", BatteryPercent = 82 });
		r.Mice.Add(new PointingDevice { Name = "USB Optical Mouse", Vid = 0x1234, Pid = 0x5678 });
		r.Keyboards.Add(new PointingDevice { Name = "USB Keyboard", Vid = 0x258A, Pid = 0x002A, Keyboard = true });
		int scans = r.Svc.Scans;
		r.Svc.Detect();
		r.Settle();
		Assert.AreNotEqual(scans, r.Svc.Scans);
		IReadOnlyList<FoundDevice> found = r.Svc.Found;
		FoundDevice g915 = found.Single((FoundDevice d) => d.Brand == "Logitech");
		Assert.IsTrue(g915.Keyboard);
		Assert.AreEqual("full", g915.Support);
		Assert.IsTrue(g915.Has("lighting") && g915.Has("brightness") && g915.Has("gkeys") && g915.Has("memory") && g915.Has("battery"), g915.Features);
		Assert.AreEqual("Logitech G915", g915.Title);
		Assert.AreEqual(g915.Id, r.Svc.KeyboardId);
		FoundDevice naga = found.Single((FoundDevice d) => d.Id == "hid:1532:00E8");
		Assert.AreEqual("battery", naga.Support);
		Assert.AreEqual("Razer Naga V3 Pro", naga.Title);
		Assert.AreEqual("windows", found.Single((FoundDevice d) => d.Id == "hid:258A:002A").Support);
		Assert.AreEqual("hid:1532:00E8", r.Svc.MouseId, "the mouse with a charge");
		LogiConfig c = LogiConfig.Load(r.ConfigPath);
		c.MouseDevice = "hid:1234:5678";
		c.Save(r.ConfigPath);
		r.Svc.ReloadConfig();
		r.Settle();
		Assert.AreEqual("hid:1234:5678", r.Svc.MouseId);
		Assert.AreEqual("USB Optical Mouse", r.Svc.CurrentMouse.Name);
		Assert.IsTrue(float.IsNaN(r.Svc.CurrentMouse.Battery), "no charge, but the chosen one");
		Assert.AreEqual(2, r.Kb.Mode, "the keyboard stays in PulseBar mode");
	}

	// Клавиатура уснула до «Определить»: вид, имя и функции — из devices.json, разделы окна не пропадают.
	[TestMethod]
	public void AsleepKeyboardIsKnownFromBefore()
	{
		using Rig r = new Rig();
		Assert.IsTrue(File.Exists(Path.Combine(r.Dir, "devices.json")));
		r.Kb.Sleep();
		r.Settle();
		r.Svc.Detect();
		r.Settle();
		FoundDevice kb = r.Svc.Found.Single();
		Assert.IsFalse(kb.Online);
		Assert.IsTrue(kb.Keyboard);
		Assert.AreEqual(FakeG915.Name, kb.Name);
		Assert.AreEqual("full", kb.Support);
		Assert.AreEqual(kb.Id, r.Svc.KeyboardId);
		r.Kb.Wake();
		r.Settle();
		Assert.AreEqual(2, r.Kb.Mode, "set up when it wakes");
	}

	// Выбрана клавиатура другой фирмы: G915 остаётся в своей памяти.
	[TestMethod]
	public void OtherKeyboardChosenLeavesTheLogitechOneAlone()
	{
		using Rig r = new Rig(c => c.KeyboardDevice = "hid:258A:002A");
		r.Keyboards.Add(new PointingDevice { Name = "USB Keyboard", Vid = 0x258A, Pid = 0x002A, Keyboard = true });
		r.Svc.Detect();
		r.Settle();
		Assert.AreEqual(1, r.Kb.Mode);
		Assert.AreEqual("hid:258A:002A", r.Svc.KeyboardId);
		Assert.AreEqual("none", r.Svc.Mode);
	}

	[TestMethod]
	public void BrightnessKeyTurnsOffFromTwelvePercent()
	{
		using Rig r = new Rig(c => c.Profiles[0].Brightness = 12);
		r.Kb.PressBrightness(0);
		r.Settle();
		Assert.AreEqual(0, LogiConfig.Load(r.ConfigPath).Profiles[0].Brightness, "12 -> 0 is the button's own step");
		r.Kb.PressBrightness(100);
		r.Settle();
		Assert.AreEqual(100, LogiConfig.Load(r.ConfigPath).Profiles[0].Brightness);
	}

	// Пользователь, 01.11.2026: клавиатура погасла сама, и зажечь её можно было только кнопкой яркости — 0 % попадал в
	// профиль. Теперь профиль не меняется, а подсветку зажигает любой ввод.
	[TestMethod]
	public void KeyboardThatTurnedItselfOffWakesOnAnyKey()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Brightness = 25;
			c.Profiles[0].Keyboard.Color = "#102030";
		});
		Assert.AreEqual(25, r.Kb.Brightness);
		r.Kb.TurnOffItself();
		r.Settle();
		Assert.AreEqual(25, r.Svc.Config.Profiles[0].Brightness, "not a press: 25 -> 0 is not the button's step");
		Assert.AreEqual(25, LogiConfig.Load(r.ConfigPath).Profiles[0].Brightness);
		r.Svc.TickNow();
		Assert.AreEqual(0, r.Kb.Brightness, "stays off without input");
		r.IdleMs = 0;
		r.Svc.TickNow();
		r.Settle();
		Assert.AreEqual(25, r.Kb.Brightness, "a key turns it on");
		Assert.AreEqual(new Rgb(0x10, 0x20, 0x30), KeysColor(r.Kb), "frame painted again");
	}

	[TestMethod]
	public void DimAndOffAfterIdleDoNotChangeTheProfile()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Brightness = 80;
			c.IdleSeconds = 30;
			c.IdleBrightness = 20;
			c.OffMinutes = 10;
		});
		r.IdleMs = 0;
		r.Svc.TickNow();
		Assert.AreEqual(80, r.Kb.Brightness);
		r.IdleMs = 31000;
		r.Svc.TickNow();
		r.Settle();
		Assert.AreEqual(20, r.Kb.Brightness, "dimmed");
		r.IdleMs = 601000;
		r.Svc.TickNow();
		r.Settle();
		Assert.AreEqual(0, r.Kb.Brightness, "off");
		Assert.AreEqual(80, LogiConfig.Load(r.ConfigPath).Profiles[0].Brightness, "own writes are not presses");
		r.Kb.Frame.Clear();
		r.IdleMs = 0;
		r.Svc.TickNow();
		r.Settle();
		Assert.AreEqual(80, r.Kb.Brightness, "back on a key");
		Assert.IsTrue(r.Kb.Frame.ContainsKey(1), "frame painted again after off");
	}

	[TestMethod]
	public void ForeignPaintIsPaintedBackFiveTimesAMinute()
	{
		using Rig r = new Rig(c =>
		{
			c.Profiles[0].Keyboard.Effect = "static";
			c.Profiles[0].Keyboard.Color = "#102030";
		});
		r.DynamicLighting = true;
		Rgb blue = new Rgb(0x00, 0x78, 0xD4);
		for (int i = 0; i < 5; i++)
		{
			r.Kb.ForeignPaint(blue);
			Thread.Sleep(700);
			r.Settle();
			Assert.AreEqual(new Rgb(0x10, 0x20, 0x30), r.Kb.Frame[0x01], $"painted back ({i + 1})");
		}
		Assert.AreEqual("lamparray", r.Svc.ForeignLighting);
		r.Kb.ForeignPaint(blue);
		Thread.Sleep(700);
		r.Settle();
		Assert.AreEqual(blue, r.Kb.Frame[0x01], "the sixth time in a minute PulseBar gives up");
	}
}

[TestClass]
public class ConfigTests
{
	[TestMethod]
	public void Version2BecomesVersion3()
	{
		string dir = Path.Combine(Path.GetTempPath(), "PulseBarTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, "logitech.json");
		File.WriteAllText(path, """
			{"version":2,"enabled":true,"active":1,"profiles":[
			 {"name":"A","perKey":true,"keyboard":{"effect":"cycle","color":"#112233"},"keyColors":{"17":"#FF0000"}},
			 {"name":"B","perKey":false,"keyboard":{"effect":"static","color":"#445566"},"keyColors":{"01":"#00FF00"}},
			 {"name":"C","perKey":false,"keyboard":{"effect":"wave","color":"#778899"},"keyColors":{"02":"#0000FF"}}]}
			""");
		LogiConfig c = LogiConfig.Load(path);
		Assert.AreEqual(3, c.Version);
		CollectionAssert.AreEqual(new[] { 0, 1, 2 }, c.Slots);
		Assert.AreEqual("static", c.Profiles[0].Keyboard.Effect, "by key -> static");
		Assert.AreEqual("#FF0000", c.Profiles[0].KeyColors["17"]);
		Assert.AreEqual(0, c.Profiles[1].KeyColors.Count, "hidden key colours of a plain colour profile do not come back");
		Assert.AreEqual("wave", c.Profiles[2].Keyboard.Effect);
		Assert.AreEqual(1, c.Profiles[2].KeyColors.Count, "kept for when it becomes static");
		Assert.IsTrue(File.Exists(Path.Combine(dir, "logitech.v2.json")));
		Directory.Delete(dir, recursive: true);
	}

	[TestMethod]
	public void OneIdleThresholdBecomesDimAndOff()
	{
		string dir = Path.Combine(Path.GetTempPath(), "PulseBarTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, "logitech.json");
		File.WriteAllText(path, "{\"version\":3,\"idleMinutes\":5,\"idleBrightness\":10,\"profiles\":[{\"name\":\"A\",\"brightness\":5}]}");
		LogiConfig c = LogiConfig.Load(path);
		Assert.AreEqual(30, c.IdleSeconds);
		Assert.AreEqual(10, c.OffMinutes);
		Assert.AreEqual(0, c.IdleBrightness, "10 % is dark on the G915 — off");
		Assert.AreEqual(LogiConfig.MinBrightness, c.Profiles[0].Brightness, "lit from 11 %");
		StringAssert.DoesNotMatch(File.ReadAllText(path), new System.Text.RegularExpressions.Regex("idleMinutes"));
		Directory.Delete(dir, recursive: true);
	}

	[TestMethod]
	public void RemovingAProfileFixesSlots()
	{
		LogiConfig c = new LogiConfig();
		c.Normalize();
		c.Profiles.Add(new Profile { Name = "D" });
		c.Slots = new int[3] { 3, 1, 2 };
		c.Active = 2;
		c.RemoveProfile(1);
		CollectionAssert.AreEqual(new[] { 2, 0, 1 }, c.Slots);
		Assert.AreEqual(1, c.Active);
	}
}

// Журнал настоящей G915 (logi-diag 31.10.2026): нажатия в режиме PulseBar, прогнанные через разбор нажатий.
[TestClass]
public class DiagReplayTests
{
	[TestMethod]
	public void RecordedPressesGiveOneEventEach()
	{
		// Индексы функций G915: 0x11 — GKeys, 0x12 — MKeys, 0x13 — MacroRecord, 0x14 — яркость.
		Dictionary<byte, ushort> features = new Dictionary<byte, ushort>
		{
			[0x11] = LogiDevice.GKeys,
			[0x12] = LogiDevice.MKeys,
			[0x13] = LogiDevice.MacroRecord
		};
		KeyEdges e = new KeyEdges();
		List<string> pressed = new List<string>();
		bool host = false;
		foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "g915-diag-20261031.txt")))
		{
			if (line.StartsWith("== host setup", StringComparison.Ordinal))
			{
				host = true;
			}
			string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (!host || w.Length != 3 || w[1] != "IN")
			{
				continue;
			}
			byte[] r = Convert.FromHexString(w[2]);
			if (r[0] == 0x11 && r[1] == 1 && (r[3] & 0x0F) == 0 && features.TryGetValue(r[2], out ushort f))
			{
				pressed.AddRange(e.Feed(f, (byte)(r[3] >> 4), r[4..]));
			}
		}
		// Шаг M1: M1 нажата дважды с задетой M2 (03 02 03 02) — только M1; дальше по шагам.
		CollectionAssert.AreEqual(new[] { "M1", "M1", "M2", "M3", "M1", "MR", "G1", "G2", "G3", "G4", "G5", "G1" }, pressed);
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

	[TestMethod]
	public void SeveralMKeysInOneMessageCountAsTheLeftOne()
	{
		KeyEdges e = new KeyEdges();
		CollectionAssert.AreEqual(new[] { "M1" }, e.Feed(LogiDevice.MKeys, 0, new byte[2] { 3, 0 }));
		Assert.AreEqual(0, e.Feed(LogiDevice.MKeys, 0, new byte[2] { 2, 0 }).Count, "M1 up, M2 still down");
		CollectionAssert.AreEqual(new[] { "M1" }, e.Feed(LogiDevice.MKeys, 0, new byte[2] { 3, 0 }));
		Assert.AreEqual(0, e.Feed(LogiDevice.MKeys, 0, new byte[2]).Count);
		CollectionAssert.AreEqual(new[] { "M2" }, e.Feed(LogiDevice.MKeys, 0, new byte[2] { 2, 0 }));
	}
}
