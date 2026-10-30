using System;
using System.IO;
using System.ServiceProcess;

namespace PulseBar;

internal static class Program
{
	// Без аргументов — запуск службой Windows. --console — то же в окне консоли (отладка, нужны права),
	// --dump — одно измерение в консоль.
	private static int Main(string[] args)
	{
		if (args.Length == 1 && args[0] == "--dump")
		{
			using CpuTemp temp = new();
			Console.WriteLine($"{temp.Status}: {temp.Read():0.0} °C");
			return 0;
		}
		if (args.Length == 1 && args[0] == "--console")
		{
			using CpuTemp temp = new();
			PipeServer server = new(temp);
			server.Start();
			Console.WriteLine($"{temp.Status}. Pipe \\\\.\\pipe\\{SensorPipe.Name} is open; press Enter to stop.");
			Console.ReadLine();
			server.Stop();
			return 0;
		}
		ServiceBase.Run(new SensorService());
		return 0;
	}
}

internal sealed class SensorService : ServiceBase
{
	private CpuTemp? _temp;

	private PipeServer? _server;

	public SensorService()
	{
		ServiceName = SensorPipe.ServiceName;
		CanStop = true;
		CanShutdown = true;
	}

	protected override void OnStart(string[] args)
	{
		_temp = new CpuTemp();
		ServiceLog.Write($"start {typeof(SensorService).Assembly.GetName().Version}: {_temp.Status}");
		_server = new PipeServer(_temp);
		_server.Start();
	}

	protected override void OnStop()
	{
		_server?.Stop();
		_temp?.Dispose();
	}

	protected override void OnShutdown() => OnStop();
}

internal static class ServiceLog
{
	private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulseBar", "sensor.log");

	public static void Write(string message)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			FileInfo info = new(FilePath);
			if (info.Exists && info.Length > 65536)
			{
				info.Delete();
			}
			File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}
}
