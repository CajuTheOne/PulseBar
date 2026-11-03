using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PulseBar.Tests;

// Свои пресеты вида (2.4.0-beta.2): снимок вида и загрузка обратно, остальные настройки не трогаются.
[TestClass]
public class SettingsTests
{
	[TestMethod]
	public void LookPresetRestoresTheLookOnly()
	{
		Settings s = new Settings
		{
			Layout = 1,
			ClaudeStyle = 2,
			GptStyle = 2,
			GptBarLength = 120,
			NormalColor = "#112233",
			ShowGpu = false,
			Language = "ru",
			CpuLoad = new Limit(50, 80)
		};
		s.Groups = new List<Settings.Group>
		{
			new Settings.Group { X = 40, Items = new List<string> { "chatgpt", "cpu" } }
		};
		string look = s.AppearanceJson();
		StringAssert.DoesNotMatch(look, new System.Text.RegularExpressions.Regex("language|cpuLoad|deepSeekKey"), "only the look");
		Settings t = new Settings();
		t.ApplyAppearance(look);
		Assert.AreEqual(1, t.Layout);
		Assert.AreEqual(2, t.ClaudeStyle);
		Assert.AreEqual(2, t.GptStyle);
		Assert.AreEqual(120, t.GptBarLength);
		Assert.AreEqual("#112233", t.NormalColor);
		Assert.IsFalse(t.ShowGpu);
		Assert.AreEqual(40, t.LayoutGroups[0].X);
		CollectionAssert.Contains(t.LayoutGroups[0].Items, "chatgpt");
		Assert.AreEqual("en", t.Language, "language is not part of the look");
		Assert.AreEqual(Settings.Defaults.CpuLoad, t.CpuLoad, "thresholds are not part of the look");
	}

	[TestMethod]
	public void ChatGptTileKeepsItsStyle()
	{
		Settings s = new Settings();
		s.ShowChatGpt = true;
		Assert.AreEqual(1, s.GptStyle, "turning the tile on gives the compact style");
		s.GptStyle = 2;
		s.ShowChatGpt = true;
		Assert.AreEqual(2, s.GptStyle, "an on tile keeps the large style");
		s.ShowChatGpt = false;
		Assert.AreEqual(0, s.GptStyle);
	}

	// Пользователь, 03.11.2026: после «Одной строки» (разделители) свой вид без разделителей должен вернуть и расположение.
	[TestMethod]
	public void LookPresetBringsBackItsLayoutWithoutSeparators()
	{
		Settings mine = new Settings { Layout = 1 };
		mine.Groups = new List<Settings.Group>
		{
			new Settings.Group { X = 38, Items = new List<string> { "chatgpt", "claude", "cpu", "gpu", "vram", "ram", "battery" } }
		};
		string look = mine.AppearanceJson();
		Settings s = new Settings { Layout = 0 };
		s.ResetLayout();
		Assert.IsTrue(s.LayoutGroups[0].Items.Exists((string i) => i.StartsWith("sep")), "one line has separators");
		s.ApplyAppearance(look);
		Assert.AreEqual(1, s.Layout);
		Assert.AreEqual(1, s.LayoutGroups.Count);
		Assert.AreEqual(38, s.LayoutGroups[0].X);
		Assert.IsFalse(s.LayoutGroups[0].Items.Exists((string i) => i.StartsWith("sep")), "no separators");
		Assert.AreEqual("chatgpt,claude,cpu,gpu,vram,ram,battery", string.Join(",", s.LayoutGroups[0].Items.FindAll((string i) => i != "grok" && i != "deepseek")), "order kept; blocks the look did not have are added hidden");
	}

	// Пользователь, 03.11.2026: пары в блоке заряда — наушники + мышь, наушники + клавиатура и т. д.
	[TestMethod]
	public void BatteryOrderPairsAndSkipsMissingDevices()
	{
		Settings s = new Settings { BatteryOrder = "headset,mouse,keyboard" };
		CollectionAssert.AreEqual(new[] { 2, 1, 0 }, BarRenderer.BatteryItems(s.BatteryOrderList(), 7));
		CollectionAssert.AreEqual(new[] { 1, 0 }, BarRenderer.BatteryItems(s.BatteryOrderList(), 3), "headset not connected: mouse and keyboard pair up");
		CollectionAssert.AreEqual(new[] { 2, 0 }, BarRenderer.BatteryItems(s.BatteryOrderList(), 5), "mouse off");
		CollectionAssert.AreEqual(new[] { "mouse", "keyboard", "headset" }, Settings.NormalizeBatteryOrder("mouse, nonsense, mouse,keyboard"));
		Settings t = new Settings();
		t.ApplyAppearance(s.AppearanceJson());
		Assert.AreEqual("headset,mouse,keyboard", t.BatteryOrder, "part of the look");
	}

	// Пользователь, 02.11.2026: выключенные и снова включённые плитки ИИ в «Виде» включались маленькими.
	[TestMethod]
	public void AiTilesTurnBackOnInTheirStyle()
	{
		Settings s = new Settings { ClaudeStyle = 2, GptStyle = 2 };
		s.SetShown("claude", false);
		s.SetShown("chatgpt", false);
		Assert.AreEqual(0, s.ClaudeStyle);
		Assert.AreEqual(0, s.GptStyle);
		Settings t = new Settings();
		t.ApplyAppearance(s.AppearanceJson());
		Assert.IsFalse(t.IsShown("claude"));
		t.SetShown("claude", true);
		t.SetShown("chatgpt", true);
		Assert.AreEqual(2, t.ClaudeStyle, "large again, also after saving");
		Assert.AreEqual(2, t.GptStyle);
	}
}
