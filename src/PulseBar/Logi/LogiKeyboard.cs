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

	// Программное управление подсветкой (0x8071 fn5) до PulseBar: [режим, флаги]; возвращается при отпускании.
	private byte[]? _swOriginal;

	// Цвета, уже отправленные клавиатуре: номер клавиши → цвет (пусто — отправить все).
	private readonly Dictionary<byte, Rgb> _sent = new Dictionary<byte, Rgb>();

	// Столбец волны для каждой клавиши (по раскладке).
	private Dictionary<byte, float>? _columns;

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
		_sent.Clear();
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
		EnsureSoftwareControl();
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

	// ms — время анимации (для дыхания, цикла, волны): кадр с тем же отпечатком дорисовывает только изменения.
	public bool Apply(KeyFrame f, long ms, out string error)
	{
		error = "";
		string key = f.LightingKey;
		if (key != _lighting)
		{
			_sent.Clear();
			if (!PaintFrame(f, f.Animate ? ms : (-1), out error))
			{
				return false;
			}
			_lighting = key;
		}
		else if (f.Animate && !PaintFrame(f, ms, out error))
		{
			return false;
		}
		if (f.Brightness != _brightness && Device.Has(LogiDevice.Brightness))
		{
			LastBrightness = f.Brightness;
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
		if (_swOriginal != null && Device.Has(LogiDevice.RgbEffects))
		{
			Device.Call(LogiDevice.RgbEffects, 5, new byte[3] { 1, _swOriginal[0], _swOriginal[1] }, out _, 2);
		}
		Device.Call(LogiDevice.OnboardProfiles, 1, new byte[1] { OnboardMemory.ModeOnboard }, out _, 2);
		if (profile > 0)
		{
			Device.Call(LogiDevice.OnboardProfiles, 3, new byte[2] { (byte)(profile >> 8), (byte)profile }, out _, 2);
		}
		Invalidate();
	}

	// Программное управление подсветкой, 0x8071 fn5: [0] → [0, режим, флаги]; [1, режим, флаги] — записать.
	// Проверено на G915 31.10.2026: после включения питания 00 00; служба Logitech LampArray ставит 02 06 или 03 06 и
	// красит клавиши по одной; с режимом 03 06 цвета по клавишам видны. Ставим биты 0 и 1, флаги оставляем (0 → 06, как
	// у LampArray); при отпускании возвращается прежнее значение. Служба может сменить режим снова — он проверяется перед
	// каждым кадром.
	private void EnsureSoftwareControl()
	{
		if (!Device.Has(LogiDevice.RgbEffects))
		{
			return;
		}
		byte[]? r = Device.Call(LogiDevice.RgbEffects, 5, new byte[1], out int e, 2);
		if (r == null || r.Length < 3)
		{
			Log.Write($"logi: software lighting control: error {e}");
			return;
		}
		_swOriginal ??= new byte[2] { r[1], r[2] };
		if ((r[1] & 3) == 3)
		{
			return;
		}
		byte mode = (byte)(r[1] | 3);
		byte flags = (byte)((r[2] == 0) ? 6 : r[2]);
		bool ok = Device.Call(LogiDevice.RgbEffects, 5, new byte[3] { 1, mode, flags }, out e, 2) != null;
		Log.Write($"logi: software lighting control {r[1]:X2} {r[2]:X2} -> {mode:X2} {flags:X2}{(ok ? "" : $": error {e}")}");
	}

	// Кадр подсветки в режиме PulseBar — только цветами по клавишам (0x8081). Эффекты 0x8071 G915 в этом режиме
	// принимает, но не показывает (проверка 31.10.2026; G HUB в этом режиме тоже рисует сам). Клавиши — цвет зоны
	// (выключена — чёрный) или свои цвета в режиме «По клавишам»; логотип — номер 210 с цветом своей зоны. Дыхание, цикл и
	// волна рисуются кадрами (ms — время анимации; -1 — без анимации: статичный цвет зоны). Отправляются только клавиши,
	// цвет которых изменился, группами одного цвета; после сброса (_sent пуст) — все.
	private bool PaintFrame(KeyFrame f, long ms, out string error)
	{
		error = "";
		if (KeyIds.Length == 0 || !Device.Has(LogiDevice.PerKeyLighting))
		{
			return true;
		}
		if (_sent.Count == 0)
		{
			EnsureSoftwareControl();
		}
		Rgb keys = Rgb.Parse(f.Keys.Color);
		Rgb logo = Rgb.Parse(f.Logo.Color);
		Dictionary<float, Rgb> byColumn = new Dictionary<float, Rgb>();
		Dictionary<byte, Rgb> changed = new Dictionary<byte, Rgb>();
		foreach (byte id in KeyIds)
		{
			Rgb c;
			if (id == KeyLayout.Logo)
			{
				c = ColorAt(f.Logo, logo, 0.0, ms);
			}
			else if (f.PerKey)
			{
				c = f.Colors.TryGetValue(id, out Rgb k) ? k : keys;
			}
			else
			{
				float x = Column(id);
				if (!byColumn.TryGetValue(x, out c))
				{
					c = byColumn[x] = ColorAt(f.Keys, keys, x, ms);
				}
			}
			if (!_sent.TryGetValue(id, out Rgb old) || old != c)
			{
				changed[id] = c;
			}
		}
		if (changed.Count == 0)
		{
			return true;
		}
		foreach (IGrouping<Rgb, byte> g in changed.Keys.GroupBy(id => changed[id]))
		{
			byte[] ids = g.ToArray();
			for (int i = 0; i < ids.Length; i += 13)
			{
				if (!PaintKeys(g.Key, ids.AsSpan(i, Math.Min(13, ids.Length - i)), out int e))
				{
					_sent.Clear();
					error = $"key colours: error {e}";
					return false;
				}
			}
		}
		if (Device.Call(LogiDevice.PerKeyLighting, 7, new byte[5], out int e2, 3) == null)
		{
			_sent.Clear();
			error = $"key colours frame: error {e2}";
			return false;
		}
		foreach (var (id, c) in changed)
		{
			_sent[id] = c;
		}
		return true;
	}

	public static bool Animated(Lighting l)
	{
		return l.Effect == "breathing" || l.Effect == "cycle" || l.Effect == "wave";
	}

	// Цвет зоны в момент ms (-1 — без анимации): дыхание — яркость цвета по синусоиде, цикл — оттенок по кругу, волна —
	// оттенок по кругу со сдвигом по горизонтали (x — 0 слева … 1 справа), волна идёт слева направо.
	internal static Rgb ColorAt(Lighting l, Rgb color, double x, long ms)
	{
		switch (l.Effect)
		{
		case "off":
			return Rgb.Black;
		case "breathing":
		{
			if (ms < 0)
			{
				return color;
			}
			double k = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * Phase(ms, l.Period, OnboardPlan.DefaultBreathePeriod));
			return new Rgb((byte)Math.Round(color.R * k), (byte)Math.Round(color.G * k), (byte)Math.Round(color.B * k));
		}
		case "cycle":
			return (ms < 0) ? color : Hue(Phase(ms, l.Period, OnboardPlan.DefaultCyclePeriod));
		case "wave":
			return (ms < 0) ? color : Hue(Phase(ms, l.Period, OnboardPlan.DefaultWavePeriod) - x);
		default:
			return color;
		}
	}

	private static double Phase(long ms, int period, int fallback)
	{
		int p = (period > 0) ? period : fallback;
		return (double)(ms % p) / p;
	}

	// Оттенок (доля круга) при полной насыщенности и яркости.
	private static Rgb Hue(double turns)
	{
		double h = (turns - Math.Floor(turns)) * 6.0;
		int sector = (int)h % 6;
		double f = h - Math.Floor(h);
		byte up = (byte)Math.Round(255 * f);
		byte down = (byte)Math.Round(255 * (1 - f));
		return sector switch
		{
			0 => new Rgb(255, up, 0),
			1 => new Rgb(down, 255, 0),
			2 => new Rgb(0, 255, up),
			3 => new Rgb(0, down, 255),
			4 => new Rgb(up, 0, 255),
			_ => new Rgb(255, 0, down)
		};
	}

	// Положение клавиши по горизонтали 0…1, округлённое до 1/16 (16 столбцов волны — меньше групп цвета и пакетов).
	private float Column(byte id)
	{
		if (_columns == null)
		{
			KeyDef[] defs = KeyLayout.For(KeyIds);
			float min = defs.Min(k => k.X + k.W / 2f);
			float max = defs.Max(k => k.X + k.W / 2f);
			_columns = defs.ToDictionary(k => k.Id, k => MathF.Round((k.X + k.W / 2f - min) / Math.Max(1f, max - min) * 16f) / 16f);
		}
		return _columns.TryGetValue(id, out float x) ? x : 0f;
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
		_sent.Clear();
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
			if (value >= 0)
			{
				LastBrightness = value;
			}
		}
	}

	// Яркость на клавиатуре: последняя отправленная или сообщённая ею (-1 — неизвестно). Клавиатура сообщает о каждой
	// записи яркости тем же значением — по нему отзвук своей записи отличается от нажатия кнопки. Сброс не меняет.
	public int LastBrightness { get; private set; } = -1;

	// Подсветку гасила прошивка или простой: яркость и весь кадр отправить заново.
	public void Relight()
	{
		_brightness = -1;
		InvalidateLighting();
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
	// клавиатура принимает, а не какие клавиши у неё есть, поэтому оставляем только клавиши раскладки: ANSI, ISO или
	// русская — по 0x4540.
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
		HashSet<byte> physical = KeyLayout.Physical(layout);
		byte[] keys = ids.Where(physical.Contains).ToArray();
		Log.Write($"logi: layout {layout} ({KeyLayout.ShapeName(layout)}), {ids.Count} key numbers, {keys.Length} keys");
		return keys;
	}
}
