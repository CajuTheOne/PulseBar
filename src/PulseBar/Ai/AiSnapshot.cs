using System;

namespace PulseBar.Ai;

// Одно показание сервиса ИИ.
// Id: "chatgpt" | "grok" | "deepseek". First / Second — израсходовано в % (0–100) короткого и длинного окна, NaN — неизвестно
// или окна нет. Balance + Currency — для DeepSeek. Source — откуда данные ("api", "codex logs", "grok cli").
// Status: "" — всё в порядке, иначе короткая причина по-английски (перевод — в интерфейсе).
internal sealed record AiSnapshot(string Id, float First, float Second, DateTimeOffset? FirstReset, DateTimeOffset? SecondReset,
	decimal? Balance, string Currency, DateTimeOffset Updated, string Source, string Status)
{
	public bool Stale => DateTimeOffset.Now - Updated > TimeSpan.FromMinutes(15);
}
