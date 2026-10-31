using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar.Ai;

// Кредиты Grok CLI (SuperGrok): сколько израсходовано за период и когда сброс.
// Токен — вход Grok CLI: $GROK_HOME\auth.json или ~\.grok\auth.json. Файл — словарь по адресам OIDC; берётся запись
// "https://auth.x.ai::<client-id>", иначе "https://accounts.x.ai/sign-in"; в записи key (Bearer) и expires_at (ISO 8601).
// Только читаем: токен (~7 дней) продлевает сам `grok login`.
// GET https://cli-chat-proxy.grok.com/v1/billing?format=credits — недокументированный эндпоинт Grok CLI, заголовки
// Authorization: Bearer и x-xai-token-auth: xai-grok-cli. Ответ: {"config": {"creditUsagePercent", "currentPeriod":
// {"start", "end"}, "billingPeriodEnd", "onDemandCap": {"val"}, "onDemandUsed": {"val"}, …}}; без creditUsagePercent —
// onDemandUsed / onDemandCap. Формат — по CodexBar: docs/grok.md и Sources/CodexBarCore/Providers/Grok/
// GrokCreditsProxyFetcher.swift, GrokAuth.swift (github.com/steipete/CodexBar).
internal static class GrokUsage
{
	public const string Id = "grok";

	public const string Source = "grok cli";

	private const string Url = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";

	private static string Home
	{
		get
		{
			string? custom = Environment.GetEnvironmentVariable("GROK_HOME");
			return string.IsNullOrWhiteSpace(custom) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok") : custom;
		}
	}

	public static async Task<AiSnapshot> FetchAsync(HttpClient http, Action<TimeSpan> retryAfter, CancellationToken ct)
	{
		string file = Path.Combine(Home, "auth.json");
		if (!File.Exists(file))
		{
			return AiUsage.Fail(Id, Source, "not logged in to Grok CLI");
		}
		string? token;
		DateTimeOffset? expires;
		using (JsonDocument auth = JsonDocument.Parse(AiUsage.ReadShared(file)))
		{
			token = ParseAuth(auth.RootElement, out expires);
		}
		if (token == null)
		{
			return AiUsage.Fail(Id, Source, "not logged in to Grok CLI");
		}
		if (expires <= DateTimeOffset.Now)
		{
			return AiUsage.Fail(Id, Source, "token expired");
		}
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, Url);
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		req.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
		(JsonDocument? doc, string status) = await AiUsage.GetJsonAsync(http, req, retryAfter, ct).ConfigureAwait(false);
		if (doc == null)
		{
			return AiUsage.Fail(Id, Source, status);
		}
		using (doc)
		{
			return ParseBilling(doc.RootElement) ?? AiUsage.Fail(Id, Source, "unknown response format");
		}
	}

	// Токен записи OIDC (SuperGrok), иначе старой записи входа; записи без key пропускаются. null — токена нет.
	internal static string? ParseAuth(JsonElement root, out DateTimeOffset? expires)
	{
		expires = null;
		if (root.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		JsonElement? oidc = null;
		JsonElement? legacy = null;
		foreach (JsonProperty e in root.EnumerateObject())
		{
			if (string.IsNullOrEmpty(AiUsage.Str(e.Value, "key")))
			{
				continue;
			}
			if (e.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal))
			{
				oidc = e.Value;
			}
			else if (e.Name.Contains("/sign-in", StringComparison.Ordinal))
			{
				legacy = e.Value;
			}
		}
		if ((oidc ?? legacy) is not JsonElement entry)
		{
			return null;
		}
		expires = AiUsage.Date(entry, "expires_at");
		return AiUsage.Str(entry, "key");
	}

	// Ответ billing?format=credits. First — % израсходованных кредитов, FirstReset — конец текущего периода
	// (иначе расчётного). Без процента, но с периодом — процент неизвестен (NaN): так отвечают тарифы, которые его
	// не публикуют. null — не тот формат.
	public static AiSnapshot? ParseBilling(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		JsonElement c = root.TryGetProperty("config", out JsonElement config) && config.ValueKind == JsonValueKind.Object ? config : root;
		double? pct = AiUsage.Number(c, "creditUsagePercent");
		if (pct is not double v || !double.IsFinite(v))
		{
			pct = null;
			if (Val(c, "onDemandCap") is double cap && cap > 0 && Val(c, "onDemandUsed") is double used)
			{
				pct = used / cap * 100;
			}
		}
		DateTimeOffset? reset = null;
		if (c.TryGetProperty("currentPeriod", out JsonElement period))
		{
			reset = AiUsage.Date(period, "end");
		}
		reset ??= AiUsage.Date(c, "billingPeriodEnd");
		if (pct == null && reset == null)
		{
			return null;
		}
		return new AiSnapshot(Id, pct.HasValue ? AiUsage.Percent(pct.Value) : float.NaN, float.NaN, reset, null, null, "", DateTimeOffset.Now, Source, "");
	}

	// Суммы в ответе — {"val": число или строка}.
	private static double? Val(JsonElement o, string name)
	{
		return o.TryGetProperty(name, out JsonElement v) ? AiUsage.Number(v, "val") : null;
	}
}
