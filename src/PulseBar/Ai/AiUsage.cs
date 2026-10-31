using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar.Ai;

// Лимиты и балансы сервисов ИИ для плиток: ChatGPT (Codex), Grok (Grok CLI), DeepSeek (ключ API).
// Каждый включённый сервис опрашивается не чаще раза в 5 минут (первый раз — через ~5 с после запуска); на 429 ждём
// Retry-After, но не меньше 5 минут. Включение сервиса или смена ключа DeepSeek — опрос сразу, без ожидания интервала.
// Ошибки не бросаются: пишутся в журнал (только при смене) и уходят в Status. Changed вызывается в потоке пула.
internal sealed class AiUsage : IDisposable
{
	public static readonly string[] Ids = { CodexUsage.Id, GrokUsage.Id, DeepSeekBalance.Id };

	internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

	private readonly Func<string, bool> _enabled;

	private readonly Func<string> _deepSeekKey;

	// Один клиент на всё. Без перенаправлений: токен уходит только на тот адрес, куда его послали.
	private readonly HttpClient _http;

	private readonly Timer _timer;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	// Состояние опроса; трогает только идущий опрос (он всегда один, см. _busy).
	private readonly Dictionary<string, DateTimeOffset> _nextAt = new Dictionary<string, DateTimeOffset>();

	private readonly Dictionary<string, string> _sig = new Dictionary<string, string>();

	private readonly Dictionary<string, string> _status = new Dictionary<string, string>();

	private volatile IReadOnlyDictionary<string, AiSnapshot> _current = new Dictionary<string, AiSnapshot>();

	private int _busy;

	private int _again;

	private volatile bool _disposed;

	// Показания включённых сервисов; словарь после публикации не меняется, его можно читать из любого потока.
	public IReadOnlyDictionary<string, AiSnapshot> Current => _current;

	public event Action? Changed;

	public AiUsage(Func<string, bool> enabled, Func<string> deepSeekKey)
	{
		_enabled = enabled;
		_deepSeekKey = deepSeekKey;
		_http = new HttpClient(new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			PooledConnectionLifetime = TimeSpan.FromMinutes(15)
		})
		{
			Timeout = TimeSpan.FromSeconds(20)
		};
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseBar/" + Updater.Current);
		// Таймер тикает раз в минуту, но в сеть каждый сервис ходит только по своему расписанию (_nextAt).
		_timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
	}

	// Опросить сейчас (например, после смены настроек). Вызовы, пришедшие во время опроса, сливаются в один повтор;
	// интервал 5 минут и Retry-After всё равно соблюдаются.
	public void PollNow()
	{
		if (!_disposed)
		{
			_ = Task.Run(PollAsync);
		}
	}

	private async Task PollAsync()
	{
		while (true)
		{
			if (Interlocked.Exchange(ref _busy, 1) == 1)
			{
				Volatile.Write(ref _again, 1);
				return;
			}
			try
			{
				do
				{
					Volatile.Write(ref _again, 0);
					await PollOnceAsync().ConfigureAwait(false);
				}
				while (Volatile.Read(ref _again) == 1 && !_disposed);
			}
			catch (Exception ex)
			{
				Log.Write("ai: " + ex.Message);
			}
			finally
			{
				Volatile.Write(ref _busy, 0);
			}
			// Просьба могла прийти между концом цикла и снятием флага.
			if (Volatile.Read(ref _again) == 0 || _disposed)
			{
				return;
			}
		}
	}

	private async Task PollOnceAsync()
	{
		Dictionary<string, AiSnapshot> next = new Dictionary<string, AiSnapshot>(_current);
		foreach (string id in Ids)
		{
			if (_disposed)
			{
				return;
			}
			bool on;
			string sig;
			try
			{
				on = _enabled(id);
				sig = (on && id == DeepSeekBalance.Id) ? (_deepSeekKey() ?? "").Trim() : "";
			}
			catch (Exception ex)
			{
				Log.Write("ai: settings: " + ex.Message);
				continue;
			}
			if (!on)
			{
				next.Remove(id);
				_sig.Remove(id);
				_nextAt.Remove(id);
				_status.Remove(id);
				continue;
			}
			// Только что включили или сменили ключ — опросить сразу.
			if (!_sig.TryGetValue(id, out string? oldSig) || oldSig != sig)
			{
				_sig[id] = sig;
				_nextAt.Remove(id);
			}
			if (_nextAt.TryGetValue(id, out DateTimeOffset at) && DateTimeOffset.Now < at)
			{
				continue;
			}
			TimeSpan wait = Interval;
			AiSnapshot s;
			try
			{
				s = await FetchAsync(id, sig, d => wait = d > wait ? d : wait, _cts.Token).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				if (_disposed)
				{
					return;
				}
				s = Fail(id, DefaultSource(id), Reason(ex));
				if (s.Status == "error")
				{
					// Текст FormatException / ArgumentException может повторять значение заголовка (токен) — пишем только тип.
					Log.Write($"ai: {id}: {ex.GetType().Name}" + (ex is FormatException || ex is ArgumentException ? "" : ": " + ex.Message));
				}
			}
			_nextAt[id] = DateTimeOffset.Now + wait;
			if (!_status.TryGetValue(id, out string? was) || was != s.Status)
			{
				Log.Write($"ai: {id}: " + (s.Status.Length > 0 ? s.Status : "ok (" + s.Source + ")"));
				_status[id] = s.Status;
			}
			next.TryGetValue(id, out AiSnapshot? prev);
			next[id] = Merge(prev, s);
		}
		foreach (string id in next.Keys.ToList())
		{
			next[id] = Expire(next[id]);
		}
		if (_disposed || Same(_current, next))
		{
			return;
		}
		_current = next;
		try
		{
			Changed?.Invoke();
		}
		catch (Exception ex)
		{
			Log.Write("ai: Changed: " + ex.Message);
		}
	}

	private Task<AiSnapshot> FetchAsync(string id, string key, Action<TimeSpan> retryAfter, CancellationToken ct)
	{
		return id switch
		{
			CodexUsage.Id => CodexUsage.FetchAsync(_http, retryAfter, ct),
			GrokUsage.Id => GrokUsage.FetchAsync(_http, retryAfter, ct),
			_ => DeepSeekBalance.FetchAsync(_http, key, retryAfter, ct)
		};
	}

	private static string DefaultSource(string id) => id == GrokUsage.Id ? GrokUsage.Source : "api";

	private static bool Same(IReadOnlyDictionary<string, AiSnapshot> a, Dictionary<string, AiSnapshot> b)
	{
		return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out AiSnapshot? v) && kv.Value.Equals(v));
	}

	// Есть ли что показать: проценты или баланс.
	internal static bool HasData(AiSnapshot s) => !float.IsNaN(s.First) || !float.IsNaN(s.Second) || s.Balance.HasValue;

	// Временный сбой: прежние цифры остаются (через 15 минут бледнеют), меняется только Status.
	internal static bool Transient(string status)
	{
		return status == "HTTP 429" || status.StartsWith("HTTP 5", StringComparison.Ordinal) || status == "no connection" || status == "timeout";
	}

	// Новое показание поверх прежнего: временная ошибка не стирает цифры; более старые данные (журнал Codex после
	// ответа API) не заменяют более свежие.
	internal static AiSnapshot Merge(AiSnapshot? old, AiSnapshot s)
	{
		if (old == null || old.Id != s.Id || !HasData(old))
		{
			return s;
		}
		if (!HasData(s))
		{
			return Transient(s.Status) ? old with { Status = s.Status } : s;
		}
		return s.Updated < old.Updated ? old with { Status = s.Status } : s;
	}

	// Окно уже сбросилось (прошло время сброса): старые проценты ничего не значат — NaN.
	internal static AiSnapshot Expire(AiSnapshot s)
	{
		DateTimeOffset now = DateTimeOffset.Now;
		bool first = s.FirstReset <= now;
		bool second = s.SecondReset <= now;
		if (!first && !second)
		{
			return s;
		}
		return s with
		{
			First = first ? float.NaN : s.First,
			FirstReset = first ? null : s.FirstReset,
			Second = second ? float.NaN : s.Second,
			SecondReset = second ? null : s.SecondReset
		};
	}

	internal static AiSnapshot Fail(string id, string source, string status)
	{
		return new AiSnapshot(id, float.NaN, float.NaN, null, null, null, "", DateTimeOffset.Now, source, status);
	}

	internal static string Reason(Exception ex)
	{
		return ex switch
		{
			OperationCanceledException => "timeout",
			HttpRequestException => "no connection",
			JsonException => "unknown response format",
			_ => "error"
		};
	}

	// GET с разбором JSON. Ошибка HTTP — (null, "HTTP <код>"); на 429 (и на любой ответ с Retry-After) сообщает,
	// сколько ждать: не меньше 5 минут и не больше 6 часов (защита от нелепых значений).
	internal static async Task<(JsonDocument? Doc, string Status)> GetJsonAsync(HttpClient http, HttpRequestMessage req, Action<TimeSpan> retryAfter, CancellationToken ct)
	{
		req.Headers.Accept.ParseAdd("application/json");
		using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
		if (resp.StatusCode == HttpStatusCode.TooManyRequests || resp.Headers.RetryAfter != null)
		{
			TimeSpan wait = resp.Headers.RetryAfter?.Delta
				?? (resp.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.Now : Interval);
			retryAfter(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, Interval.TotalSeconds, TimeSpan.FromHours(6).TotalSeconds)));
		}
		if (!resp.IsSuccessStatusCode)
		{
			return (null, "HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
		}
		using Stream body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		return (await JsonDocument.ParseAsync(body, default, ct).ConfigureAwait(false), "");
	}

	// Файлы входа CLI открыты их владельцами — читаем, не мешая им писать.
	internal static string ReadShared(string path)
	{
		using FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader r = new StreamReader(f);
		return r.ReadToEnd();
	}

	internal static string? Str(JsonElement o, string name)
	{
		return o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	}

	// Число или число строкой (int64 в JSON протобуфа приходит строкой).
	internal static double? Number(JsonElement o, string name)
	{
		if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out JsonElement v))
		{
			return null;
		}
		if (v.ValueKind == JsonValueKind.Number)
		{
			return v.GetDouble();
		}
		if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
		{
			return d;
		}
		return null;
	}

	// Время: строка ISO 8601 или секунды Unix.
	internal static DateTimeOffset? Date(JsonElement o, string name)
	{
		if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out JsonElement v))
		{
			return null;
		}
		if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t))
		{
			return t;
		}
		if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long unix) && unix > 0)
		{
			return DateTimeOffset.FromUnixTimeSeconds(unix);
		}
		return null;
	}

	internal static float Percent(double v) => double.IsFinite(v) ? (float)Math.Clamp(v, 0, 100) : float.NaN;

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_timer.Dispose();
		_cts.Cancel();
		_http.Dispose();
	}
}
