using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseBar.Ai;

// Вход приложения Claude (Claude Desktop) — для лимитов аккаунта, открытого в приложении, прямо с claude.ai, без истории
// приложения (её приложение перестаёт писать, если окно расхода в трее не открывали 24 часа).
// Токены OAuth приложение хранит в %APPDATA%\Claude\config.json ("oauth:tokenCacheV2") зашифрованными, как Chromium:
// "v10", 12 байт nonce, AES-256-GCM; ключ — в "Local State" (os_crypt.encrypted_key: "DPAPI" + ключ под DPAPI текущего
// пользователя Windows). Пользователь разрешил (02.11.2026) читать этот вход, как вход Codex и Grok: токен только читается
// (не продлевается — это делает само приложение), уходит только на api.anthropic.com и нигде не записывается.
// Ключ записи: "acct:<аккаунт>|<клиент>:<организация>:<адрес API>:<права через пробел>".
internal static class ClaudeAppAuth
{
	public sealed record Token(string Account, string Org, string Value, DateTimeOffset Expires);

	private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

	private static readonly object Lock = new object();

	private static DateTime _stamp;

	private static List<Token> _tokens = new List<Token>();

	private static string? _account;

	// Действующий токен с правом user:profile (его требует api/oauth/usage) для аккаунта, открытого в приложении:
	// организации org, если она есть среди его токенов, иначе с самым поздним сроком. null — приложения нет, не вошли
	// или формат хранилища сменился. Файлы перечитываются, только когда config.json изменился.
	public static Token? Pick(string? org)
	{
		lock (Lock)
		{
			Reload();
			DateTimeOffset soon = DateTimeOffset.Now.AddMinutes(1);
			List<Token> live = _tokens.Where((Token t) => t.Expires > soon).ToList();
			List<Token> mine = live.Where((Token t) => t.Account == _account).ToList();
			if (mine.Count == 0)
			{
				mine = live;
			}
			return mine.FirstOrDefault((Token t) => t.Org == org) ?? mine.OrderByDescending((Token t) => t.Expires).FirstOrDefault();
		}
	}

	private static void Reload()
	{
		string config = Path.Combine(Dir, "config.json");
		DateTime stamp = File.Exists(config) ? File.GetLastWriteTimeUtc(config) : DateTime.MinValue;
		if (stamp == _stamp)
		{
			return;
		}
		_stamp = stamp;
		_tokens = new List<Token>();
		_account = null;
		if (stamp == DateTime.MinValue)
		{
			return;
		}
		byte[]? key = null;
		try
		{
			using JsonDocument cfg = JsonDocument.Parse(ReadShared(config));
			if (cfg.RootElement.TryGetProperty("lastKnownAccountUuid", out JsonElement acct) && acct.ValueKind == JsonValueKind.String)
			{
				_account = acct.GetString();
			}
			if (!cfg.RootElement.TryGetProperty("oauth:tokenCacheV2", out JsonElement cache) || cache.ValueKind != JsonValueKind.String)
			{
				Log.Write("claude app sign-in: no token cache");
				return;
			}
			key = Key();
			if (key == null)
			{
				return;
			}
			string? json = Decrypt(key, Convert.FromBase64String(cache.GetString()!));
			if (json == null)
			{
				Log.Write("claude app sign-in: cannot decrypt the token cache");
				return;
			}
			using JsonDocument doc = JsonDocument.Parse(json);
			foreach (JsonProperty p in doc.RootElement.EnumerateObject())
			{
				Token? t = Parse(p.Name, p.Value);
				if (t != null)
				{
					_tokens.Add(t);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Write("claude app sign-in: " + ex.GetType().Name);
		}
		finally
		{
			if (key != null)
			{
				CryptographicOperations.ZeroMemory(key);
			}
		}
	}

	internal static Token? Parse(string name, JsonElement v)
	{
		// acct:<аккаунт>|<клиент>:<организация>:https://api.anthropic.com:<права>
		if (!name.StartsWith("acct:", StringComparison.Ordinal) || v.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		string[] head = name.Substring(5).Split(':', 3);
		if (head.Length < 3)
		{
			return null;
		}
		string rest = head[2];
		int scheme = rest.IndexOf("://", StringComparison.Ordinal);
		int cut = rest.IndexOf(':', (scheme >= 0) ? (scheme + 3) : 0);
		if (cut < 0)
		{
			return null;
		}
		string audience = rest.Substring(0, cut);
		string[] scopes = rest.Substring(cut + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (audience != "https://api.anthropic.com" || !scopes.Contains("user:profile"))
		{
			return null;
		}
		if (!v.TryGetProperty("token", out JsonElement tok) || tok.GetString() is not string value || value.Length == 0)
		{
			return null;
		}
		long ms = v.TryGetProperty("expiresAt", out JsonElement e) && e.TryGetInt64(out long x) ? x : 0;
		return new Token(head[0].Split('|')[0], head[1], value, DateTimeOffset.FromUnixTimeMilliseconds(ms));
	}

	// Ключ AES из "Local State": base64 → "DPAPI" + данные под DPAPI текущего пользователя.
	private static byte[]? Key()
	{
		string path = Path.Combine(Dir, "Local State");
		if (!File.Exists(path))
		{
			return null;
		}
		using JsonDocument doc = JsonDocument.Parse(ReadShared(path));
		string? b64 = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString();
		byte[] blob = Convert.FromBase64String(b64 ?? "");
		if (blob.Length <= 5 || Encoding.ASCII.GetString(blob, 0, 5) != "DPAPI")
		{
			Log.Write("claude app sign-in: unknown key format");
			return null;
		}
		byte[]? key = Secret.UnprotectUser(blob.AsSpan(5).ToArray());
		if (key == null || key.Length != 32)
		{
			Log.Write("claude app sign-in: cannot unprotect the key");
			return null;
		}
		return key;
	}

	// "v10" + nonce 12 байт + шифротекст + метка 16 байт.
	private static string? Decrypt(byte[] key, byte[] blob)
	{
		if (blob.Length < 3 + 12 + 16 || Encoding.ASCII.GetString(blob, 0, 3) != "v10")
		{
			return null;
		}
		byte[] plain = new byte[blob.Length - 3 - 12 - 16];
		try
		{
			using AesGcm gcm = new AesGcm(key, 16);
			gcm.Decrypt(blob.AsSpan(3, 12), blob.AsSpan(15, plain.Length), blob.AsSpan(blob.Length - 16), plain);
			return Encoding.UTF8.GetString(plain);
		}
		catch (CryptographicException)
		{
			return null;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plain);
		}
	}

	private static string ReadShared(string path)
	{
		using FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader r = new StreamReader(f);
		return r.ReadToEnd();
	}
}
