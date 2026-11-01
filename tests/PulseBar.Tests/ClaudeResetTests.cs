using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PulseBar.Tests;

// Оценка времени сброса лимитов Claude по истории приложения (2.4.0-beta.3), на образце настоящей истории 30–31.10.2026.
[TestClass]
public class ClaudeResetTests
{
	private static long T(string local)
	{
		return new DateTimeOffset(DateTime.Parse("2026-" + local), TimeSpan.FromHours(3)).ToUnixTimeMilliseconds();
	}

	[TestMethod]
	public void FiveHourWindowStartsBetweenSamples()
	{
		List<(long, float, float)> s = new List<(long, float, float)>
		{
			(T("10-30 20:18"), 13f, 5f),
			(T("10-30 21:24"), 0f, 5f),
			(T("10-30 21:33"), 1f, 6f),
			(T("10-30 23:03"), 6f, 6f)
		};
		DateTimeOffset? reset = ClaudeUsage.EstimateFiveHourReset(s, T("10-30 23:05"));
		Assert.IsNotNull(reset);
		Assert.AreEqual(T("10-31 02:28") + 30000, reset!.Value.ToUnixTimeMilliseconds(), "midpoint of 21:24–21:33 + 5 h");
	}

	[TestMethod]
	public void FiveHourWindowAfterADropOrAGap()
	{
		List<(long, float, float)> drop = new List<(long, float, float)>
		{
			(T("10-31 02:18"), 28f, 10f),
			(T("10-31 02:33"), 2f, 10f),
			(T("10-31 03:03"), 4f, 11f)
		};
		Assert.IsNotNull(ClaudeUsage.EstimateFiveHourReset(drop, T("10-31 03:10")), "new window right after a reset");
		List<(long, float, float)> gap = new List<(long, float, float)>
		{
			(T("10-31 16:48"), 22f, 14f),
			(T("11-01 02:29"), 28f, 20f)
		};
		Assert.IsNull(ClaudeUsage.EstimateFiveHourReset(gap, T("11-01 02:30")), "start lost in a 10-hour gap");
	}

	[TestMethod]
	public void WeeklyResetNarrowsAcrossWeeks()
	{
		List<(long, float, float)> s = new List<(long, float, float)>
		{
			(T("10-09 03:09"), 0f, 22f), (T("10-09 09:09"), 0f, 0f),
			(T("10-15 22:18"), 0f, 45f), (T("10-16 07:51"), 0f, 0f),
			(T("10-23 01:02"), 0f, 27f), (T("10-23 09:04"), 0f, 0f),
			(T("10-26 02:37"), 0f, 2f), (T("10-30 10:32"), 0f, 0f),
			(T("11-01 03:04"), 30f, 20f)
		};
		DateTimeOffset? reset = ClaudeUsage.EstimateWeeklyReset(s, T("11-01 03:10"));
		Assert.IsNotNull(reset);
		// Пересечение: пятница 03:09–07:51 → около 05:30, следующая пятница — 06.11.
		DateTime local = reset!.Value.ToOffset(TimeSpan.FromHours(3)).DateTime;
		Assert.AreEqual(new DateTime(2026, 11, 6), local.Date);
		Assert.IsTrue(local.Hour >= 5 && local.Hour <= 6, local.ToString());
	}
}
