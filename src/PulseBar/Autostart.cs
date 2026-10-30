using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace PulseBar;

internal static class Autostart
{
	public const string TaskName = "\\AI Tools\\PulseBar";

	public static bool IsEnabled()
	{
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\TaskCache\\Tree\\AI Tools\\PulseBar");
			return registryKey != null;
		}
		catch
		{
			return Schtasks("/Query /TN \"\\AI Tools\\PulseBar\"") == 0;
		}
	}

	public static bool Enable(string exePath)
	{
		string value = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
		string contents = $"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n  <RegistrationInfo>\n    <Author>{value}</Author>\n    <Description>PulseBar: CPU/GPU load and temperature, RAM usage on the taskbar</Description>\n  </RegistrationInfo>\n  <Triggers>\n    <LogonTrigger>\n      <Enabled>true</Enabled>\n      <UserId>{value}</UserId>\n      <Delay>PT10S</Delay>\n    </LogonTrigger>\n  </Triggers>\n  <Principals>\n    <Principal id=\"Author\">\n      <UserId>{value}</UserId>\n      <LogonType>InteractiveToken</LogonType>\n      <RunLevel>HighestAvailable</RunLevel>\n    </Principal>\n  </Principals>\n  <Settings>\n    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n    <AllowHardTerminate>true</AllowHardTerminate>\n    <StartWhenAvailable>false</StartWhenAvailable>\n    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n    <IdleSettings>\n      <StopOnIdleEnd>false</StopOnIdleEnd>\n      <RestartOnIdle>false</RestartOnIdle>\n    </IdleSettings>\n    <AllowStartOnDemand>true</AllowStartOnDemand>\n    <Enabled>true</Enabled>\n    <Hidden>false</Hidden>\n    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n    <WakeToRun>false</WakeToRun>\n    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n    <Priority>5</Priority>\n  </Settings>\n  <Actions Context=\"Author\">\n    <Exec>\n      <Command>\"{SecurityElement.Escape(exePath)}\"</Command>\n    </Exec>\n  </Actions>\n</Task>";
		string text = Path.Combine(Path.GetTempPath(), $"PulseBar-task-{Environment.ProcessId}.xml");
		try
		{
			File.WriteAllText(text, contents, Encoding.Unicode);
			return Schtasks($"/Create /TN \"{"\\AI Tools\\PulseBar"}\" /XML \"{text}\" /F") == 0;
		}
		finally
		{
			try
			{
				File.Delete(text);
			}
			catch
			{
			}
		}
	}

	public static bool Disable()
	{
		return Schtasks("/Delete /TN \"\\AI Tools\\PulseBar\" /F") == 0;
	}

	public static bool RunTask()
	{
		return Schtasks("/Run /TN \"\\AI Tools\\PulseBar\"") == 0;
	}

	public static bool RunElevated(string exePath, string args)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo(exePath, args)
			{
				UseShellExecute = true,
				Verb = "runas"
			});
			if (process == null)
			{
				return false;
			}
			process.WaitForExit(30000);
			return process.HasExited && process.ExitCode == 0;
		}
		catch (Exception ex)
		{
			Log.Write("autostart elevated: " + ex.Message);
			return false;
		}
	}

	private static int Schtasks(string args)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardError = true
			});
			if (process == null)
			{
				return -1;
			}
			string text = process.StandardError.ReadToEnd();
			process.WaitForExit(15000);
			if (process.ExitCode != 0)
			{
				Log.Write($"schtasks {args}: {process.ExitCode} {text.Trim()}");
			}
			return process.ExitCode;
		}
		catch (Exception ex)
		{
			Log.Write("schtasks " + args + ": " + ex.Message);
			return -1;
		}
	}
}
