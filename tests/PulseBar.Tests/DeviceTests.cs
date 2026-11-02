using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PulseBar.Ai;
using PulseBar.Logi;

namespace PulseBar.Tests;

// 2.4.0-beta.8: названия вкладок «Клавиатура» и «Мышь» по устройству; ключ записи во входе приложения Claude.
[TestClass]
public class DeviceTests
{
	[TestMethod]
	public void TabTitleIsBrandAndShortModel()
	{
		Assert.AreEqual("Logitech G915", Title("G915 WIRELESS RGB MECHANICAL GAMING KEYBOARD", "Logitech"));
		Assert.AreEqual("Logitech G915 TKL", Title("G915 TKL LIGHTSPEED WIRELESS RGB MECHANICAL GAMING KEYBOARD", "Logitech"));
		Assert.AreEqual("Logitech MX Master 3S", Title("MX Master 3S", "Logitech"));
		Assert.AreEqual("Razer Naga V3 Pro", Title("Razer Naga V3 Pro", "Razer"));
		Assert.AreEqual("USB Keyboard", Title("USB Keyboard", ""), "unknown brand: the name as it is");
		Assert.AreEqual("Razer", FoundDevice.BrandOf(0x1532));
		Assert.AreEqual("", FoundDevice.BrandOf(0xABCD));
	}

	private static string Title(string name, string brand)
	{
		return new FoundDevice("id", Keyboard: true, name, brand, Online: true, -1, Charging: false, "full", "").Title;
	}

	// Ключ: "acct:<аккаунт>|<клиент>:<организация>:<адрес API>:<права>" — нужен токен с user:profile.
	[TestMethod]
	public void ClaudeAppTokenKeyIsParsed()
	{
		using JsonDocument v = JsonDocument.Parse("{\"token\":\"t1\",\"expiresAt\":1795000000000}");
		ClaudeAppAuth.Token? t = ClaudeAppAuth.Parse("acct:1af0|9d1c:a231:https://api.anthropic.com:user:inference user:file_upload user:profile", v.RootElement);
		Assert.IsNotNull(t);
		Assert.AreEqual("1af0", t.Account);
		Assert.AreEqual("a231", t.Org);
		Assert.AreEqual("t1", t.Value);
		Assert.AreEqual(1795000000000L, t.Expires.ToUnixTimeMilliseconds());
		Assert.IsNull(ClaudeAppAuth.Parse("acct:1af0|9d1c:a231:https://api.anthropic.com:user:inference", v.RootElement), "no user:profile");
		Assert.IsNull(ClaudeAppAuth.Parse("acct:1af0|9d1c:a231:https://example.com:user:profile", v.RootElement), "another audience");
		Assert.IsNull(ClaudeAppAuth.Parse("other", v.RootElement));
	}
}
