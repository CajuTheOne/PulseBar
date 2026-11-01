using System;
using System.Collections.Generic;
using System.Linq;
using PulseBar.Logi;

namespace PulseBar.Tests;

// Модель G915 на приёмнике Lightspeed (слот 1) для тестов: отвечает на запросы HID++ так же, как настоящая клавиатура
// (таблица функций, память профилей из копии, подсветка, яркость, лампочки M), и ведёт себя как прошивка при нажатиях:
// во встроенном режиме M переключает профиль сама, в режиме PulseBar — только сообщает о нажатии. Форматы ответов и
// уведомлений — из журнала настоящей G915 (logi-diag 31.10.2026, Data\g915-diag-20261031.txt).
internal sealed class FakeG915 : IHidppPort
{
	public const byte Slot = 1;

	public const string Name = "G915 WIRELESS RGB MECHANICAL GAMING KEYBOARD";

	// Индексы функций — как у настоящей G915 (logi-probe 31.10.2026).
	private static readonly ushort[] FeatureIds = new ushort[22]
	{
		0x0001, 0x0003, 0x0005, 0x1D4B, 0x0020, 0x0007, 0x1001, 0x1814, 0x1815, 0x8071, 0x8081,
		0x1B04, 0x1BC0, 0x4100, 0x4522, 0x4540, 0x8010, 0x8020, 0x8030, 0x8040, 0x8100, 0x8060
	};

	private static readonly byte[] Description = Convert.FromHexString("0104010303081000FF3604");

	// Зоны 0x8071: 0 — логотип (место 2), 1 — клавиши (место 1); эффекты в порядке таблицы настоящей клавиатуры.
	public const int LogoZone = 0;

	public const int KeysZone = 1;

	private static readonly int[][] ClusterEffects = new int[2][]
	{
		new int[4] { 0x00, 0x01, 0x03, 0x0A },
		new int[7] { 0x00, 0x01, 0x0A, 0x03, 0x04, 0x0B, 0x0C }
	};

	private static readonly int[] ClusterPlaces = new int[2] { 2, 1 };

	private readonly object _lock = new object();

	private byte[]? _write;

	private int _writeSector;

	private int _writeOffset;

	public readonly Dictionary<int, byte[]> Sectors;

	// Номера, которые принимает 0x8081: все 0–111, мультимедиа, G1–G5, логотип (карта fn0 настоящей G915).
	public readonly HashSet<byte> Accepted;

	// Раскладка 0x4540: 7 — русская (ANSI).
	public int Layout = 7;

	// Что клавиатура получила (для проверок).
	public readonly List<(ushort Feature, byte Fn, byte[] Args)> Calls = new List<(ushort, byte, byte[])>();

	public readonly List<int> ModeChanges = new List<int>();

	public readonly List<int> SectorWrites = new List<int>();

	public int Mode = 1;

	public int Profile = 1;

	public bool Diverted;

	public int MLeds = -1;

	public int Brightness = 100;

	// Запись яркости (fn2): настоящая G915 сначала сообщает новую яркость (fn0), потом отвечает (журнал 31.10.2026).
	private int _echo = -1;

	// Программное управление подсветкой 0x8071 fn5: после режима PulseBar у настоящей G915 — 02 06 (эффекты отклоняются).
	public byte SwMode = 2;

	public byte SwFlags = 6;

	public int BatteryMv = 3900;

	public bool Online = true;

	public readonly Dictionary<int, (int Effect, byte[] Params)> Effects = new Dictionary<int, (int, byte[])>();

	public Dictionary<byte, Rgb> Frame = new Dictionary<byte, Rgb>();

	private readonly Dictionary<byte, Rgb> _pending = new Dictionary<byte, Rgb>();

	public bool HasShort => true;

	public event Action<byte[]>? Received;

	public event Action? Closed;

	public FakeG915(Dictionary<int, byte[]> sectors)
	{
		Sectors = sectors.ToDictionary(x => x.Key, x => (byte[])x.Value.Clone());
		Accepted = new HashSet<byte>(Enumerable.Range(0, 112).Select(i => (byte)i));
		Accepted.UnionWith(new byte[11] { 153, 155, 156, 157, 158, 180, 181, 182, 183, 184, KeyLayout.Logo });
	}

	public void Start()
	{
	}

	public void Dispose()
	{
	}

	public void Die()
	{
		Closed?.Invoke();
	}

	// ---------- действия пользователя ----------

	// Во встроенном режиме событие 0x8100 не приходит: клавиатура сообщает только о смене эффекта (0x8071 fn1 [01]).
	public void PressM(int n)
	{
		if (Mode == 1)
		{
			lock (_lock)
			{
				Profile = n;
			}
			Notify(0x8071, 1, new byte[2] { 1, 0 });
			return;
		}
		Notify(0x8020, 0, new byte[2] { (byte)(1 << (n - 1)), 0 });
		Notify(0x8020, 0, new byte[2]);
	}

	// Нажатие M1 с задетой M2, как в журнале: 03 → 02 → 03 → 02 → 00.
	public void PressM1TouchingM2()
	{
		foreach (byte b in new byte[5] { 3, 2, 3, 2, 0 })
		{
			Notify(0x8020, 0, new byte[2] { b, 0 });
		}
	}

	// Кнопка яркости: прошивка меняет яркость сама и сообщает 0x8040 fn0 [яркость BE16].
	public void PressBrightness(int value)
	{
		lock (_lock)
		{
			Brightness = value;
		}
		Notify(0x8040, 0, new byte[2] { (byte)(value >> 8), (byte)value });
	}

	// Клавиатура погасла сама (сон прошивки и т. п.): яркость 0 и то же событие, что от кнопки.
	public void TurnOffItself()
	{
		lock (_lock)
		{
			Brightness = 0;
			Frame.Clear();
		}
		Notify(0x8040, 0, new byte[2]);
	}

	// Служба Logitech LampArray («Динамическое освещение» Windows) красит все клавиши в один цвет; ответы на её
	// запросы (программный id 0xF) приходят и в канал PulseBar.
	public void ForeignPaint(Rgb c)
	{
		byte index = (byte)(Array.IndexOf(FeatureIds, (ushort)0x8081) + 1);
		lock (_lock)
		{
			foreach (byte id in Accepted)
			{
				if (id != KeyLayout.Logo)
				{
					Frame[id] = c;
				}
			}
		}
		byte[] r = new byte[20] { 0x11, Slot, index, 0x6F, c.R, c.G, c.B, 0x62, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		Received?.Invoke(r);
		Received?.Invoke(new byte[20] { 0x11, Slot, index, 0x7F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
	}

	public void PressG(int n, int repeats = 1)
	{
		if (Mode != 2 || !Diverted)
		{
			return;
		}
		int bits = 1 << (n - 1);
		for (int i = 0; i < repeats; i++)
		{
			Notify(0x8010, 0, new byte[2] { (byte)bits, (byte)(bits >> 8) });
		}
		Notify(0x8010, 0, new byte[2]);
	}

	public void PressMR()
	{
		if (Mode != 2)
		{
			return;
		}
		Notify(0x8030, 0, new byte[2] { 1, 0 });
		Notify(0x8030, 0, new byte[2]);
	}

	// Клавиатура уснула / проснулась: приёмник сообщает о связи; после пробуждения прошивка — во встроенном режиме.
	public void Sleep()
	{
		Online = false;
		Received?.Invoke(new byte[7] { 0x10, Slot, 0x41, 0x04, 0x61, 0x6D, 0x40 });
	}

	public void Wake()
	{
		lock (_lock)
		{
			Online = true;
			Mode = 1;
			Diverted = false;
			MLeds = -1;
			Frame.Clear();
			Effects.Clear();
		}
		Received?.Invoke(new byte[7] { 0x10, Slot, 0x41, 0x04, 0x21, 0x6D, 0x40 });
	}

	private void Notify(ushort feature, byte fn, byte[] data)
	{
		byte[] r = new byte[20];
		r[0] = 0x11;
		r[1] = Slot;
		r[2] = (byte)(Array.IndexOf(FeatureIds, feature) + 1);
		r[3] = (byte)(fn << 4);
		data.CopyTo(r, 4);
		Received?.Invoke(r);
	}

	// ---------- запросы ----------

	public void Write(byte[] q)
	{
		byte[]? reply;
		int echo;
		lock (_lock)
		{
			reply = (q[0] == 0x10) ? Register(q) : Feature(q);
			echo = _echo;
			_echo = -1;
		}
		if (echo >= 0)
		{
			Notify(0x8040, 0, new byte[2] { (byte)(echo >> 8), (byte)echo });
		}
		if (reply != null)
		{
			Received?.Invoke(reply);
		}
	}

	private static byte[] Error10(byte[] q, byte code)
	{
		return new byte[7] { 0x10, q[1], 0x8F, q[2], q[3], code, 0 };
	}

	private byte[]? Register(byte[] q)
	{
		if (q[1] != 0xFF)
		{
			return Error10(q, 1);
		}
		switch (q[2], q[3])
		{
		case (0x81, 0x00):
			return new byte[7] { 0x10, 0xFF, 0x81, 0x00, 0, 0, 0 };
		case (0x80, 0x00):
			return new byte[7] { 0x10, 0xFF, 0x80, 0x00, 0, 0, 0 };
		case (0x83, 0xB5):
			if (q[4] == 0x40)
			{
				byte[] r = new byte[20];
				r[0] = 0x11;
				r[1] = 0xFF;
				r[2] = 0x83;
				r[3] = 0xB5;
				r[4] = 0x40;
				r[5] = 14;
				System.Text.Encoding.ASCII.GetBytes("G915 KEYBOARD ").CopyTo(r, 6);
				return r;
			}
			return Error10(q, 3);
		default:
			return Error10(q, 1);
		}
	}

	private byte[]? Feature(byte[] q)
	{
		if (q[1] != Slot || !Online)
		{
			return null;
		}
		byte index = q[2];
		byte fn = (byte)(q[3] >> 4);
		byte[] a = q[4..];
		byte[] r = new byte[20];
		r[0] = 0x11;
		Array.Copy(q, 1, r, 1, 3);
		if (index == 0)
		{
			if (fn == 0)
			{
				ushort id = (ushort)((a[0] << 8) | a[1]);
				r[4] = (byte)(Array.IndexOf(FeatureIds, id) + 1);
			}
			else if (fn == 1)
			{
				r[4] = 4;
				r[5] = 2;
				r[6] = a[2];
			}
			return r;
		}
		if (index > FeatureIds.Length)
		{
			return Error20(q, 6);
		}
		ushort feature = FeatureIds[index - 1];
		Calls.Add((feature, fn, a.ToArray()));
		byte[]? data = Handle(feature, fn, a, out byte err);
		if (data == null)
		{
			return Error20(q, err);
		}
		data.AsSpan(0, Math.Min(16, data.Length)).CopyTo(r.AsSpan(4));
		return r;
	}

	private static byte[] Error20(byte[] q, byte code)
	{
		byte[] r = new byte[20];
		r[0] = 0x11;
		r[1] = q[1];
		r[2] = 0xFF;
		r[3] = q[2];
		r[4] = q[3];
		r[5] = code;
		return r;
	}

	private byte[]? Handle(ushort feature, byte fn, byte[] a, out byte err)
	{
		err = 2;
		switch (feature)
		{
		case 0x0001:
			if (fn == 0)
			{
				return new byte[1] { (byte)FeatureIds.Length };
			}
			if (a[0] >= 1 && a[0] <= FeatureIds.Length)
			{
				ushort id = FeatureIds[a[0] - 1];
				return new byte[4] { (byte)(id >> 8), (byte)id, 0, 0 };
			}
			return null;
		case 0x0005:
			if (fn == 0)
			{
				return new byte[1] { (byte)Name.Length };
			}
			if (fn == 1)
			{
				return System.Text.Encoding.ASCII.GetBytes(Name.Substring(Math.Min(a[0], Name.Length)).PadRight(16, '\0').Substring(0, 16));
			}
			return new byte[1];
		case 0x1001:
			return new byte[3] { (byte)(BatteryMv >> 8), (byte)BatteryMv, 0 };
		case 0x8071:
			if (fn == 0)
			{
				if (a[0] == 0xFF)
				{
					return new byte[7] { 0xFF, 0, (byte)ClusterEffects.Length, 0, 2, 0, 4 };
				}
				if (a[0] >= ClusterEffects.Length)
				{
					return null;
				}
				if (a[1] == 0xFF)
				{
					return new byte[5] { a[0], 0, 0, (byte)ClusterPlaces[a[0]], (byte)ClusterEffects[a[0]].Length };
				}
				if (a[1] >= ClusterEffects[a[0]].Length)
				{
					return null;
				}
				int e = ClusterEffects[a[0]][a[1]];
				return new byte[4] { a[0], a[1], (byte)(e >> 8), (byte)e };
			}
			if (fn == 1)
			{
				if ((SwMode & 1) == 0)
				{
					err = 5;
					return null;
				}
				if (a[0] >= ClusterEffects.Length || a[1] >= ClusterEffects[a[0]].Length)
				{
					return null;
				}
				Effects[a[0]] = (ClusterEffects[a[0]][a[1]], a[2..12]);
			}
			if (fn == 5)
			{
				if (a[0] == 1)
				{
					SwMode = a[1];
					SwFlags = a[2];
				}
				return new byte[3] { a[0], SwMode, SwFlags };
			}
			return new byte[0];
		case 0x8081:
			if (fn == 0)
			{
				// Карта номеров: [0, страница, 14 байт — бит на номер страница×112 + i].
				byte[] map = new byte[16];
				map[1] = a[1];
				foreach (byte id in Accepted)
				{
					int i = id - a[1] * 112;
					if (i >= 0 && i < 112)
					{
						map[2 + i / 8] |= (byte)(1 << (i % 8));
					}
				}
				return map;
			}
			if (fn == 6)
			{
				if (Mode != 2)
				{
					err = 7;
					return null;
				}
				for (int i = 3; i < 16; i++)
				{
					if (!Accepted.Contains(a[i]))
					{
						return null;
					}
				}
				for (int i = 3; i < 16; i++)
				{
					_pending[a[i]] = new Rgb(a[0], a[1], a[2]);
				}
			}
			else if (fn == 7)
			{
				foreach (var (id, c) in _pending)
				{
					Frame[id] = c;
				}
				_pending.Clear();
			}
			return new byte[0];
		case 0x8010:
			if (fn == 0)
			{
				return new byte[1] { 5 };
			}
			if (fn == 2)
			{
				Diverted = a[0] != 0;
			}
			return new byte[0];
		case 0x8020:
			if (fn == 0)
			{
				return new byte[1] { 3 };
			}
			if (fn == 1)
			{
				MLeds = a[0];
			}
			return new byte[0];
		case 0x8040:
			if (fn == 0)
			{
				return new byte[4] { 0, 100, 5, 3 };
			}
			if (fn == 1)
			{
				return new byte[2] { (byte)(Brightness >> 8), (byte)Brightness };
			}
			if (fn == 2)
			{
				Brightness = (a[0] << 8) | a[1];
				_echo = Brightness;
			}
			return new byte[0];
		case 0x4540:
			return new byte[1] { (byte)Layout };
		case 0x8100:
			return Onboard(fn, a, out err);
		default:
			return new byte[0];
		}
	}

	private byte[]? Onboard(byte fn, byte[] a, out byte err)
	{
		err = 2;
		switch (fn)
		{
		case 0:
			return Description;
		case 1:
			Mode = a[0];
			ModeChanges.Add(a[0]);
			if (Mode == 1)
			{
				Diverted = false;
			}
			return new byte[0];
		case 2:
			return new byte[1] { (byte)Mode };
		case 3:
			// В режиме PulseBar профиль не меняется: ошибка 2 (журнал).
			if (Mode == 2)
			{
				return null;
			}
			Profile = (a[0] << 8) | a[1];
			return new byte[0];
		case 4:
			return new byte[2] { (byte)(Profile >> 8), (byte)Profile };
		case 5:
		{
			int sector = (a[0] << 8) | a[1];
			int offset = (a[2] << 8) | a[3];
			if (!Sectors.TryGetValue(sector, out byte[]? s) || offset > s.Length - 16)
			{
				return null;
			}
			return s[offset..(offset + 16)];
		}
		case 6:
			_writeSector = (a[0] << 8) | a[1];
			if (_writeSector >= 0x100)
			{
				return null;
			}
			_write = new byte[(a[4] << 8) | a[5]];
			_writeOffset = 0;
			return new byte[0];
		case 7:
			if (_write == null)
			{
				return null;
			}
			Array.Copy(a, 0, _write, _writeOffset, Math.Min(16, _write.Length - _writeOffset));
			_writeOffset += 16;
			return new byte[0];
		case 8:
			if (_write == null)
			{
				return null;
			}
			Sectors[_writeSector] = _write;
			SectorWrites.Add(_writeSector);
			_write = null;
			return new byte[0];
		default:
			return new byte[0];
		}
	}
}
