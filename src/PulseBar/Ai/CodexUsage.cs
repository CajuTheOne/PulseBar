using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar.Ai;

// Лимиты подписки ChatGPT в Codex: 5 часов (primary) и неделя (secondary), как /status в Codex CLI.
// Токен — вход Codex CLI: $CODEX_HOME\auth.json или ~\.codex\auth.json, {"tokens": {"access_token", "account_id", …}}
// (openai/codex, codex-rs/login/src/auth/storage.rs). Только читаем: токен продлевает сам Codex CLI.
// Два источника:
//  - GET https://chatgpt.com/backend-api/wham/usage — недокументированный эндпоинт, им пользуется сам Codex CLI
//    (codex-rs/backend-client/src/client.rs: Bearer + заголовок ChatGPT-Account-Id) и CodexBar (docs/codex.md).
//    Ответ — RateLimitStatusPayload (codex-rs/codex-backend-openapi-models): {"plan_type", "rate_limit": {"primary_window",
//    "secondary_window": {"used_percent", "limit_window_seconds", "reset_after_seconds", "reset_at" (Unix, с)}}}.
//  - запасной: журналы сессий $CODEX_HOME\sessions\ГГГГ\ММ\ДД\*.jsonl. На каждый ход Codex CLI пишет
//    {"timestamp", "type": "event_msg", "payload": {"type": "token_count", "rate_limits": {"limit_id", "primary",
//    "secondary": {"used_percent", "window_minutes", "resets_at" (Unix, с)}}}} (codex-rs/protocol/src/protocol.rs).
//    Старые версии вместо resets_at писали resets_in_seconds — от timestamp строки (github.com/xiangz19/codex-ratelimit).
internal static class CodexUsage
{
	public const string Id = "chatgpt";

	private const string Url = "https://chatgpt.com/backend-api/wham/usage";

	// Последняя причина сбоя API — в журнал только при смене (при запасном источнике Status пустой).
	private static string _apiStatus = "";

	private static string Home
	{
		get
		{
			string? custom = Environment.GetEnvironmentVariable("CODEX_HOME");
			return string.IsNullOrWhiteSpace(custom) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : custom;
		}
	}

	public static async Task<AiSnapshot> FetchAsync(HttpClient http, Action<TimeSpan> retryAfter, CancellationToken ct)
	{
		AiSnapshot api;
		try
		{
			api = await FromApiAsync(http, retryAfter, ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (!ct.IsCancellationRequested)
		{
			api = AiUsage.Fail(Id, "api", AiUsage.Reason(ex));
		}
		if (api.Status != _apiStatus)
		{
			_apiStatus = api.Status;
			if (api.Status.Length > 0)
			{
				Log.Write("ai: codex api: " + api.Status);
			}
		}
		if (api.Status.Length == 0)
		{
			return api;
		}
		try
		{
			return FromSessions(Path.Combine(Home, "sessions")) ?? api;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Log.Write("ai: codex logs: " + ex.Message);
			return api;
		}
	}

	private static async Task<AiSnapshot> FromApiAsync(HttpClient http, Action<TimeSpan> retryAfter, CancellationToken ct)
	{
		string file = Path.Combine(Home, "auth.json");
		if (!File.Exists(file))
		{
			return AiUsage.Fail(Id, "api", "not logged in to Codex");
		}
		string? token;
		string? account;
		using (JsonDocument auth = JsonDocument.Parse(AiUsage.ReadShared(file)))
		{
			token = ParseAuth(auth.RootElement, out account);
		}
		// Нет токенов — не входили или вошли ключом API (у ключа нет лимитов подписки).
		if (token == null)
		{
			return AiUsage.Fail(Id, "api", "not logged in to Codex");
		}
		if (JwtExpired(token))
		{
			return AiUsage.Fail(Id, "api", "token expired");
		}
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, Url);
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		if (account != null)
		{
			req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
		}
		(JsonDocument? doc, string status) = await AiUsage.GetJsonAsync(http, req, retryAfter, ct).ConfigureAwait(false);
		if (doc == null)
		{
			return AiUsage.Fail(Id, "api", status);
		}
		using (doc)
		{
			return ParseUsage(doc.RootElement) ?? AiUsage.Fail(Id, "api", "unknown response format");
		}
	}

	// tokens.access_token и tokens.account_id; null — токенов нет.
	internal static string? ParseAuth(JsonElement root, out string? accountId)
	{
		accountId = null;
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tokens", out JsonElement t) || t.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		string? token = AiUsage.Str(t, "access_token");
		if (string.IsNullOrEmpty(token))
		{
			return null;
		}
		accountId = AiUsage.Str(t, "account_id") is { Length: > 0 } a ? a : null;
		return token;
	}

	// access_token — JWT; поле exp полезной нагрузки показывает, что токен истёк, без запроса в сеть.
	internal static bool JwtExpired(string jwt)
	{
		string[] parts = jwt.Split('.');
		if (parts.Length != 3)
		{
			return false;
		}
		try
		{
			string p = parts[1].Replace('-', '+').Replace('_', '/');
			p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
			using JsonDocument d = JsonDocument.Parse(Convert.FromBase64String(p));
			return AiUsage.Number(d.RootElement, "exp") is double exp && DateTimeOffset.FromUnixTimeSeconds((long)exp) <= DateTimeOffset.Now;
		}
		catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is ArgumentOutOfRangeException)
		{
			return false;
		}
	}

	// Ответ wham/usage. null — не тот формат; rate_limit: null (тариф без лимитов) — показание без процентов.
	public static AiSnapshot? ParseUsage(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		DateTimeOffset now = DateTimeOffset.Now;
		(float Used, DateTimeOffset? Reset)? a = null;
		(float Used, DateTimeOffset? Reset)? b = null;
		if (root.TryGetProperty("rate_limit", out JsonElement rl) && rl.ValueKind == JsonValueKind.Object)
		{
			a = ApiWindow(rl, "primary_window", now);
			b = ApiWindow(rl, "secondary_window", now);
		}
		if (a == null && b == null && !root.TryGetProperty("plan_type", out _))
		{
			return null;
		}
		return new AiSnapshot(Id, a?.Used ?? float.NaN, b?.Used ?? float.NaN, a?.Reset, b?.Reset, null, "", now, "api", "");
	}

	private static (float Used, DateTimeOffset? Reset)? ApiWindow(JsonElement rl, string name, DateTimeOffset now)
	{
		if (!rl.TryGetProperty(name, out JsonElement w) || AiUsage.Number(w, "used_percent") is not double used)
		{
			return null;
		}
		DateTimeOffset? reset = AiUsage.Date(w, "reset_at");
		if (reset == null && AiUsage.Number(w, "reset_after_seconds") is double after && after >= 0)
		{
			reset = now.AddSeconds(after);
		}
		return (AiUsage.Percent(used), reset);
	}

	// Строка журнала сессии с лимитами; null — строка не о лимитах.
	public static AiSnapshot? ParseSessionLine(string line)
	{
		if (string.IsNullOrEmpty(line) || !line.Contains("\"token_count\"", StringComparison.Ordinal) || !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
		{
			return null;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			JsonElement root = doc.RootElement;
			if (!root.TryGetProperty("payload", out JsonElement p) || AiUsage.Str(p, "type") != "token_count"
				|| !p.TryGetProperty("rate_limits", out JsonElement rl) || rl.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			// Отдельные лимиты моделей (limit_id не "codex") пропускаем: плитка показывает общий лимит Codex.
			if (AiUsage.Str(rl, "limit_id") is string limit && limit != "codex")
			{
				return null;
			}
			if (AiUsage.Date(root, "timestamp") is not DateTimeOffset t)
			{
				return null;
			}
			(float Used, DateTimeOffset? Reset)? a = LogWindow(rl, "primary", t);
			(float Used, DateTimeOffset? Reset)? b = LogWindow(rl, "secondary", t);
			if (a == null && b == null)
			{
				return null;
			}
			return new AiSnapshot(Id, a?.Used ?? float.NaN, b?.Used ?? float.NaN, a?.Reset, b?.Reset, null, "", t, "codex logs", "");
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static (float Used, DateTimeOffset? Reset)? LogWindow(JsonElement rl, string name, DateTimeOffset t)
	{
		if (!rl.TryGetProperty(name, out JsonElement w) || AiUsage.Number(w, "used_percent") is not double used)
		{
			return null;
		}
		DateTimeOffset? reset = AiUsage.Date(w, "resets_at");
		if (reset == null && AiUsage.Number(w, "resets_in_seconds") is double after && after >= 0)
		{
			reset = t.AddSeconds(after);
		}
		return (AiUsage.Percent(used), reset);
	}

	// Последние лимиты из журналов: самые новые файлы (по времени записи) из последних 8 папок дней, последняя строка
	// token_count с лимитами. Читается только хвост файла — журналы бывают в десятки мегабайт.
	internal static AiSnapshot? FromSessions(string root)
	{
		if (!Directory.Exists(root))
		{
			return null;
		}
		List<string> days = new List<string>();
		foreach (string y in Desc(root))
		{
			foreach (string m in Desc(y))
			{
				foreach (string d in Desc(m))
				{
					days.Add(d);
				}
				if (days.Count >= 8)
				{
					break;
				}
			}
			if (days.Count >= 8)
			{
				break;
			}
		}
		IEnumerable<FileInfo> files = days.Take(8).SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*.jsonl")).OrderByDescending(f => f.LastWriteTimeUtc).Take(10);
		foreach (FileInfo f in files)
		{
			AiSnapshot? s = LastInFile(f.FullName);
			if (s != null)
			{
				return s;
			}
		}
		return null;
	}

	// Папки ГГГГ / ММ / ДД, новые первыми (имена с ведущими нулями — порядок строк совпадает с порядком дат).
	private static IEnumerable<string> Desc(string dir)
	{
		return Directory.EnumerateDirectories(dir).Where(p => Path.GetFileName(p).All(char.IsAsciiDigit)).OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal);
	}

	private static AiSnapshot? LastInFile(string path)
	{
		const int Tail = 2 * 1024 * 1024;
		using FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		long start = Math.Max(0, f.Length - Tail);
		f.Seek(start, SeekOrigin.Begin);
		using StreamReader r = new StreamReader(f, Encoding.UTF8);
		string[] lines = r.ReadToEnd().Split('\n');
		// Первая строка хвоста может быть обрезана — она просто не разберётся.
		for (int i = lines.Length - 1; i >= 0; i--)
		{
			AiSnapshot? s = ParseSessionLine(lines[i].TrimEnd('\r'));
			if (s != null)
			{
				return s;
			}
		}
		return null;
	}
}
