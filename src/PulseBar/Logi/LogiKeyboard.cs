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

	// Таблица эффектов, зоны и подсвечиваемые клавиши не меняются: читаются один раз за запуск для каждой клавиатуры.
	private sealed record Caps(Dictionary<int, Dictionary<int, int>> Clusters, int KeysZone, int LogoZone, byte[] Keys);

	private static readonly Dictionary<string, Caps> Known = new Dictionary<string, Caps>();

	// Для тестов: разные модели клавиатуры с одним именем.
	internal static void ForgetKnown()
	{
		lock (Known)
		{
			Known.Clear();
		}
	}

	private string? _lighting;

	private int _brightness = -1;

	private int _mleds = -1;

	private bool _setUp;

	public LogiDevice Device { get; }

	public KeyEdges Edges { get; } = new KeyEdges();

	// Кластеры 0x8071: номер → (индекс эффекта → id эффекта).
	public Dictionary<int, Dictionary<int, int>> Clusters { get; } = new Dictionary<int, Dictionary<int, int>>();

	// Какой кластер — клавиши, какой — логотип (по месту из 0x8071; у G915 0 — логотип, 1 — клавиши); -1 — нет.
	public int KeysZone { get; private set; } = -1;

	public int LogoZone { get; private set; } = -1;

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
				if (Known.TryGetValue(Device.Name, out Caps? k))
				{
					foreach (var (c, map) in k.Clusters)
					{
						Clusters[c] = map;
					}
					KeysZone = k.KeysZone;
					LogoZone = k.LogoZone;
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
			KeyIds = ReadKeyIds();
		}
		lock (Known)
		{
			Known[Device.Name] = new Caps(Clusters, KeysZone, LogoZone, KeyIds);
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
		if (!SetEffect(KeysZone, f.Keys, out error) || !SetEffect(LogoZone, f.Logo, out error))
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

	// Эффект кластера через 0x8071: [кластер, индекс эффекта, 10 параметров, 1]. Последний байт — 1, как у OpenRGB и
	// Solaar: с 0 G915 отвечает ошибкой 5 (проверка 31.10.2026). Параметры — как в записи встроенной памяти.
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
		args[12] = 1;
		if (Device.Call(LogiDevice.RgbEffects, 1, args, out int e, 3) == null)
		{
			if (e > 0 && e != HidppLink.ErrBusy)
			{
				// Клавиатура отклонила параметры: кадр без этого эффекта, иначе сверка повторялась бы без конца.
				Log.Write($"logi: effect {l.Effect} (zone {cluster}) rejected: error {e}");
				return true;
			}
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

	// Эффекты зоны (логотип или клавиши): индекс эффекта → id эффекта; null — такой зоны нет.
	public Dictionary<int, int>? EffectsOf(bool logo)
	{
		return Clusters.TryGetValue(logo ? LogoZone : KeysZone, out Dictionary<int, int>? map) ? map : null;
	}

	// Подсветка изменилась не нами (служба Windows «Динамическое освещение» и т. п.): кадр отправить заново.
	public void InvalidateLighting()
	{
		_lighting = null;
	}

	// Яркость, поставленная кнопкой на клавиатуре: уже применена, второй раз не отправлять.
	public int AppliedBrightness
	{
		get
		{
			return _brightness;
		}
		set
		{
			_brightness = value;
		}
	}

	// Таблица эффектов 0x8071 (журнал G915 31.10.2026):
	//   fn0 [кластер, 0xFF] → [кластер, 0, место BE16, число эффектов]; место 1 — клавиши, 2 — логотип;
	//   fn0 [кластер, индекс] → [кластер, индекс, id эффекта BE16, …]; ошибка 2 — индекс или кластер за концом.
	private void ReadEffects()
	{
		if (!Device.Has(LogiDevice.RgbEffects))
		{
			return;
		}
		Dictionary<int, int> places = new Dictionary<int, int>();
		for (int c = 0; c < 4; c++)
		{
			byte[]? info = Device.Call(LogiDevice.RgbEffects, 0, new byte[2] { (byte)c, 0xFF }, out _, 2);
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
			places[c] = (info != null) ? ((info[2] << 8) | info[3]) : 0;
		}
		KeysZone = places.FirstOrDefault(x => x.Value == 1, new KeyValuePair<int, int>(-1, 0)).Key;
		LogoZone = places.FirstOrDefault(x => x.Value == 2, new KeyValuePair<int, int>(-1, 0)).Key;
		// Место не сообщили: клавиши — зона с наибольшим числом эффектов, логотип — другая.
		if (KeysZone < 0)
		{
			KeysZone = Clusters.Where(x => x.Key != LogoZone).OrderByDescending(x => x.Value.Count).Select(x => x.Key).DefaultIfEmpty(-1).First();
		}
		if (LogoZone < 0)
		{
			LogoZone = Clusters.Keys.Where(x => x != KeysZone).DefaultIfEmpty(-1).First();
		}
		Log.Write($"logi: effects keys zone {KeysZone}, logo zone {LogoZone}; " + string.Join("; ", Clusters.Select(c => $"zone {c.Key}: " + string.Join(",", c.Value.Values.Select(v => $"{v:X2}")))));
	}

	// Номера клавиш: карта 0x8081 fn0 [0, страница] → [0, страница, 14 байт — по биту на номер страница×112 + i]
	// (у G915 страница 0 — все номера 0–111, страница 1 — мультимедиа, G1–G5, логотип). Карта говорит, какие номера
	// клавиатура принимает, а не какие клавиши у неё есть, поэтому оставляем только клавиши раскладки: ANSI или ISO —
	// по 0x4540.
	private byte[] ReadKeyIds()
	{
		List<byte> ids = new List<byte>();
		for (int page = 0; page < 3; page++)
		{
			byte[]? r = Device.Call(LogiDevice.PerKeyLighting, 0, new byte[2] { 0, (byte)page }, out int e, 2);
			if (r == null || r.Length < 16)
			{
				Log.Write($"logi: key map page {page}: error {e}");
				break;
			}
			for (int i = 0; i < 14 * 8; i++)
			{
				int id = page * 112 + i;
				if (id < 256 && (r[2 + i / 8] & (1 << (i % 8))) != 0)
				{
					ids.Add((byte)id);
				}
			}
		}
		int layout = Device.Has(LogiDevice.KeyboardLayout) ? (Device.Call(LogiDevice.KeyboardLayout, 0, ReadOnlySpan<byte>.Empty, out _, 2)?[0] ?? 0) : 0;
		bool iso = KeyLayout.IsIso(layout);
		HashSet<byte> physical = KeyLayout.Physical(iso);
		byte[] keys = ids.Where(physical.Contains).ToArray();
		Log.Write($"logi: layout {layout} ({(iso ? "ISO" : "ANSI")}), {ids.Count} key numbers, {keys.Length} keys");
		return keys;
	}
}
