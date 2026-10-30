using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PulseBar;

// Отвечает строкам PulseBar (по одной на каждого вошедшего пользователя) по каналу SensorPipe.Name.
// Доступ: вошедшие пользователи — чтение/запись, по сети — запрещён.
internal sealed class PipeServer
{
	private const int Instances = 4;

	private readonly CpuTemp _temp;

	private readonly CancellationTokenSource _cts = new();

	private readonly List<Task> _loops = new();

	public PipeServer(CpuTemp temp)
	{
		_temp = temp;
	}

	public void Start()
	{
		for (int i = 0; i < Instances; i++)
		{
			_loops.Add(Task.Run(() => Serve(_cts.Token)));
		}
	}

	public void Stop()
	{
		_cts.Cancel();
		try
		{
			Task.WaitAll(_loops.ToArray(), 3000);
		}
		catch (AggregateException)
		{
		}
	}

	private async Task Serve(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(SensorPipe.Name, PipeDirection.InOut, Instances,
					PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64, 64, Security());
				await pipe.WaitForConnectionAsync(ct);
				byte[] request = new byte[1];
				while (!ct.IsCancellationRequested && await pipe.ReadAsync(request, ct) == 1)
				{
					if (request[0] == SensorPipe.RequestTemp)
					{
						await pipe.WriteAsync(Reply(), ct);
					}
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (IOException)
			{
			}
			catch (Exception ex)
			{
				ServiceLog.Write("pipe: " + ex.Message);
				await Task.Delay(1000, ct).ContinueWith(_ => { });
			}
		}
	}

	private byte[] Reply()
	{
		float t = _temp.Read();
		byte[] status = Encoding.UTF8.GetBytes(_temp.Status);
		int len = Math.Min(status.Length, 255);
		byte[] reply = new byte[5 + len];
		BitConverter.TryWriteBytes(reply, t);
		reply[4] = (byte)len;
		Array.Copy(status, 0, reply, 5, len);
		return reply;
	}

	private static PipeSecurity Security()
	{
		PipeSecurity s = new();
		s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
		s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
		s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
		s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
		return s;
	}
}
