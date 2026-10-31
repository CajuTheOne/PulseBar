using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PulseBar.Tests;

// Журнал тестов — во временной папке, а не в %LOCALAPPDATA%\PulseBar\pulsebar.log рабочей строки.
[TestClass]
public static class TestSetup
{
	[AssemblyInitialize]
	public static void Init(TestContext _)
	{
		Log.FilePath = Path.Combine(Path.GetTempPath(), "PulseBarTests", "pulsebar-tests.log");
	}
}
