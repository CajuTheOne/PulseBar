using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulseBar;

// Отчёты для отладки (2.4.0-beta.12, решения пользователя 05.11.2026). Только при галочке «Собирать данные для отладки»
// в «О программе» (после установки выключена): выключена — ничего не собирается и не отправляется; журнал pulsebar.log
// пишется как всегда и остаётся на этом ПК.
// Отчёт — ZIP: report.txt (повод, текст пользователя, система, состояние строки), журналы строки и службы датчиков,
// settings.json без ключей, logitech.json, devices.json, отчёт об устройствах; по кнопке «Отправить» — и снимок панели
// задач. Всё обезличено (Anonymize): имя ПК и пользователя, почта, адреса Bluetooth.
// Очередь — %LOCALAPPDATA%\PulseBar\reports (index.json, последние 20 отчётов); отправка — копия в папку NAS Leo
// \\192.168.1.61\Leo\AI\Tools\PulseBar\reports\<ID ПК>\ (ПК должен видеть общую папку Leo); нет связи — снова позже.
// Поводы: crash — строка упала (отчёт при следующем запуске), error — ошибка без падения, daily — раз в сутки,
// manual — кнопка «Отправить» с описанием.
internal static class DebugReports
{
	public const string DefaultTarget = @"\\192.168.1.61\Leo\AI\Tools\PulseBar\reports";

	public const int Keep = 20;

	// Для тестов: своя папка очереди и своя папка NAS.
	internal static string? DirOverride;

	internal static string? TargetOverride;

	private static readonly object Lock = new object();

	private static string Base => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar");

	public static string LocalDir => DirOverride ?? Path.Combine(Base, "reports");

	public static string Target => TargetOverride ?? DefaultTarget;

	// Текст падения — пишет обработчик в Program, отчёт собирается при следующем запуске.
	public static string CrashFile => Path.Combine(DirOverride ?? Base, "crash.txt");

	// Описание от окна настроек для отчёта по кнопке «Отправить».
	public static string RequestFile => Path.Combine(DirOverride ?? Base, "report-request.txt");

	private static string IndexFile => Path.Combine(LocalDir, "index.json");

	// Выставляет строка из настроек; обработчики падения и ошибки смотрят сюда.
	public static volatile bool Enabled;

	// Ошибка без падения (первая после последнего отчёта): время и текст.
	private static long _errorAt = -1;

	private static string _errorText = "";

	public sealed class Entry
	{
		public string File { get; set; } = "";

		public DateTime Time { get; set; }

		public string Reason { get; set; } = "";

		public long Size { get; set; }

		public bool Sent { get; set; }

		public string Note { get; set; } = "";
	}

	// Обезличенный ID этого ПК: "pc-" + 8 hex от MachineGuid Windows; папка на NAS называется так же.
	public static string PcId { get; } = ComputeId();

	private static string ComputeId()
	{
		string seed = Environment.MachineName;
		try
		{
			using RegistryKey? k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
			seed = (k?.GetValue("MachineGuid") as string) ?? seed;
		}
		catch (Exception)
		{
		}
		byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("PulseBar:" + seed));
		return "pc-" + Convert.ToHexString(h, 0, 4).ToLowerInvariant();
	}

	public static void RecordCrash(string text)
	{
		if (!Enabled)
		{
			return;
		}
		try
		{
			File.AppendAllText(CrashFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{text}{Environment.NewLine}");
		}
		catch (Exception)
		{
		}
	}

	public static void RecordError(string text)
	{
		if (!Enabled)
		{
			return;
		}
		lock (Lock)
		{
			if (_errorAt < 0)
			{
				_errorAt = Environment.TickCount64;
				_errorText = text;
			}
		}
	}

	// Ошибка ждёт отчёта не меньше minAgeMs (вдруг следом ещё); null — нет.
	public static string? TakeError(long minAgeMs)
	{
		lock (Lock)
		{
			if (_errorAt < 0 || Environment.TickCount64 - _errorAt < minAgeMs)
			{
				return null;
			}
			_errorAt = -1;
			return _errorText;
		}
	}

	public static List<Entry> List()
	{
		lock (Lock)
		{
			try
			{
				if (File.Exists(IndexFile))
				{
					using FileStream f = new FileStream(IndexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					return JsonSerializer.Deserialize<List<Entry>>(f) ?? new List<Entry>();
				}
			}
			catch (Exception ex) when (ex is IOException || ex is JsonException)
			{
			}
			return new List<Entry>();
		}
	}

	private static void Save(List<Entry> list)
	{
		Directory.CreateDirectory(LocalDir);
		string tmp = IndexFile + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
		File.Move(tmp, IndexFile, overwrite: true);
	}

	// Последний отчёт с этим поводом (для «раз в сутки» и «не чаще раза в 6 часов»).
	public static DateTime? Last(string reason)
	{
		return List().Where((Entry e) => e.Reason == reason).Select((Entry e) => (DateTime?)e.Time).DefaultIfEmpty(null).Max();
	}

	// Записать отчёт в очередь: ZIP из частей (имя → содержимое). Текстовые части обезличиваются.
	public static Entry Create(string reason, string note, IEnumerable<(string Name, byte[] Data)> parts, Func<string, string> anonymize)
	{
		Directory.CreateDirectory(LocalDir);
		DateTime now = DateTime.Now;
		// Имя уникально и при нескольких отчётах в одну секунду: чистка удаляет файл по имени.
		string name = $"{now:yyyyMMdd-HHmmss}-{reason}.zip";
		for (int n = 2; File.Exists(Path.Combine(LocalDir, name)); n++)
		{
			name = $"{now:yyyyMMdd-HHmmss}-{reason}-{n}.zip";
		}
		string path = Path.Combine(LocalDir, name);
		using (FileStream f = new FileStream(path, FileMode.Create, FileAccess.Write))
		{
			using ZipArchive zip = new ZipArchive(f, ZipArchiveMode.Create);
			foreach (var (partName, data) in parts)
			{
				ZipArchiveEntry e = zip.CreateEntry(partName, CompressionLevel.Optimal);
				using Stream s = e.Open();
				byte[] bytes = IsText(partName) ? Encoding.UTF8.GetBytes(anonymize(Encoding.UTF8.GetString(data))) : data;
				s.Write(bytes, 0, bytes.Length);
			}
		}
		Entry entry = new Entry
		{
			File = name,
			Time = now,
			Reason = reason,
			Size = new FileInfo(path).Length,
			Note = anonymize(note.Length > 200 ? note.Substring(0, 200) + "…" : note)
		};
		lock (Lock)
		{
			List<Entry> list = List();
			list.Add(entry);
			// Старые — прочь (и с диска), сначала отправленные.
			while (list.Count > Keep)
			{
				Entry old = list.FirstOrDefault((Entry x) => x.Sent) ?? list[0];
				list.Remove(old);
				TryDelete(Path.Combine(LocalDir, old.File));
			}
			Save(list);
		}
		Log.Write($"reports: {name} ({entry.Size / 1024} KB) queued");
		return entry;
	}

	private static bool IsText(string name)
	{
		string ext = Path.GetExtension(name).ToLowerInvariant();
		return ext == ".txt" || ext == ".log" || ext == ".json";
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception)
		{
		}
	}

	private static string _lastSendError = "";

	// Неотправленные — копией на NAS. Нет связи — остаются в очереди (в журнал — один раз до следующего успеха).
	// Сетевой путь может думать десятки секунд — вызывать не из потока окна.
	public static int SendPending()
	{
		List<Entry> pending = List().Where((Entry e) => !e.Sent).ToList();
		if (pending.Count == 0)
		{
			return 0;
		}
		int sent = 0;
		try
		{
			string dir = Path.Combine(Target, PcId);
			Directory.CreateDirectory(dir);
			foreach (Entry e in pending)
			{
				string src = Path.Combine(LocalDir, e.File);
				if (File.Exists(src))
				{
					File.Copy(src, Path.Combine(dir, e.File), overwrite: true);
				}
				lock (Lock)
				{
					List<Entry> list = List();
					foreach (Entry x in list.Where((Entry x) => x.File == e.File))
					{
						x.Sent = true;
					}
					Save(list);
				}
				sent++;
			}
			_lastSendError = "";
			Log.Write($"reports: {sent} sent to {PcId}");
		}
		catch (Exception ex)
		{
			if (ex.Message != _lastSendError)
			{
				_lastSendError = ex.Message;
				Log.Write("reports: NAS not reachable, " + (pending.Count - sent) + " waiting: " + ex.Message);
			}
		}
		return sent;
	}

	// settings.json без ключей и токенов (ключ DeepSeek зашифрован, но и его — прочь).
	public static string SanitizeSettings(string json)
	{
		try
		{
			JsonNode? root = JsonNode.Parse(json);
			if (root is JsonObject o)
			{
				foreach (string key in o.Select(p => p.Key).Where(k => k.Contains("key", StringComparison.OrdinalIgnoreCase) || k.Contains("token", StringComparison.OrdinalIgnoreCase)).ToList())
				{
					o[key] = "<removed>";
				}
				return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
			}
		}
		catch (JsonException)
		{
		}
		return "<not readable>";
	}

	// Обезличить текст: имя ПК и пользователя (и путь профиля), почта, адреса Bluetooth (bt:… → bt:<6 hex хеша>);
	// secrets — ещё строки, которые надо заменить (почта аккаунта Claude и т. п.).
	public static string Anonymize(string text, IEnumerable<string>? secrets = null)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		foreach (string s in (secrets ?? Array.Empty<string>()).Where((string s) => s.Length >= 3).OrderByDescending((string s) => s.Length))
		{
			text = text.Replace(s, "<private>", StringComparison.OrdinalIgnoreCase);
		}
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (profile.Length > 3)
		{
			text = text.Replace(profile, @"C:\Users\<user>", StringComparison.OrdinalIgnoreCase);
		}
		text = Regex.Replace(text, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "<email>");
		text = Regex.Replace(text, @"\bbt:([0-9A-Fa-f]{12})\b", (Match m) => "bt:" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(m.Groups[1].Value.ToUpperInvariant())), 0, 3));
		foreach (string word in new string[2] { Environment.MachineName, Environment.UserName })
		{
			if (word.Length >= 3)
			{
				text = Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(word) + @"(?![A-Za-z0-9])", (word == Environment.MachineName) ? "<pc>" : "<user>", RegexOptions.IgnoreCase);
			}
		}
		return text;
	}

	public static string Size(long bytes)
	{
		return (bytes < 1024 * 1024) ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / 1024.0 / 1024.0:0.0} MB";
	}
}
