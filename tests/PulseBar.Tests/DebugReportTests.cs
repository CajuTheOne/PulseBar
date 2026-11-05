using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PulseBar.Tests;

// Отчёты для отладки (2.4.0-beta.12): обезличивание, очередь, отправка на «NAS» (временная папка), без ключей.
[TestClass]
public class DebugReportTests
{
	private string _dir = "";

	[TestInitialize]
	public void Init()
	{
		_dir = Path.Combine(Path.GetTempPath(), "PulseBarTests", "reports-" + Guid.NewGuid().ToString("N"));
		DebugReports.DirOverride = Path.Combine(_dir, "local");
		DebugReports.TargetOverride = Path.Combine(_dir, "nas");
	}

	[TestCleanup]
	public void Cleanup()
	{
		DebugReports.DirOverride = null;
		DebugReports.TargetOverride = null;
		DebugReports.Enabled = false;
		try
		{
			Directory.Delete(_dir, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[TestMethod]
	public void PrivateDataIsReplaced()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string text = $"PC {Environment.MachineName}, user {Environment.UserName}, log {profile}\\AppData\\x.log, mail someone@example.com, headset bt:8099E74265A2, account Secret Name";
		string a = DebugReports.Anonymize(text, new[] { "Secret Name" });
		StringAssert.DoesNotMatch(a, new System.Text.RegularExpressions.Regex("someone@|8099E74265A2|Secret Name"));
		Assert.IsFalse(a.Contains(profile, StringComparison.OrdinalIgnoreCase), a);
		StringAssert.Contains(a, "<pc>");
		StringAssert.Contains(a, "<email>");
		StringAssert.Contains(a, @"C:\Users\<user>\AppData\x.log");
		Assert.AreEqual(DebugReports.Anonymize("bt:8099E74265A2"), DebugReports.Anonymize("bt:8099e74265a2"), "the same headset keeps the same id");
		StringAssert.Matches(DebugReports.PcId, new System.Text.RegularExpressions.Regex("^pc-[0-9a-f]{8}$"));
	}

	[TestMethod]
	public void SettingsLoseKeys()
	{
		string s = DebugReports.SanitizeSettings("{\"layout\": 1, \"deepSeekKey\": \"AQAAAsecret\", \"beta\": true}");
		StringAssert.Contains(s, "\"layout\": 1");
		Assert.IsFalse(s.Contains("AQAAAsecret"));
		StringAssert.Contains(s, "<removed>");
	}

	[TestMethod]
	public void ReportsWaitForTheNasAndKeepTwenty()
	{
		List<(string, byte[])> parts = new List<(string, byte[])>
		{
			("report.txt", Encoding.UTF8.GetBytes("hello someone@example.com")),
			("taskbar.png", new byte[3] { 1, 2, 3 })
		};
		DebugReports.Entry e = DebugReports.Create("manual", "keyboard went dark", parts, (string t) => DebugReports.Anonymize(t));
		Assert.IsFalse(e.Sent);
		using (ZipArchive zip = ZipFile.OpenRead(Path.Combine(DebugReports.LocalDir, e.File)))
		{
			using StreamReader r = new StreamReader(zip.GetEntry("report.txt")!.Open());
			Assert.AreEqual("hello <email>", r.ReadToEnd(), "text parts are anonymised");
			Assert.AreEqual(3, zip.GetEntry("taskbar.png")!.Length);
		}
		// «NAS» недоступен: файл на месте пути папки.
		File.WriteAllText(Path.Combine(_dir, "nas"), "not a folder");
		Assert.AreEqual(0, DebugReports.SendPending());
		Assert.IsFalse(DebugReports.List().Single().Sent, "waits in the queue");
		File.Delete(Path.Combine(_dir, "nas"));
		Assert.AreEqual(1, DebugReports.SendPending());
		Assert.IsTrue(DebugReports.List().Single().Sent);
		Assert.IsTrue(File.Exists(Path.Combine(_dir, "nas", DebugReports.PcId, e.File)));
		for (int i = 0; i < 25; i++)
		{
			DebugReports.Create("daily", "", parts, (string t) => t);
		}
		Assert.AreEqual(DebugReports.Keep, DebugReports.List().Count);
		Assert.AreEqual(DebugReports.Keep, Directory.GetFiles(DebugReports.LocalDir, "*.zip").Length, "old ones are deleted");
		Assert.IsNotNull(DebugReports.Last("daily"));
	}

	[TestMethod]
	public void NothingIsRecordedWhenOff()
	{
		DebugReports.Enabled = false;
		DebugReports.RecordCrash("boom");
		DebugReports.RecordError("oops");
		Assert.IsFalse(File.Exists(DebugReports.CrashFile));
		Assert.IsNull(DebugReports.TakeError(0));
		DebugReports.Enabled = true;
		Directory.CreateDirectory(Path.GetDirectoryName(DebugReports.CrashFile)!);
		DebugReports.RecordCrash("boom");
		StringAssert.Contains(File.ReadAllText(DebugReports.CrashFile), "boom");
		DebugReports.RecordError("oops");
		Assert.IsNull(DebugReports.TakeError(60000), "waits a minute for more");
		Assert.AreEqual("oops", DebugReports.TakeError(0));
		Assert.IsNull(DebugReports.TakeError(0), "taken once");
	}
}
