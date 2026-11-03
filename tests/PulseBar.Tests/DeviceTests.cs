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
		return new FoundDevice("id", "keyboard", name, brand, Online: true, -1, Charging: false, "full", "").Title;
	}

	// Узлы Bluetooth одного устройства собираются по адресу из идентификатора узла.
	[TestMethod]
	public void BluetoothAddressFromInstanceId()
	{
		Assert.AreEqual("8099E74265A2", Headsets.Address("BTHENUM\\DEV_8099E74265A2\\A&69DB629&0&BLUETOOTHDEVICE_8099E74265A2"));
		Assert.AreEqual("8099E74265A2", Headsets.Address("BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0DF0\\A&69DB629&0&8099E74265A2_C00000000"));
		Assert.IsNull(Headsets.Address("BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\\7&1234ABCD&0&000000000000_00000000"), "enumerator node");
	}

	// Блок заряда: в одну строку — подряд, в две — по два в столбце.
	[TestMethod]
	public void BatteryBlockGrowsWithHeadset()
	{
		using BarRenderer r = new BarRenderer();
		Settings s = new Settings { Layout = 0 };
		r.Configure(1f, 48, s);
		r.Batteries = 3;
		float two = r.ElementWidth("battery");
		r.Batteries = 7;
		float three = r.ElementWidth("battery");
		r.Batteries = 4;
		float one = r.ElementWidth("battery");
		Assert.IsTrue(three > two && two > one && one > 0f);
		r.Batteries = 0;
		Assert.AreEqual(0f, r.ElementWidth("battery"));
		s.Layout = 1;
		r.Configure(1f, 48, s);
		r.Batteries = 3;
		float column = r.ElementWidth("battery");
		r.Batteries = 7;
		Assert.IsTrue(r.ElementWidth("battery") > column * 1.5f, "two columns");
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
