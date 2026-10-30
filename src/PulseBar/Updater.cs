using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace PulseBar;

// Обновления из GitHub Releases: ищет самый новый релиз (с бета-версиями, если включены),
// качает PulseBar-Setup-<версия>.exe, сверяет SHA-256 из GitHub и запускает тихую установку (один запрос UAC).
internal sealed class Updater
{
	public const string Repo = "CajuTheOne/PulseBar";

	public sealed record Release(string Version, string Url, long Size, string? Sha256);

	public static readonly string Current = CurrentVersion();

	public Release? Available { get; private set; }

	public bool Busy { get; private set; }

	public async Task CheckAsync(bool beta)
	{
		try
		{
			using HttpClient http = Client();
			using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30"));
			Release? best = null;
			foreach (JsonElement r in doc.RootElement.EnumerateArray())
			{
				if (r.GetProperty("draft").GetBoolean() || (!beta && r.GetProperty("prerelease").GetBoolean()))
				{
					continue;
				}
				string version = r.GetProperty("tag_name").GetString()!.TrimStart('v');
				JsonElement asset = r.GetProperty("assets").EnumerateArray()
					.FirstOrDefault(a => a.GetProperty("name").GetString()!.StartsWith("PulseBar-Setup", StringComparison.OrdinalIgnoreCase)
						&& a.GetProperty("name").GetString()!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
				if (asset.ValueKind != JsonValueKind.Object || (best != null && Compare(version, best.Version) <= 0))
				{
					continue;
				}
				string? digest = asset.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
				best = new Release(version, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
					digest != null && digest.StartsWith("sha256:") ? digest[7..] : null);
			}
			Available = best != null && Compare(best.Version, Current) > 0 ? best : null;
			Log.Write($"update check: current {Current}, latest {best?.Version ?? "none"}{(Available != null ? " — update available" : "")}");
		}
		catch (Exception ex)
		{
			Log.Write("update check: " + ex.Message);
		}
	}

	// true — установщик запущен, программу надо закрыть (установщик запустит новую версию сам).
	public async Task<bool> InstallAsync()
	{
		Release? r = Available;
		if (r == null || Busy)
		{
			return false;
		}
		Busy = true;
		try
		{
			string file = Path.Combine(Path.GetTempPath(), $"PulseBar-Setup-{r.Version}.exe");
			using (HttpClient http = Client())
			{
				byte[] data = await http.GetByteArrayAsync(r.Url);
				if (data.Length != r.Size)
				{
					throw new InvalidDataException($"size {data.Length}, expected {r.Size}");
				}
				if (r.Sha256 != null && !Convert.ToHexString(SHA256.HashData(data)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("SHA-256 does not match the release");
				}
				await File.WriteAllBytesAsync(file, data);
			}
			Log.Write($"update: starting {file}");
			Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true })?.Dispose();
			return true;
		}
		catch (Exception ex)
		{
			Log.Write("update: " + ex.Message);
			return false;
		}
		finally
		{
			Busy = false;
		}
	}

	private static HttpClient Client()
	{
		HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseBar/" + Current);
		http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return http;
	}

	private static string CurrentVersion()
	{
		string v = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
		int plus = v.IndexOf('+');
		return plus < 0 ? v : v[..plus];
	}

	// Как в SemVer: 2.0.0-beta.2 < 2.0.0-beta.10 < 2.0.0 < 2.0.1.
	public static int Compare(string a, string b)
	{
		(string coreA, string? preA) = Split(a);
		(string coreB, string? preB) = Split(b);
		int[] na = coreA.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
		int[] nb = coreB.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
		for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
		{
			int c = (i < na.Length ? na[i] : 0).CompareTo(i < nb.Length ? nb[i] : 0);
			if (c != 0)
			{
				return c;
			}
		}
		if (preA == null || preB == null)
		{
			return preA == null ? (preB == null ? 0 : 1) : -1;
		}
		string[] pa = preA.Split('.');
		string[] pb = preB.Split('.');
		for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
		{
			bool numA = int.TryParse(pa[i], out int ia);
			bool numB = int.TryParse(pb[i], out int ib);
			int c = numA && numB ? ia.CompareTo(ib) : numA ? -1 : numB ? 1 : string.CompareOrdinal(pa[i], pb[i]);
			if (c != 0)
			{
				return c;
			}
		}
		return pa.Length.CompareTo(pb.Length);
	}

	private static (string, string?) Split(string v)
	{
		int dash = v.IndexOf('-');
		return dash < 0 ? (v, null) : (v[..dash], v[(dash + 1)..]);
	}
}
