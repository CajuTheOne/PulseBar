using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using PulseBar.Ai;

namespace PulseBar;

// Лимиты подписки Claude (5 часов и 7 дней), как на claude.ai.
// Аккаунт — тот, в который вошёл Claude Code (~\.claude\.credentials.json); его организацию узнаём один раз на токен
// через api/oauth/profile и запоминаем в %LOCALAPPDATA%\PulseBar\claude-account.json.
// Три источника, берётся более свежий:
//  - вход самого приложения Claude (ClaudeAppAuth, с 2.4.0-beta.8) — GET api/oauth/usage за аккаунт, открытый в
//    приложении: точные проценты и время сброса, приложение может быть свёрнуто в трей сколько угодно;
//  - %APPDATA%\Claude\plan-usage-history.json — приложение Claude пишет туда проценты примерно раз в 15 мин;
//    токен не нужен, лимитов на запросы нет, но нет времени сброса. Приложение может быть в другом аккаунте или
//    организации (у каждой записи есть "org"), поэтому берутся только записи организации Claude Code;
//  - GET https://api.anthropic.com/api/oauth/usage с токеном входа Claude Code (~\.claude\.credentials.json).
//    Эндпоинт недокументирован и режет частые запросы (429 с Retry-After до часа), поэтому опрос редкий,
//    Retry-After соблюдается, токен не продлевается — это делает сам Claude Code. Квота не тратится.
internal sealed class ClaudeUsage : IDisposable
{
	// ResetsEstimated — время сброса оценено по истории приложения Claude (claude.ai его не дал), показывается с «≈».
	public sealed record Snapshot(float FiveHour, float SevenDay, DateTimeOffset? FiveHourReset, DateTimeOffset? SevenDayReset,
		DateTimeOffset Updated, string Source, bool ResetsEstimated = false)
	{
		// Старше 20 минут — показываем бледным: приложение Claude обновляет историю раз в ~15 минут.
		public bool Stale => DateTimeOffset.Now - Updated > TimeSpan.FromMinutes(20);
	}

	private static readonly string Credentials = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

	private static readonly string History = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");

	private static readonly string AccountCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", "claude-account.json");

	// Журнал приложения Claude: по нему видно, что приложение перестало обновлять историю.
	private static readonly string AppLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude", "logs", "main.log");

	private string? _profileToken;

	private DateTimeOffset _profileRetryAt = DateTimeOffset.MinValue;

	private volatile string? _org;

	// Почта аккаунта Claude Code (пусто — ещё не известна).
	public volatile string Account = "";

	// Вход приложения Claude: организация его токена, почта аккаунта (api/oauth/profile, один раз на токен).
	private volatile string? _appOrg;

	private string? _appProfileToken;

	private DateTimeOffset _appProfileRetryAt = DateTimeOffset.MinValue;

	private volatile string _appEmail = "";

	// Что видно о входе приложения — в журнал, только когда меняется.
	private string _appState = "";

	// true — показывать аккаунт, открытый сейчас в приложении Claude (организация последней записи его истории);
	// false — аккаунт, в который вошёл Claude Code.
	public volatile bool FollowApp = true;

	// Организация, чьи лимиты показываются сейчас.
	private volatile string? _target;

	// История приложения Claude: последняя запись каждой организации и организация самой последней записи.
	private DateTime _historyTime;

	private string? _historyLatestOrg;

	private readonly Dictionary<string, (long T, float Fh, float Sd)> _historyByOrg = new Dictionary<string, (long, float, float)>();

	// Все записи истории по организациям (для оценки времени сброса).
	private readonly Dictionary<string, List<(long T, float Fh, float Sd)>> _series = new Dictionary<string, List<(long, float, float)>>();

	// Кому принадлежат показанные лимиты: почта (если это аккаунт Claude Code) или «аккаунт из приложения Claude».
	public string AccountLabel
	{
		get
		{
			string target = _target;
			if (target == null)
			{
				return "";
			}
			if (target == _appOrg && _appEmail.Length > 0)
			{
				return _appEmail;
			}
			if (!(target == _org) || Account.Length <= 0)
			{
				return L.T("account open in the Claude app");
			}
			return Account;
		}
	}

	private readonly Timer _timer;

	private int _intervalMin;

	private DateTimeOffset _nextApi = DateTimeOffset.MinValue;

	private int _busy;

	public volatile Snapshot? Current;

	// Последняя причина, почему нет свежих данных из API (для подсказки и журнала).
	public volatile string ApiStatus = "";

	// Почему история приложения Claude не обновляется ("" — обновляется или причина неизвестна).
	public volatile string AppStatus = "";

	public bool Available => Current != null;

	public event Action? Changed;

	private readonly bool _api;

	// api = false — только история приложения Claude (для снимков окна настроек: не тратить запросы к claude.ai).
	public ClaudeUsage(int intervalMin, bool api = true)
	{
		_api = api;
		_intervalMin = Math.Clamp(intervalMin, 1, 15);
		LoadAccount();
		_timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1));
	}

	public int IntervalMin
	{
		get => _intervalMin;
		set => _intervalMin = Math.Clamp(value, 1, 15);
	}

	private void Poll()
	{
		if (Interlocked.Exchange(ref _busy, 1) == 1)
		{
			return;
		}
		try
		{
			Snapshot? before = Current;
			Snapshot? next = before;
			string? token = ReadToken(out bool loggedIn);
			if (_api && token != null && EnsureAccount(token))
			{
				next = null; // данные прежнего аккаунта больше не подходят
			}
			ReadHistory();
			ClaudeAppAuth.Token? app = _api ? ClaudeAppAuth.Pick(_historyLatestOrg) : null;
			_appOrg = app?.Org;
			string state = (app == null) ? "none" : "ok";
			if (_api && state != _appState)
			{
				_appState = state;
				Log.Write((app == null) ? "claude: no Claude app sign-in" : "claude: using the Claude app sign-in");
			}
			string? codeOrg = (loggedIn ? _org : null);
			string? target = (FollowApp ? (app?.Org ?? _historyLatestOrg ?? codeOrg) : codeOrg);
			if (target != _target)
			{
				_target = target;
				next = null;
				if (target != null)
				{
					Log.Write((target == _org) ? "claude: showing the Claude Code account" : "claude: showing the account open in the Claude app");
				}
			}
			if (target == null)
			{
				next = null;
			}
			else
			{
				// claude.ai отвечает за аккаунт токена: вход приложения — за организацию его токена, вход Claude Code — за
				// свою; данные годятся, только если показываем этот же аккаунт.
				string? apiToken = (app != null && app.Org == target) ? app.Value : ((token != null && target == _org) ? token : null);
				if (_api && apiToken != null && DateTimeOffset.Now >= _nextApi)
				{
					if (app != null && apiToken == app.Value)
					{
						AppProfile(app.Value);
					}
					next = Newer(next, FromApi(apiToken)) ?? next;
				}
				next = Newer(next, FromHistory(target));
				next = Estimate(next, target);
			}
			next = Expire(next);
			AppStatus = (next != null && next.Source == "claude app" && DateTimeOffset.Now - next.Updated > TimeSpan.FromMinutes(30) && AppPaused()) ? L.T("The Claude app paused usage updates: open its usage window (Claude icon in the tray)") : "";
				Current = next;
			if (!Equals(before, next))
			{
				Changed?.Invoke();
			}
		}
		catch (Exception ex)
		{
			Log.Write("claude: " + ex.Message);
		}
		finally
		{
			Volatile.Write(ref _busy, 0);
		}
	}

	private static Snapshot? Newer(Snapshot? a, Snapshot? b)
	{
		if (b == null)
		{
			return a;
		}
		if (a == null || b.Updated > a.Updated)
		{
			// Времени сброса в истории нет — оставить известное (из API или оценку), пока оно не прошло; признак оценки — вместе с ним.
			DateTimeOffset? five = b.FiveHourReset ?? Future(a?.FiveHourReset);
			DateTimeOffset? week = b.SevenDayReset ?? Future(a?.SevenDayReset);
			bool carried = (b.FiveHourReset == null && five != null) || (b.SevenDayReset == null && week != null);
			return b with
			{
				FiveHourReset = five,
				SevenDayReset = week,
				ResetsEstimated = b.ResetsEstimated || (carried && (a?.ResetsEstimated ?? false))
			};
		}
		return a;
	}

	private static DateTimeOffset? Future(DateTimeOffset? t) => t > DateTimeOffset.Now ? t : null;

	// Окно лимита уже сбросилось (прошло время сброса, а если оно неизвестно — 5 часов или 7 дней с замера):
	// старые проценты ничего не значат, показывается «—».
	private static Snapshot? Expire(Snapshot? s)
	{
		if (s == null)
		{
			return null;
		}
		DateTimeOffset now = DateTimeOffset.Now;
		// Оценённый сброс — с запасом 30 минут: оценка может быть раньше настоящего.
		TimeSpan slack = s.ResetsEstimated ? TimeSpan.FromMinutes(30) : TimeSpan.Zero;
		bool fiveOld = s.FiveHourReset.HasValue ? (s.FiveHourReset + slack <= now) : (now - s.Updated > TimeSpan.FromHours(5));
		bool weekOld = s.SevenDayReset.HasValue ? (s.SevenDayReset + slack <= now) : (now - s.Updated > TimeSpan.FromDays(7));
		if (!fiveOld && !weekOld)
		{
			return s;
		}
		return s with
		{
			FiveHour = fiveOld ? float.NaN : s.FiveHour,
			FiveHourReset = fiveOld ? null : s.FiveHourReset,
			SevenDay = weekOld ? float.NaN : s.SevenDay,
			SevenDayReset = weekOld ? null : s.SevenDayReset
		};
	}

	// Время сброса, когда claude.ai его не дал (в истории приложения его нет): оценка по записям истории.
	private Snapshot? Estimate(Snapshot? s, string org)
	{
		if (s == null || s.Source != "claude app" || !_series.TryGetValue(org, out List<(long T, float Fh, float Sd)>? series))
		{
			return s;
		}
		long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		DateTimeOffset? five = s.FiveHourReset ?? EstimateFiveHourReset(series, now);
		DateTimeOffset? week = s.SevenDayReset ?? EstimateWeeklyReset(series, now);
		if (five == s.FiveHourReset && week == s.SevenDayReset)
		{
			return s;
		}
		return s with
		{
			FiveHourReset = five,
			SevenDayReset = week,
			ResetsEstimated = true
		};
	}

	private const long Hour = 3600000L;

	private const long Week = 7L * 24 * Hour;

	// Окно 5 часов идёт от первого сообщения (история 31.10.2026: окно с 21:3x сбросилось в 02:3x, без округления до
	// часа). Начало текущего окна — последняя запись, где процент поднялся с нуля или упал после сброса; оно между этой
	// записью и предыдущей (приложение пишет раз в ~15 минут) — берётся середина. Пропуск больше 35 минут (приложение не
	// обновляло историю) — начало неизвестно.
	internal static DateTimeOffset? EstimateFiveHourReset(IReadOnlyList<(long T, float Fh, float Sd)> s, long nowMs)
	{
		if (s.Count < 2 || !(s[^1].Fh > 0f))
		{
			return null;
		}
		for (int i = s.Count - 1; i > 0; i--)
		{
			(long T, float Fh, float Sd) prev = s[i - 1];
			(long T, float Fh, float Sd) cur = s[i];
			bool started = cur.Fh > 0f && (!(prev.Fh > 0f) || cur.Fh < prev.Fh - 1f);
			if (!started)
			{
				continue;
			}
			if (cur.T - prev.T > 35L * 60000L)
			{
				return null;
			}
			long reset = (prev.T + cur.T) / 2 + 5L * Hour;
			return (reset > nowMs) ? DateTimeOffset.FromUnixTimeMilliseconds(reset) : null;
		}
		return null;
	}

	// Недельный сброс — в одно и то же время недели (история: пятница, около 03–08 ч). Каждое падение недельного процента
	// даёт промежуток, где был сброс; промежутки разных недель, сдвинутые на целые недели к последнему, пересекаются —
	// так время уточняется. Слишком широкая оценка (больше 6 часов) — не показывается.
	internal static DateTimeOffset? EstimateWeeklyReset(IReadOnlyList<(long T, float Fh, float Sd)> s, long nowMs)
	{
		List<(long Lo, long Hi)> drops = new List<(long, long)>();
		for (int i = 1; i < s.Count; i++)
		{
			float a = float.IsNaN(s[i - 1].Sd) ? 0f : s[i - 1].Sd;
			float b = float.IsNaN(s[i].Sd) ? 0f : s[i].Sd;
			if (b < a - 1f || (a > 0f && b == 0f))
			{
				drops.Add((s[i - 1].T, s[i].T));
			}
		}
		if (drops.Count == 0)
		{
			return null;
		}
		(long lo, long hi) = drops[^1];
		for (int i = drops.Count - 2; i >= 0; i--)
		{
			long shift = (long)Math.Round((double)(drops[^1].Hi - drops[i].Hi) / Week) * Week;
			long l = Math.Max(lo, drops[i].Lo + shift);
			long h = Math.Min(hi, drops[i].Hi + shift);
			if (l <= h)
			{
				lo = l;
				hi = h;
			}
		}
		if (hi - lo > 6L * Hour)
		{
			return null;
		}
		long reset = (lo + hi) / 2;
		while (reset <= nowMs)
		{
			reset += Week;
		}
		return DateTimeOffset.FromUnixTimeMilliseconds(reset);
	}

	// Приложение Claude опрашивает лимиты, только если его окно расхода открывали за последние 24 часа; иначе пишет в
	// журнал «[plan-usage] background poll paused: tray not opened recently» (проверено 01.11.2026). Смотрим последнюю
	// запись [plan-usage] в конце журнала.
	private static bool AppPaused()
	{
		try
		{
			if (!File.Exists(AppLog))
			{
				return false;
			}
			using FileStream f = new(AppLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long start = Math.Max(0, f.Length - 256 * 1024);
			f.Seek(start, SeekOrigin.Begin);
			using StreamReader r = new(f);
			string? last = null;
			string? line;
			while ((line = r.ReadLine()) != null)
			{
				if (line.Contains("[plan-usage]", StringComparison.Ordinal) && (line.Contains("poll paused", StringComparison.Ordinal) || line.Contains("poll resumed", StringComparison.Ordinal) || line.Contains("polling", StringComparison.OrdinalIgnoreCase) || line.Contains("tray opened", StringComparison.Ordinal)))
				{
					last = line;
				}
			}
			return last != null && last.Contains("poll paused", StringComparison.Ordinal);
		}
		catch (Exception)
		{
			return false;
		}
	}

	// null — нет файла (Claude Code не вошёл); пустой токен при loggedIn — токен истёк, ждём, пока Claude Code его обновит.
	private static string? ReadToken(out bool loggedIn)
	{
		loggedIn = false;
		if (!File.Exists(Credentials))
		{
			return null;
		}
		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Credentials));
		if (!doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement o) || !o.TryGetProperty("accessToken", out JsonElement t))
		{
			return null;
		}
		loggedIn = true;
		if (o.TryGetProperty("expiresAt", out JsonElement e) && e.TryGetInt64(out long ms) && DateTimeOffset.FromUnixTimeMilliseconds(ms) <= DateTimeOffset.Now)
		{
			return null;
		}
		return t.GetString();
	}

	private Snapshot? FromApi(string token)
	{
		try
		{
			using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
			using HttpRequestMessage req = new(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
			req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
			req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
			req.Headers.UserAgent.ParseAdd("PulseBar/" + Updater.Current);
			using HttpResponseMessage resp = http.Send(req);
			if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
			{
				TimeSpan wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5);
				wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 60, 3600));
				_nextApi = DateTimeOffset.Now + wait;
				ApiStatus = $"{(int)resp.StatusCode}, retry in {wait.TotalMinutes:0} min";
				Log.Write("claude api: " + ApiStatus);
				return null;
			}
			_nextApi = DateTimeOffset.Now + TimeSpan.FromMinutes(_intervalMin);
			if (!resp.IsSuccessStatusCode)
			{
				ApiStatus = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
				Log.Write("claude api: " + ApiStatus);
				return null;
			}
			using JsonDocument doc = JsonDocument.Parse(resp.Content.ReadAsStream());
			Snapshot? s = Parse(doc.RootElement);
			ApiStatus = s != null ? "" : "unknown response format";
			return s;
		}
		catch (Exception ex)
		{
			_nextApi = DateTimeOffset.Now + TimeSpan.FromMinutes(_intervalMin);
			ApiStatus = ex.Message;
			Log.Write("claude api: " + ex.Message);
			return null;
		}
	}

	// Ответ бывает в двух видах: старый (five_hour / seven_day: utilization, resets_at) и новый
	// (limits: [{ kind: session | weekly_all | weekly_scoped, percent | utilization, resets_at }]).
	public static Snapshot? Parse(JsonElement root)
	{
		(float, DateTimeOffset?)? five = Window(root, "five_hour");
		(float, DateTimeOffset?)? week = Window(root, "seven_day");
		if (root.TryGetProperty("limits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement l in limits.EnumerateArray())
			{
				string? kind = l.TryGetProperty("kind", out JsonElement k) ? k.GetString() : null;
				if (kind == "session")
				{
					five ??= Limit(l);
				}
				else if (kind == "weekly_all")
				{
					week ??= Limit(l);
				}
			}
		}
		if (five == null && week == null)
		{
			return null;
		}
		return new Snapshot(five?.Item1 ?? float.NaN, week?.Item1 ?? float.NaN, five?.Item2, week?.Item2, DateTimeOffset.Now, "api");
	}

	private static (float, DateTimeOffset?)? Window(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out JsonElement w) || w.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		return Limit(w);
	}

	private static (float, DateTimeOffset?)? Limit(JsonElement l)
	{
		float? pct = null;
		foreach (string name in new[] { "utilization", "percent", "percentage" })
		{
			if (l.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
			{
				pct = (float)v.GetDouble();
				break;
			}
		}
		if (pct == null)
		{
			return null;
		}
		DateTimeOffset? reset = null;
		if (l.TryGetProperty("resets_at", out JsonElement r))
		{
			if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t))
			{
				reset = t;
			}
			else if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out long unix))
			{
				reset = DateTimeOffset.FromUnixTimeSeconds(unix);
			}
		}
		return (pct.Value, reset);
	}

	// Организация и почта аккаунта токена; запрос — только когда сменился токен (или раз в 10 мин после ошибки).
	// true — аккаунт сменился.
	private bool EnsureAccount(string token)
	{
		if (token == _profileToken || DateTimeOffset.Now < _profileRetryAt)
		{
			return false;
		}
		try
		{
			using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
			using HttpRequestMessage req = new(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
			req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
			req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
			req.Headers.UserAgent.ParseAdd("PulseBar/" + Updater.Current);
			using HttpResponseMessage resp = http.Send(req);
			if (!resp.IsSuccessStatusCode)
			{
				_profileRetryAt = DateTimeOffset.Now + TimeSpan.FromMinutes(10);
				Log.Write($"claude profile: {(int)resp.StatusCode}");
				return false;
			}
			using JsonDocument doc = JsonDocument.Parse(resp.Content.ReadAsStream());
			string? org = doc.RootElement.TryGetProperty("organization", out JsonElement o) && o.TryGetProperty("uuid", out JsonElement u) ? u.GetString() : null;
			string email = "";
			if (doc.RootElement.TryGetProperty("account", out JsonElement a))
			{
				email = (a.TryGetProperty("email", out JsonElement e) ? e.GetString() : (a.TryGetProperty("email_address", out JsonElement e2) ? e2.GetString() : null)) ?? "";
			}
			_profileToken = token;
			if (org == null || (org == _org && email == Account))
			{
				return false;
			}
			bool changed = org != _org;
			_org = org;
			Account = email;
			SaveAccount();
			Log.Write(changed ? "claude: account changed" : "claude: account e-mail updated");
			return changed;
		}
		catch (Exception ex)
		{
			_profileRetryAt = DateTimeOffset.Now + TimeSpan.FromMinutes(10);
			Log.Write("claude profile: " + ex.Message);
			return false;
		}
	}

	// Почта аккаунта входа приложения Claude — для подписи «Аккаунт»; запрос — когда сменился токен (или раз в 10 мин
	// после ошибки).
	private void AppProfile(string token)
	{
		if (token == _appProfileToken || DateTimeOffset.Now < _appProfileRetryAt)
		{
			return;
		}
		try
		{
			using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
			using HttpRequestMessage req = new(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
			req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
			req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
			req.Headers.UserAgent.ParseAdd("PulseBar/" + Updater.Current);
			using HttpResponseMessage resp = http.Send(req);
			if (!resp.IsSuccessStatusCode)
			{
				_appProfileRetryAt = DateTimeOffset.Now + TimeSpan.FromMinutes(10);
				Log.Write($"claude app profile: {(int)resp.StatusCode}");
				return;
			}
			using JsonDocument doc = JsonDocument.Parse(resp.Content.ReadAsStream());
			string email = "";
			if (doc.RootElement.TryGetProperty("account", out JsonElement a))
			{
				email = (a.TryGetProperty("email", out JsonElement e) ? e.GetString() : (a.TryGetProperty("email_address", out JsonElement e2) ? e2.GetString() : null)) ?? "";
			}
			_appProfileToken = token;
			_appEmail = email;
		}
		catch (Exception ex)
		{
			_appProfileRetryAt = DateTimeOffset.Now + TimeSpan.FromMinutes(10);
			Log.Write("claude app profile: " + ex.Message);
		}
	}

	private void LoadAccount()
	{
		try
		{
			if (File.Exists(AccountCache))
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AccountCache));
				_org = doc.RootElement.GetProperty("org").GetString();
				Account = doc.RootElement.GetProperty("email").GetString() ?? "";
			}
		}
		catch (Exception ex)
		{
			Log.Write("claude account cache: " + ex.Message);
		}
	}

	private void SaveAccount()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(AccountCache)!);
			File.WriteAllText(AccountCache, JsonSerializer.Serialize(new { org = _org, email = Account }));
		}
		catch (Exception ex)
		{
			Log.Write("claude account cache: " + ex.Message);
		}
	}

	// История приложения Claude: {"samples": [{"t": мс, "org": …, "u": {"fh": 5 часов %, "sd": 7 дней %}}, …]} —
	// перечитывается, только когда файл изменился.
	private void ReadHistory()
	{
		if (!File.Exists(History))
		{
			_historyLatestOrg = null;
			_historyByOrg.Clear();
			return;
		}
		DateTime t = File.GetLastWriteTimeUtc(History);
		if (t == _historyTime)
		{
			return;
		}
		using FileStream f = new(History, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using JsonDocument doc = JsonDocument.Parse(f);
		if (!doc.RootElement.TryGetProperty("samples", out JsonElement samples) || samples.ValueKind != JsonValueKind.Array)
		{
			return;
		}
		_historyTime = t;
		_historyByOrg.Clear();
		_series.Clear();
		_historyLatestOrg = null;
		foreach (JsonElement sample in samples.EnumerateArray())
		{
			if (!sample.TryGetProperty("org", out JsonElement o) || o.GetString() is not string org || !sample.TryGetProperty("t", out JsonElement ts) || !sample.TryGetProperty("u", out JsonElement u))
			{
				continue;
			}
			float fh = u.TryGetProperty("fh", out JsonElement a) && a.ValueKind == JsonValueKind.Number ? (float)a.GetDouble() : float.NaN;
			float sd = u.TryGetProperty("sd", out JsonElement b) && b.ValueKind == JsonValueKind.Number ? (float)b.GetDouble() : float.NaN;
			_historyByOrg[org] = (ts.GetInt64(), fh, sd);
			if (!_series.TryGetValue(org, out List<(long T, float Fh, float Sd)>? list))
			{
				list = (_series[org] = new List<(long, float, float)>());
			}
			list.Add((ts.GetInt64(), fh, sd));
			_historyLatestOrg = org;
		}
	}

	private Snapshot? FromHistory(string org)
	{
		if (!_historyByOrg.TryGetValue(org, out var h))
		{
			return null;
		}
		return new Snapshot(h.Fh, h.Sd, null, null, DateTimeOffset.FromUnixTimeMilliseconds(h.T), "claude app");
	}

	public void Dispose()
	{
		_timer.Dispose();
	}
}
