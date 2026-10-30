using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace PulseBar;

// Температура CPU от службы PulseBarSensor (PawnIO нужны права администратора, строка работает без них).
// Канал опрашивает свой поток: если служба зависнет, поток окна (а с ним и панель задач) не ждёт.
internal sealed class CpuTempSensor : IDisposable
{
	private readonly Thread _thread;

	private readonly AutoResetEvent _wake = new(false);

	private volatile bool _stop;

	private volatile float _value = float.NaN;

	private volatile string _status = "PulseBarSensor service is not running";

	public string Status => L.T(_status);

	public bool Available => !float.IsNaN(_value);

	public CpuTempSensor()
	{
		_thread = new Thread(Run) { IsBackground = true, Name = "PulseBar.Sensor client", Priority = ThreadPriority.BelowNormal };
		_thread.Start();
	}

	// Значение предыдущего опроса; заодно будит поток за следующим.
	public float Read()
	{
		_wake.Set();
		return _value;
	}

	private void Run()
	{
		byte[] head = new byte[5];
		byte[] text = new byte[255];
		while (!_stop)
		{
			try
			{
				using NamedPipeClientStream pipe = new(".", SensorPipe.Name, PipeDirection.InOut);
				pipe.Connect(1000);
				while (!_stop)
				{
					pipe.WriteByte(SensorPipe.RequestTemp);
					pipe.ReadExactly(head);
					pipe.ReadExactly(text, 0, head[4]);
					_value = BitConverter.ToSingle(head, 0);
					_status = Encoding.UTF8.GetString(text, 0, head[4]);
					_wake.WaitOne(5000);
				}
			}
			catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
			{
				_value = float.NaN;
				_status = "PulseBarSensor service is not running";
			}
			if (!_stop)
			{
				_wake.WaitOne(5000);
			}
		}
	}

	public void Dispose()
	{
		_stop = true;
		_wake.Set();
	}
}
