using System;
using System.IO;

namespace PulseBar;

internal static class Log
{
	// Тесты переводят журнал в свою папку, чтобы не писать в журнал строки, запущенной у пользователя.
	internal static string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBar", "pulsebar.log");

	// Прошлый журнал (2.4.0-beta.12): раньше журнал при 64 КБ стирался целиком — с ним пропадало и падение.
	internal static string OldPath => Path.ChangeExtension(FilePath, ".old.log");

	private const long MaxSize = 512 * 1024;

	public static void Write(string message)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
			FileInfo fileInfo = new FileInfo(FilePath);
			if (fileInfo.Exists && fileInfo.Length > MaxSize)
			{
				File.Move(FilePath, OldPath, overwrite: true);
			}
			File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}
}
