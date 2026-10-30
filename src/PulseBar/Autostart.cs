using System;
using Microsoft.Win32;

namespace PulseBar;

// С 2.0 строка работает без прав администратора, поэтому автозапуск — обычный ключ Run текущего пользователя
// (в 1.x была задача Планировщика «\AI Tools\PulseBar» с наивысшими правами; её удаляет установщик).
internal static class Autostart
{
	private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string ValueName = "PulseBar";

	public static bool IsEnabled()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
			return key?.GetValue(ValueName) is string;
		}
		catch
		{
			return false;
		}
	}

	public static bool Enable(string exePath)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
			key.SetValue(ValueName, "\"" + exePath + "\"");
			return true;
		}
		catch (Exception ex)
		{
			Log.Write("autostart: " + ex.Message);
			return false;
		}
	}

	public static bool Disable()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
			key?.DeleteValue(ValueName, throwOnMissingValue: false);
			return true;
		}
		catch (Exception ex)
		{
			Log.Write("autostart: " + ex.Message);
			return false;
		}
	}
}
