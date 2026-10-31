using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar.Ai;

// Баланс DeepSeek API по ключу пользователя: GET https://api.deepseek.com/user/balance, Authorization: Bearer <ключ>
// (документировано: https://api-docs.deepseek.com/api/get-user-balance). Ответ: {"is_available": bool, "balance_infos":
// [{"currency": "CNY" | "USD", "total_balance": "110.00", "granted_balance", "topped_up_balance"}]} — суммы строками.
internal static class DeepSeekBalance
{
	public const string Id = "deepseek";

	private const string Url = "https://api.deepseek.com/user/balance";

	public static async Task<AiSnapshot> FetchAsync(HttpClient http, string key, Action<TimeSpan> retryAfter, CancellationToken ct)
	{
		key = (key ?? "").Trim();
		if (key.Length == 0)
		{
			return AiUsage.Fail(Id, "api", "no API key");
		}
		// Ключ вводит пользователь: посторонние символы (перевод строки, кириллица) — неверный ключ, в сеть не идём.
		if (!key.All(c => c > ' ' && c < '\x7f'))
		{
			return AiUsage.Fail(Id, "api", "invalid API key");
		}
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, Url);
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		(JsonDocument? doc, string status) = await AiUsage.GetJsonAsync(http, req, retryAfter, ct).ConfigureAwait(false);
		if (doc == null)
		{
			// 401 у DeepSeek — неверный ключ.
			return AiUsage.Fail(Id, "api", status == "HTTP 401" ? "invalid API key" : status);
		}
		using (doc)
		{
			return ParseBalance(doc.RootElement) ?? AiUsage.Fail(Id, "api", "unknown response format");
		}
	}

	// Balance — total_balance записи в USD, если она есть, иначе первой записи. is_available: false — "not available"
	// (баланса не хватает на запросы), баланс при этом показывается.
	public static AiSnapshot? ParseBalance(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("balance_infos", out JsonElement infos) || infos.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		JsonElement? pick = null;
		foreach (JsonElement b in infos.EnumerateArray())
		{
			if (b.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			pick ??= b;
			if (string.Equals(AiUsage.Str(b, "currency"), "USD", StringComparison.OrdinalIgnoreCase))
			{
				pick = b;
				break;
			}
		}
		bool available = !root.TryGetProperty("is_available", out JsonElement a) || a.ValueKind != JsonValueKind.False;
		decimal? total = null;
		string currency = "";
		if (pick is JsonElement p)
		{
			total = Amount(p, "total_balance");
			currency = AiUsage.Str(p, "currency") ?? "";
		}
		return new AiSnapshot(Id, float.NaN, float.NaN, null, null, total, currency, DateTimeOffset.Now, "api", available ? "" : "not available");
	}

	private static decimal? Amount(JsonElement o, string name)
	{
		if (!o.TryGetProperty(name, out JsonElement v))
		{
			return null;
		}
		if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out decimal n))
		{
			return n;
		}
		if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal s))
		{
			return s;
		}
		return null;
	}
}
