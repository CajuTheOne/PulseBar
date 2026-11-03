using System;
using System.Collections.Generic;
using System.Linq;

namespace PulseBar.Logi;

// Найденная клавиатура, мышь или наушники — для вкладок устройств (кнопка «Определить», 2.4.0-beta.8; наушники —
// beta.10). Kind: keyboard, mouse, headset. Id: Logitech — "logi:<PID канала>:<номер на приёмнике>" (не меняется,
// пока устройство не сопрягут заново), другие — "hid:<VID>:<PID>", наушники Bluetooth — "bt:<адрес>".
// Support: full — PulseBar ведёт устройство (клавиатура Logitech с профилями в памяти), battery — только заряд, windows — только настройки Windows, nobattery — наушники, которые не сообщают заряд,
// "" — спит, умения неизвестны.
// Features — функции HID++, которые устройство заявило: lighting, effects, brightness, gkeys, mkeys, macro, memory,
// battery ("" — неизвестно: устройство ещё ни разу не было на связи).
internal sealed record FoundDevice(string Id, string Kind, string Name, string Brand, bool Online, int Battery, bool Charging, string Support, string Features)
{
	public bool Keyboard => Kind == "keyboard";

	// Марка по VID (USB-IF): чего нет в списке — марка пустая, показывается имя устройства.
	private static readonly Dictionary<ushort, string> Brands = new Dictionary<ushort, string>
	{
		[0x046D] = "Logitech", [0x1532] = "Razer", [0x1038] = "SteelSeries", [0x1B1C] = "Corsair", [0x3434] = "Keychron",
		[0x0B05] = "ASUS", [0x045E] = "Microsoft", [0x1E7D] = "ROCCAT", [0x0951] = "HyperX", [0x03F0] = "HP",
		[0x2516] = "Cooler Master", [0x258A] = "", [0x04F2] = "Chicony", [0x413C] = "Dell", [0x17EF] = "Lenovo",
		[0x05AC] = "Apple", [0x3297] = "ZSA", [0x320F] = "Glorious", [0x28DA] = "Endgame Gear", [0x093A] = "Pixart",
		[0x1A2C] = "", [0x248A] = "", [0x0C45] = "", [0x04D9] = ""
	};

	public static string BrandOf(ushort vid)
	{
		return Brands.TryGetValue(vid, out string? b) ? b : "";
	}

	// Слова, которые есть почти в каждом имени: «G915 WIRELESS RGB MECHANICAL GAMING KEYBOARD» → «G915».
	private static readonly HashSet<string> Filler = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"wireless", "rgb", "mechanical", "gaming", "keyboard", "mouse", "lightspeed", "usb", "receiver"
	};

	// Название вкладки: марка и модель коротко — «Logitech G915», «Razer Naga V3 Pro».
	public string Title
	{
		get
		{
			string model = string.Join(" ", Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where((string w) => !Filler.Contains(w)));
			if (model.Length == 0 || Brand.Length == 0)
			{
				model = Name;
			}
			if (Brand.Length == 0 || model.StartsWith(Brand, StringComparison.OrdinalIgnoreCase))
			{
				return model;
			}
			return Brand + " " + model;
		}
	}

	public bool Has(string feature)
	{
		return Features.Split(',').Contains(feature);
	}

	// Функции известны (устройство было на связи хотя бы раз).
	public bool FeaturesKnown => Features.Length > 0;
}

// Устройство Logitech, бывшее на связи (devices.json): спящее при запуске устройство приёмник знает только по короткому
// имени, а вид и функции — отсюда.
internal sealed class KnownDevice
{
	public string Name { get; set; } = "";

	public bool Keyboard { get; set; }

	public string Features { get; set; } = "";
}
