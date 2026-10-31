using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PulseBar.Ai;

namespace PulseBar.Tests;

// Плитки ИИ: разбор ответов ChatGPT (Codex), Grok CLI, DeepSeek и журналов Codex на образцах из документации
// (Data\ai\, без настоящих токенов), DPAPI, слияние показаний. Сеть не трогается.
[TestClass]
public class AiTests
{
	private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Data", "ai", name);

	private static JsonDocument Json(string name) => JsonDocument.Parse(File.ReadAllText(DataPath(name)));

	private static string[] SessionLines() => File.ReadAllLines(DataPath("codex-session.jsonl"));

	private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

	private static string FakeJwt(DateTimeOffset exp)
	{
		static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		return B64("{\"alg\":\"none\"}") + "." + B64("{\"exp\":" + exp.ToUnixTimeSeconds() + ",\"sub\":\"test\"}") + ".sig";
	}

	[TestMethod]
	public void CodexUsageParsesBothWindows()
	{
		using JsonDocument doc = Json("codex-usage.json");
		AiSnapshot s = CodexUsage.ParseUsage(doc.RootElement)!;
		Assert.AreEqual("chatgpt", s.Id);
		Assert.AreEqual(23f, s.First);
		Assert.AreEqual(41f, s.Second);
		Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1793520000), s.FirstReset);
		Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1793865600), s.SecondReset);
		Assert.AreEqual("api", s.Source);
		Assert.AreEqual("", s.Status);
		Assert.IsNull(s.Balance);
	}

	[TestMethod]
	public void CodexUsageWithoutResetAtUsesResetAfter()
	{
		using JsonDocument doc = JsonDocument.Parse("{\"plan_type\":\"free\",\"rate_limit\":{\"allowed\":true,\"limit_reached\":false,\"primary_window\":{\"used_percent\":150,\"reset_after_seconds\":3600},\"secondary_window\":null}}");
		DateTimeOffset before = DateTimeOffset.Now;
		AiSnapshot s = CodexUsage.ParseUsage(doc.RootElement)!;
		Assert.AreEqual(100f, s.First, "проценты ограничены 0–100");
		Assert.IsTrue(float.IsNaN(s.Second));
		Assert.IsNull(s.SecondReset);
		Assert.IsTrue(s.FirstReset >= before.AddSeconds(3599) && s.FirstReset <= DateTimeOffset.Now.AddSeconds(3601));
	}

	[TestMethod]
	public void CodexUsageRejectsUnknownFormat()
	{
		using JsonDocument other = JsonDocument.Parse("{\"detail\":\"Unauthorized\"}");
		Assert.IsNull(CodexUsage.ParseUsage(other.RootElement));
		// Тариф без лимитов: формат верный, процентов нет.
		using JsonDocument none = JsonDocument.Parse("{\"plan_type\":\"guest\",\"rate_limit\":null}");
		AiSnapshot s = CodexUsage.ParseUsage(none.RootElement)!;
		Assert.IsTrue(float.IsNaN(s.First) && float.IsNaN(s.Second));
		Assert.AreEqual("", s.Status);
	}

	[TestMethod]
	public void CodexSessionLineNewFormat()
	{
		AiSnapshot s = CodexUsage.ParseSessionLine(SessionLines()[5])!;
		Assert.AreEqual(12.5f, s.First);
		Assert.AreEqual(23f, s.Second);
		Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1793520000), s.FirstReset);
		Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1793865600), s.SecondReset);
		Assert.AreEqual(Utc("2026-11-01T06:04:00Z"), s.Updated);
		Assert.AreEqual("codex logs", s.Source);
	}

	[TestMethod]
	public void CodexSessionLineOldFormatCountsFromTimestamp()
	{
		AiSnapshot s = CodexUsage.ParseSessionLine(SessionLines()[2])!;
		DateTimeOffset t = Utc("2026-11-01T06:01:00Z");
		Assert.AreEqual(10f, s.First);
		Assert.AreEqual(22f, s.Second);
		Assert.AreEqual(t.AddSeconds(17940), s.FirstReset);
		Assert.AreEqual(t.AddSeconds(351406), s.SecondReset);
	}

	[TestMethod]
	public void CodexSessionLineSkipsOtherLines()
	{
		string[] lines = SessionLines();
		Assert.IsNull(CodexUsage.ParseSessionLine(lines[0]), "session_meta");
		Assert.IsNull(CodexUsage.ParseSessionLine(lines[1]), "rate_limits: null");
		Assert.IsNull(CodexUsage.ParseSessionLine(lines[3]), "слова в тексте ответа");
		Assert.IsNull(CodexUsage.ParseSessionLine(lines[4]), "лимит другой модели");
		Assert.IsNull(CodexUsage.ParseSessionLine(lines[6]), "agent_message");
		Assert.IsNull(CodexUsage.ParseSessionLine("{\"token_count\":\"rate_limits\" — обрезанная строка"));
		Assert.IsNull(CodexUsage.ParseSessionLine(""));
	}

	[TestMethod]
	public void CodexFallbackTakesLastEventOfNewestFile()
	{
		string root = Path.Combine(Path.GetTempPath(), "PulseBarTests", Guid.NewGuid().ToString("N"), "sessions");
		try
		{
			string oldDir = Path.Combine(root, "2026", "10", "31");
			string newDir = Path.Combine(root, "2026", "11", "01");
			Directory.CreateDirectory(oldDir);
			Directory.CreateDirectory(newDir);
			string[] lines = SessionLines();
			string oldFile = Path.Combine(oldDir, "rollout-old.jsonl");
			File.WriteAllText(oldFile, lines[2] + "\n");
			File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-1));
			string newFile = Path.Combine(newDir, "rollout-new.jsonl");
			File.Copy(DataPath("codex-session.jsonl"), newFile);
			File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);
			File.WriteAllText(Path.Combine(newDir, "notes.txt"), lines[2]);
			AiSnapshot s = CodexUsage.FromSessions(root)!;
			Assert.AreEqual(12.5f, s.First);
			Assert.AreEqual(Utc("2026-11-01T06:04:00Z"), s.Updated);
			// В самом новом файле нет лимитов — берётся следующий.
			string empty = Path.Combine(newDir, "rollout-empty.jsonl");
			File.WriteAllText(empty, lines[0] + "\n" + lines[1] + "\n");
			File.SetLastWriteTimeUtc(empty, DateTime.UtcNow.AddMinutes(1));
			Assert.AreEqual(12.5f, CodexUsage.FromSessions(root)!.First);
			Assert.IsNull(CodexUsage.FromSessions(Path.Combine(root, "missing")));
		}
		finally
		{
			Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
		}
	}

	[TestMethod]
	public void CodexAuthAndJwtExpiry()
	{
		using JsonDocument auth = JsonDocument.Parse("{\"OPENAI_API_KEY\":null,\"tokens\":{\"id_token\":\"x\",\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"account_id\":\"acc-test\"},\"last_refresh\":\"2026-10-30T00:00:00Z\"}");
		Assert.AreEqual("test-access", CodexUsage.ParseAuth(auth.RootElement, out string? account));
		Assert.AreEqual("acc-test", account);
		using JsonDocument apiKey = JsonDocument.Parse("{\"OPENAI_API_KEY\":\"sk-test\",\"tokens\":null}");
		Assert.IsNull(CodexUsage.ParseAuth(apiKey.RootElement, out _), "вход ключом API — лимитов подписки нет");
		Assert.IsTrue(CodexUsage.JwtExpired(FakeJwt(DateTimeOffset.Now.AddMinutes(-1))));
		Assert.IsFalse(CodexUsage.JwtExpired(FakeJwt(DateTimeOffset.Now.AddDays(1))));
		Assert.IsFalse(CodexUsage.JwtExpired("not-a-jwt"));
		Assert.IsFalse(CodexUsage.JwtExpired("a.!!!.c"));
	}

	[TestMethod]
	public void GrokBillingParsesCredits()
	{
		using JsonDocument doc = Json("grok-billing.json");
		AiSnapshot s = GrokUsage.ParseBilling(doc.RootElement)!;
		Assert.AreEqual("grok", s.Id);
		Assert.AreEqual(12.5f, s.First);
		Assert.AreEqual(Utc("2026-08-13T00:00:00Z"), s.FirstReset, "конец текущего периода важнее расчётного");
		Assert.IsTrue(float.IsNaN(s.Second));
		Assert.AreEqual("grok cli", s.Source);
		Assert.AreEqual("", s.Status);
	}

	[TestMethod]
	public void GrokBillingFallbacks()
	{
		// Без creditUsagePercent — onDemandUsed / onDemandCap (суммы бывают строками); без currentPeriod — billingPeriodEnd.
		using JsonDocument ratio = JsonDocument.Parse("{\"config\":{\"billingPeriodEnd\":\"2026-09-01T00:00:00Z\",\"onDemandCap\":{\"val\":\"1000\"},\"onDemandUsed\":{\"val\":250}}}");
		AiSnapshot a = GrokUsage.ParseBilling(ratio.RootElement)!;
		Assert.AreEqual(25f, a.First);
		Assert.AreEqual(Utc("2026-09-01T00:00:00Z"), a.FirstReset);
		// Только период — процент неизвестен.
		using JsonDocument period = JsonDocument.Parse("{\"config\":{\"currentPeriod\":{\"start\":\"2026-08-06T00:00:00Z\",\"end\":\"2026-08-13T00:00:00Z\"}}}");
		AiSnapshot b = GrokUsage.ParseBilling(period.RootElement)!;
		Assert.IsTrue(float.IsNaN(b.First));
		Assert.AreEqual(Utc("2026-08-13T00:00:00Z"), b.FirstReset);
		using JsonDocument other = JsonDocument.Parse("{\"error\":\"unauthenticated\"}");
		Assert.IsNull(GrokUsage.ParseBilling(other.RootElement));
	}

	[TestMethod]
	public void GrokAuthPrefersOidcEntry()
	{
		using JsonDocument doc = Json("grok-auth.json");
		Assert.AreEqual("test-oidc-key", GrokUsage.ParseAuth(doc.RootElement, out DateTimeOffset? expires));
		Assert.AreEqual(Utc("2026-11-08T12:00:00Z"), expires);
		using JsonDocument legacy = JsonDocument.Parse("{\"https://accounts.x.ai/sign-in\":{\"key\":\"test-legacy-key\"}}");
		Assert.AreEqual("test-legacy-key", GrokUsage.ParseAuth(legacy.RootElement, out expires));
		Assert.IsNull(expires);
		using JsonDocument none = JsonDocument.Parse("{}");
		Assert.IsNull(GrokUsage.ParseAuth(none.RootElement, out _));
	}

	[TestMethod]
	public void DeepSeekBalanceFromDocs()
	{
		using JsonDocument doc = Json("deepseek-balance.json");
		AiSnapshot s = DeepSeekBalance.ParseBalance(doc.RootElement)!;
		Assert.AreEqual("deepseek", s.Id);
		Assert.AreEqual(110.00m, s.Balance);
		Assert.AreEqual("CNY", s.Currency);
		Assert.AreEqual("", s.Status);
		Assert.IsTrue(float.IsNaN(s.First) && float.IsNaN(s.Second));
	}

	[TestMethod]
	public void DeepSeekPrefersUsdAndReportsUnavailable()
	{
		using JsonDocument doc = JsonDocument.Parse("{\"is_available\":false,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"7.10\"},{\"currency\":\"USD\",\"total_balance\":\"0.95\"}]}");
		AiSnapshot s = DeepSeekBalance.ParseBalance(doc.RootElement)!;
		Assert.AreEqual(0.95m, s.Balance);
		Assert.AreEqual("USD", s.Currency);
		Assert.AreEqual("not available", s.Status);
		using JsonDocument other = JsonDocument.Parse("{\"error\":{\"message\":\"Authentication Fails\"}}");
		Assert.IsNull(DeepSeekBalance.ParseBalance(other.RootElement));
	}

	[TestMethod]
	public void DeepSeekBadKeyStaysOffline()
	{
		// Тайм-аут 1 мс: случайный запрос упал бы, а не дошёл до DeepSeek.
		using System.Net.Http.HttpClient http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(1) };
		Assert.AreEqual("no API key", DeepSeekBalance.FetchAsync(http, "  ", _ => { }, CancellationToken.None).Result.Status);
		Assert.AreEqual("invalid API key", DeepSeekBalance.FetchAsync(http, "sk-тест", _ => { }, CancellationToken.None).Result.Status);
		Assert.AreEqual("invalid API key", DeepSeekBalance.FetchAsync(http, "sk-a\nb", _ => { }, CancellationToken.None).Result.Status);
	}

	[TestMethod]
	public void SecretRoundTrip()
	{
		const string key = "sk-test-0123456789abcdef-ключ";
		string stored = Secret.Protect(key);
		Assert.IsFalse(string.IsNullOrEmpty(stored));
		Assert.IsFalse(stored.Contains("sk-test"));
		Assert.AreEqual(key, Secret.Unprotect(stored));
		Assert.AreNotEqual(stored, Secret.Protect(key), "DPAPI солит каждый вызов");
		Assert.AreEqual("", Secret.Protect(""));
		Assert.AreEqual("", Secret.Unprotect(""));
		Assert.AreEqual("", Secret.Unprotect("not base64!"));
		Assert.AreEqual("", Secret.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
	}

	[TestMethod]
	public void MergeKeepsDataOnTransientErrors()
	{
		DateTimeOffset now = DateTimeOffset.Now;
		AiSnapshot good = new AiSnapshot("chatgpt", 30, 40, now.AddHours(1), now.AddDays(2), null, "", now.AddMinutes(-1), "api", "");
		AiSnapshot e429 = AiUsage.Fail("chatgpt", "api", "HTTP 429");
		AiSnapshot kept = AiUsage.Merge(good, e429);
		Assert.AreEqual(30f, kept.First);
		Assert.AreEqual("HTTP 429", kept.Status);
		Assert.AreEqual(good.Updated, kept.Updated, "время прежнее — через 15 минут показание бледнеет");
		// Ошибка входа стирает цифры: они могли принадлежать другому аккаунту.
		Assert.IsTrue(float.IsNaN(AiUsage.Merge(good, AiUsage.Fail("chatgpt", "api", "HTTP 401")).First));
		// Журнал Codex старше ответа API — остаётся ответ API.
		AiSnapshot logs = good with { First = 5, Updated = now.AddMinutes(-30), Source = "codex logs" };
		Assert.AreEqual(30f, AiUsage.Merge(good, logs).First);
		AiSnapshot fresh = good with { First = 35, Updated = now };
		Assert.AreEqual(35f, AiUsage.Merge(good, fresh).First);
	}

	[TestMethod]
	public void ExpireClearsPassedWindows()
	{
		DateTimeOffset now = DateTimeOffset.Now;
		AiSnapshot s = new AiSnapshot("chatgpt", 30, 40, now.AddMinutes(-1), now.AddDays(2), null, "", now.AddHours(-1), "codex logs", "");
		AiSnapshot e = AiUsage.Expire(s);
		Assert.IsTrue(float.IsNaN(e.First));
		Assert.IsNull(e.FirstReset);
		Assert.AreEqual(40f, e.Second);
		AiSnapshot untouched = s with { FirstReset = null };
		Assert.AreSame(untouched, AiUsage.Expire(untouched));
	}

	[TestMethod]
	public void ManagerReportsMissingKeyWithoutNetwork()
	{
		// Включён только DeepSeek без ключа — в сеть никто не ходит.
		using ManualResetEventSlim changed = new ManualResetEventSlim();
		using AiUsage ai = new AiUsage(id => id == "deepseek", () => "");
		ai.Changed += () => changed.Set();
		ai.PollNow();
		Assert.IsTrue(changed.Wait(TimeSpan.FromSeconds(10)));
		Assert.AreEqual(1, ai.Current.Count);
		AiSnapshot s = ai.Current["deepseek"];
		Assert.AreEqual("no API key", s.Status);
		Assert.IsNull(s.Balance);
		Assert.IsFalse(ai.Current.Keys.Any(k => k != "deepseek"));
	}
}
