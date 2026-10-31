using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace PulseBar;

// Лимиты подписки Claude (5 часов и 7 дней), как на claude.ai.
// Аккаунт — тот, в который вошёл Claude Code (~\.claude\.credentials.json); его организацию узнаём один раз на токен
// через api/oauth/profile и запоминаем в %LOCALAPPDATA%\PulseBar\claude-account.json.
// Два источника, берётся более свежий:
//  - %APPDATA%\Claude\plan-usage-history.json — приложение Claude пишет туда проценты примерно раз в 15 мин;
//    токен не нужен, лимитов на запросы нет, но нет времени сброса. Приложение может быть в другом аккаунте или
//    организации (у каждой записи есть "org"), поэтому берутся только записи организации Claude Code;
//  - GET https://api.anthropic.com/api/oauth/usage с токеном входа Claude Code (~\.claude\.credentials.json).
//    Эндпоинт недокументирован и режет частые запросы (429 с Retry-After до часа), поэтому опрос редкий,
//    Retry-After соблюдается, токен не продлевается — это делает сам Claude Code. Квота не тратится.
internal sealed class ClaudeUsage : IDisposable
{
	public sealed record Snapshot(float FiveHour, float SevenDay, DateTimeOffset? FiveHourReset, DateTimeOffset? SevenDayReset,
		DateTimeOffset Updated, string Source)
	{
		// Старше 20 минут — показываем бледным: приложение Claude обновляет историю раз в ~15 минут.
		public bool Stale => DateTimeOffset.Now - Updated > TimeSpan.FromMinutes(20);
	}

	private static readonly string Credentials = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

	private static readonly string History = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");

	private static readonly string AccountCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", "claude-account.json");

	private string? _profileToken;

	private DateTimeOffset _profileRetryAt = DateTimeOffset.MinValue;

	private volatile string? _org;

	// Почта аккаунта Claude Code (пусто — ещё не известна).
	public volatile string Account = "";

	private readonly Timer _timer;

	private int _intervalMin;

	private DateTimeOffset _nextApi = DateTimeOffset.MinValue;

	private int _busy;

	public volatile Snapshot? Current;

	// Последняя причина, почему нет свежих данных из API (для подсказки и журнала).
	public volatile string ApiStatus = "";

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
			if (!loggedIn)
			{
				next = null;
			}
			else
			{
				if (_api && token != null && EnsureAccount(token))
				{
					next = null; // данные прежнего аккаунта больше не подходят
				}
				if (_api && token != null && DateTimeOffset.Now >= _nextApi)
				{
					next = Newer(next, FromApi(token)) ?? next;
				}
				if (_org != null)
				{
					next = Newer(next, FromHistory(next, _org));
				}
			}
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
			// Время сброса в истории нет — оставить известное из API, пока оно не прошло.
			return b with
			{
				FiveHourReset = b.FiveHourReset ?? Future(a?.FiveHourReset),
				SevenDayReset = b.SevenDayReset ?? Future(a?.SevenDayReset)
			};
		}
		return a;
	}

	private static DateTimeOffset? Future(DateTimeOffset? t) => t > DateTimeOffset.Now ? t : null;

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

	// Последняя запись истории приложения Claude для организации org: {"t": мс, "org": …, "u": {"fh": 5 часов %, "sd": 7 дней %}}.
	private static Snapshot? FromHistory(Snapshot? known, string org)
	{
		if (!File.Exists(History))
		{
			return null;
		}
		DateTime modified = File.GetLastWriteTimeUtc(History);
		if (known != null && modified <= known.Updated.UtcDateTime)
		{
			return null;
		}
		using FileStream f = new(History, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using JsonDocument doc = JsonDocument.Parse(f);
		if (!doc.RootElement.TryGetProperty("samples", out JsonElement samples) || samples.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		JsonElement last = default;
		for (int i = samples.GetArrayLength() - 1; i >= 0; i--)
		{
			if (samples[i].TryGetProperty("org", out JsonElement o) && o.GetString() == org)
			{
				last = samples[i];
				break;
			}
		}
		if (last.ValueKind != JsonValueKind.Object || !last.TryGetProperty("t", out JsonElement t) || !last.TryGetProperty("u", out JsonElement u))
		{
			return null;
		}
		float fh = u.TryGetProperty("fh", out JsonElement a) && a.ValueKind == JsonValueKind.Number ? (float)a.GetDouble() : float.NaN;
		float sd = u.TryGetProperty("sd", out JsonElement b) && b.ValueKind == JsonValueKind.Number ? (float)b.GetDouble() : float.NaN;
		return new Snapshot(fh, sd, null, null, DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()), "claude app");
	}

	public void Dispose()
	{
		_timer.Dispose();
	}
}
