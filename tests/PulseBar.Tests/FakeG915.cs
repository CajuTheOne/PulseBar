using System;
using System.Collections.Generic;
using System.Linq;
using PulseBar.Logi;

namespace PulseBar.Tests;

// Модель G915 на приёмнике Lightspeed (слот 1) для тестов: отвечает на запросы HID++ так же, как настоящая клавиатура
// (таблица функций, память профилей из копии, подсветка, яркость, лампочки M), и ведёт себя как прошивка при нажатиях:
// во встроенном режиме M переключает профиль сама, в режиме PulseBar — только сообщает о нажатии.
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

	private static readonly int[][] ClusterEffects = new int[2][]
	{
		new int[5] { 0x00, 0x01, 0x0A, 0x03, 0x04 },
		new int[4] { 0x00, 0x01, 0x0A, 0x03 }
	};

	private readonly object _lock = new object();

	private byte[]? _write;

	private int _writeSector;

	private int _writeOffset;

	public readonly Dictionary<int, byte[]> Sectors;

	public readonly HashSet<byte> LitKeys;

	// Что клавиатура получила (для проверок).
	public readonly List<(ushort Feature, byte Fn, byte[] Args)> Calls = new List<(ushort, byte, byte[])>();

	public readonly List<int> ModeChanges = new List<int>();

	public readonly List<int> SectorWrites = new List<int>();

	public int Mode = 1;

	public int Profile = 1;

	public bool Diverted;

	public int MLeds = -1;

	public int Brightness = 100;

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
		// ANSI-раскладка без логотипа и мультимедиа — как у клавиатуры, которая их не подсвечивает.
		LitKeys = new HashSet<byte>(KeyLayout.For(Array.Empty<byte>()).Select(k => k.Id).Where(id => id < 150 || id >= 180));
		LitKeys.Add(KeyLayout.Logo);
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

	public void PressM(int n)
	{
		if (Mode == 1)
		{
			lock (_lock)
			{
				Profile = n;
			}
			Notify(0x8100, 0, new byte[2] { 0, (byte)n });
			return;
		}
		Notify(0x8020, 0, new byte[2] { (byte)(1 << (n - 1)), 0 });
		Notify(0x8020, 0, new byte[2]);
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
		lock (_lock)
		{
			reply = (q[0] == 0x10) ? Register(q) : Feature(q);
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
				if (a[0] >= ClusterEffects.Length || a[1] >= ClusterEffects[a[0]].Length)
				{
					return null;
				}
				int e = ClusterEffects[a[0]][a[1]];
				return new byte[4] { a[0], a[1], (byte)(e >> 8), (byte)e };
			}
			if (fn == 1)
			{
				if (a[0] >= ClusterEffects.Length || a[1] >= ClusterEffects[a[0]].Length)
				{
					return null;
				}
				Effects[a[0]] = (ClusterEffects[a[0]][a[1]], a[2..12]);
			}
			return new byte[0];
		case 0x8081:
			if (fn == 6)
			{
				if (Mode != 2)
				{
					err = 7;
					return null;
				}
				for (int i = 3; i < 16; i++)
				{
					if (!LitKeys.Contains(a[i]))
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
			}
			return new byte[0];
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
