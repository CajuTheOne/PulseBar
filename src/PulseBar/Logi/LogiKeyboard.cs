using System;
using System.Collections.Generic;
using System.Linq;

namespace PulseBar.Logi;

// Клавиатура под управлением PulseBar (2.3): режим PulseBar (0x8100 режим 2), перехват G-клавиш, кадр подсветки,
// яркость, лампочки M. Помнит, что уже применено, и отправляет только разницу; после переподключения или пробуждения
// (Invalidate) всё применяется заново. Вызывается только из потока службы.
internal sealed class LogiKeyboard
{
	// Эффект «статичный цвет» и прочие — id из таблицы 0x8071 (совпадают с режимами записей встроенной памяти).
	public static readonly (int Id, string Name)[] EffectNames = new (int, string)[5]
	{
		(0x00, "off"),
		(0x01, "static"),
		(0x0A, "breathing"),
		(0x03, "cycle"),
		(0x04, "wave")
	};

	// Таблица эффектов и подсвечиваемые клавиши не меняются: читаются один раз за запуск для каждой клавиатуры.
	private static readonly Dictionary<string, (Dictionary<int, Dictionary<int, int>> Clusters, byte[] Keys)> Known = new Dictionary<string, (Dictionary<int, Dictionary<int, int>>, byte[])>();

	private string? _lighting;

	private int _brightness = -1;

	private int _mleds = -1;

	private bool _setUp;

	public LogiDevice Device { get; }

	public KeyEdges Edges { get; } = new KeyEdges();

	// Кластеры 0x8071: номер → (индекс эффекта → id эффекта). Обычно 0 — клавиши, 1 — логотип.
	public Dictionary<int, Dictionary<int, int>> Clusters { get; } = new Dictionary<int, Dictionary<int, int>>();

	// Номера клавиш, которые принимает 0x8081 (подсвечиваемые клавиши).
	public byte[] KeyIds { get; private set; } = Array.Empty<byte>();

	public LogiKeyboard(LogiDevice device)
	{
		Device = device;
	}

	public bool SetUpDone => _setUp;

	public void Invalidate()
	{
		_setUp = false;
		_lighting = null;
		_brightness = -1;
		_mleds = -1;
		lock (Edges)
		{
			Edges.Reset();
		}
	}

	// Режим PulseBar и перехват G-клавиш. Повторять безопасно.
	public bool SetUp(out string error)
	{
		error = "";
		if (Device.Call(LogiDevice.OnboardProfiles, 1, new byte[1] { OnboardMemory.ModeHost }, out int e, 3) == null)
		{
			error = $"host mode: error {e}";
			return false;
		}
		if (Device.Has(LogiDevice.GKeys) && Device.Call(LogiDevice.GKeys, 2, new byte[1] { 1 }, out e, 3) == null)
		{
			error = $"G-keys: error {e}";
			return false;
		}
		if (Clusters.Count == 0 || KeyIds.Length == 0)
		{
			lock (Known)
			{
				if (Known.TryGetValue(Device.Name, out var k))
				{
					foreach (var (c, map) in k.Clusters)
					{
						Clusters[c] = map;
					}
					KeyIds = k.Keys;
				}
			}
		}
		if (Clusters.Count == 0)
		{
			ReadEffects();
		}
		if (KeyIds.Length == 0 && Device.Has(LogiDevice.PerKeyLighting))
		{
			KeyIds = ProbeKeyIds();
		}
		lock (Known)
		{
			Known[Device.Name] = (Clusters, KeyIds);
		}
		_setUp = true;
		return true;
	}

	// Режим клавиатуры сейчас (для сверки): 2 — PulseBar, 1 — память, -1 — нет ответа.
	public int ReadMode()
	{
		return Device.Call(LogiDevice.OnboardProfiles, 2, ReadOnlySpan<byte>.Empty, out _, 2)?[0] ?? (-1);
	}

	public bool Apply(KeyFrame f, out string error)
	{
		error = "";
		string key = f.LightingKey;
		if (key != _lighting)
		{
			if (!PaintLighting(f, out error))
			{
				return false;
			}
			_lighting = key;
		}
		if (f.Brightness != _brightness && Device.Has(LogiDevice.Brightness))
		{
			if (Device.Call(LogiDevice.Brightness, 2, new byte[2] { (byte)(f.Brightness >> 8), (byte)f.Brightness }, out int e, 3) == null)
			{
				error = $"brightness: error {e}";
				return false;
			}
			_brightness = f.Brightness;
		}
		if (f.MLeds != _mleds && Device.Has(LogiDevice.MKeys))
		{
			if (Device.Call(LogiDevice.MKeys, 1, new byte[1] { f.MLeds }, out int e, 3) == null)
			{
				error = $"M LEDs: error {e}";
				return false;
			}
			_mleds = f.MLeds;
		}
		return true;
	}

	// Вернуть клавиатуру её памяти: G-клавиши не перехватывать, профиль profile (1–3), встроенный режим.
	public void Release(int profile)
	{
		if (Device.Has(LogiDevice.GKeys))
		{
			Device.Call(LogiDevice.GKeys, 2, new byte[1], out _, 2);
		}
		Device.Call(LogiDevice.OnboardProfiles, 1, new byte[1] { OnboardMemory.ModeOnboard }, out _, 2);
		if (profile > 0)
		{
			Device.Call(LogiDevice.OnboardProfiles, 3, new byte[2] { (byte)(profile >> 8), (byte)profile }, out _, 2);
		}
		Invalidate();
	}

	private bool PaintLighting(KeyFrame f, out string error)
	{
		error = "";
		if (!SetEffect(0, f.Keys, out error) || !SetEffect(1, f.Logo, out error))
		{
			return false;
		}
		if (f.PerKey && KeyIds.Length > 0)
		{
			Rgb baseColor = Rgb.Parse(f.Keys.Color);
			// Логотип — своя зона со своим эффектом: в кадр клавиш не входит.
			foreach (IGrouping<Rgb, byte> g in KeyIds.Where((byte id) => id != KeyLayout.Logo).GroupBy((byte id) => f.Colors.TryGetValue(id, out Rgb c) ? c : baseColor))
			{
				byte[] ids = g.ToArray();
				for (int i = 0; i < ids.Length; i += 13)
				{
					if (!PaintKeys(g.Key, ids.AsSpan(i, Math.Min(13, ids.Length - i)), out int e))
					{
						error = $"key colours: error {e}";
						return false;
					}
				}
			}
			if (Device.Call(LogiDevice.PerKeyLighting, 7, new byte[5], out int e2, 3) == null)
			{
				error = $"key colours frame: error {e2}";
				return false;
			}
		}
		return true;
	}

	// Эффект кластера через 0x8071: [кластер, индекс эффекта, 10 параметров, 0 — не сохранять в памяти].
	// Яркость здесь всегда 100: общую яркость задаёт 0x8040.
	private bool SetEffect(int cluster, Lighting l, out string error)
	{
		error = "";
		if (!Clusters.TryGetValue(cluster, out Dictionary<int, int>? effects))
		{
			return true;
		}
		byte[] entry = OnboardPlan.Entry(l, 100);
		int index = effects.FirstOrDefault(x => x.Value == entry[0], new KeyValuePair<int, int>(-1, -1)).Key;
		if (index < 0)
		{
			// Такого эффекта у зоны нет — статичный цвет.
			entry = OnboardSector.Static(Rgb.Parse(l.Color));
			index = effects.FirstOrDefault(x => x.Value == entry[0], new KeyValuePair<int, int>(-1, -1)).Key;
			if (index < 0)
			{
				return true;
			}
		}
		byte[] args = new byte[13];
		args[0] = (byte)cluster;
		args[1] = (byte)index;
		Array.Copy(entry, 1, args, 2, 10);
		if (Device.Call(LogiDevice.RgbEffects, 1, args, out int e, 3) == null)
		{
			error = $"effect {l.Effect} (zone {cluster}): error {e}";
			return false;
		}
		return true;
	}

	// 0x8081 fn6: один цвет для 1–13 клавиш [R, G, B, номера…], недостающие места — повтор последнего номера.
	private bool PaintKeys(Rgb c, ReadOnlySpan<byte> ids, out int error)
	{
		byte[] args = new byte[16];
		args[0] = c.R;
		args[1] = c.G;
		args[2] = c.B;
		for (int i = 0; i < 13; i++)
		{
			args[3 + i] = ids[Math.Min(i, ids.Length - 1)];
		}
		return Device.Call(LogiDevice.PerKeyLighting, 6, args, out error, 3) != null;
	}

	// Таблица эффектов 0x8071: fn0 [кластер, индекс] → [кластер, индекс, id эффекта BE16, …]; ошибка — индекс за концом.
	private void ReadEffects()
	{
		if (!Device.Has(LogiDevice.RgbEffects))
		{
			return;
		}
		for (int c = 0; c < 4; c++)
		{
			Dictionary<int, int> map = new Dictionary<int, int>();
			for (int i = 0; i < 16; i++)
			{
				byte[]? r = Device.Call(LogiDevice.RgbEffects, 0, new byte[2] { (byte)c, (byte)i }, out _, 2);
				if (r == null)
				{
					break;
				}
				map[r[1]] = (r[2] << 8) | r[3];
			}
			if (map.Count == 0)
			{
				break;
			}
			Clusters[c] = map;
		}
		Log.Write("logi: effects " + string.Join("; ", Clusters.Select(c => $"zone {c.Key}: " + string.Join(",", c.Value.Values.Select(v => $"{v:X2}")))));
	}

	// Какие номера клавиш принимает 0x8081: пробуем блоками по 13 (чёрным, без показа — кадр не завершается),
	// при ошибке «неверный аргумент» — по одной.
	private byte[] ProbeKeyIds()
	{
		List<byte> ok = new List<byte>();
		for (int start = 1; start < 256; start += 13)
		{
			byte[] block = Enumerable.Range(start, Math.Min(13, 256 - start)).Select(i => (byte)i).ToArray();
			if (PaintKeys(Rgb.Black, block, out int e))
			{
				ok.AddRange(block);
				continue;
			}
			if (e != 2)
			{
				Log.Write($"logi: key probe stopped at {start} (error {e})");
				break;
			}
			foreach (byte id in block)
			{
				if (PaintKeys(Rgb.Black, new byte[1] { id }, out _))
				{
					ok.Add(id);
				}
			}
		}
		Log.Write($"logi: {ok.Count} lit keys");
		return ok.ToArray();
	}
}
