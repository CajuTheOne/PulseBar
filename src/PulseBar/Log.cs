using System;
using System.IO;

namespace PulseBar;

internal static class Log
{
	// Тесты переводят журнал в свою папку, чтобы не писать в журнал строки, запущенной у пользователя.
	internal static string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", "pulsebar.log");

	public static void Write(string message)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
			FileInfo fileInfo = new FileInfo(FilePath);
			if (fileInfo.Exists && fileInfo.Length > 65536)
			{
				fileInfo.Delete();
			}
			File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}
}
