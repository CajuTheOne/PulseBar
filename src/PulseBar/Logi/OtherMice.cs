using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PulseBar.Logi;

internal static class OtherMice
{
	public static List<PointingDevice> Find()
	{
		List<HidInfo> source = Hid.Enumerate((string p) => p.Contains("vid_", StringComparison.OrdinalIgnoreCase) && !p.Contains("vid_046d", StringComparison.OrdinalIgnoreCase));
		List<PointingDevice> list = new List<PointingDevice>();
		foreach (IGrouping<(ushort, ushort), HidInfo> item in from h in source
			group h by (Vid: h.Vid, Pid: h.Pid))
		{
			if (!item.Any((HidInfo h) => h.UsagePage == 1 && h.Usage == 2))
			{
				continue;
			}
			string text = item.Select((HidInfo h) => h.Product).FirstOrDefault((string n) => n.Length > 0) ?? "";
			if (text.Length == 0)
			{
				text = $"USB mouse {item.Key.Item1:X4}:{item.Key.Item2:X4}";
			}
			PointingDevice pointingDevice = new PointingDevice
			{
				Name = text,
				Vid = item.Key.Item1,
				Pid = item.Key.Item2
			};
			if (pointingDevice.Vid == 5426)
			{
				pointingDevice.RazerPath = item.FirstOrDefault((HidInfo h) => h.FeatureLen == 91)?.Path;
			}
			list.Add(pointingDevice);
		}
		return list;
	}

	// Заряд мыши Razer: класс 7, команда 0x80 — заряд 0–255, 0x84 — заряжается. Synapse спрашивает ту же мышь через тот
	// же отчёт, и его ответ может затереть наш — тогда спросить снова (до трёх раз). Не прочитался — остаётся прежний.
	public static bool ReadBattery(PointingDevice d)
	{
		if (d.RazerPath == null)
		{
			return false;
		}
		int status = -1;
		try
		{
			using SafeFileHandle safeFileHandle = Hid.OpenSync(d.RazerPath);
			if (!safeFileHandle.IsInvalid)
			{
				byte[] array = ((d.RazerTid == 0) ? new byte[3] { 31, 255, 63 } : new byte[1] { d.RazerTid });
				for (int attempt = 0; attempt < 3; attempt++)
				{
					if (attempt > 0)
					{
						Thread.Sleep(150);
					}
					foreach (byte b in array)
					{
						byte[]? array2 = Razer(safeFileHandle, b, 7, 128, 2, out status);
						if (array2 != null)
						{
							d.RazerTid = b;
							d.BatteryPercent = (int)Math.Round((double)(array2[10] * 100) / 255.0);
							byte[]? array3 = Razer(safeFileHandle, b, 7, 132, 2, out _);
							d.Charging = array3 != null && array3[10] != 0;
							Report(d, 0);
							return true;
						}
						// Мышь спит (приёмник не дозвался) — повторять сейчас бесполезно.
						if (status == 4)
						{
							break;
						}
					}
					if (status == 4)
					{
						break;
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log.Write("razer: " + ex.Message);
			status = -2;
		}
		Report(d, status);
		return false;
	}

	private static void Report(PointingDevice d, int status)
	{
		if (status == d.RazerStatus)
		{
			return;
		}
		d.RazerStatus = status;
		Log.Write((status == 0) ? $"razer: {d.Name}: battery {d.BatteryPercent}%{(d.Charging ? ", charging" : "")}" : $"razer: {d.Name}: no battery reading ({Reason(status)})");
	}

	public static string Reason(int status)
	{
		return status switch
		{
			-1 => "cannot open the device",
			-2 => "error",
			-3 => "request not sent",
			-4 => "no answer",
			2 => "answer to another request",
			3 => "the mouse refused",
			4 => "the mouse did not answer, asleep or off",
			5 => "not supported",
			_ => $"status {status}"
		};
	}

	// status — байт состояния ответа: 1 — занято, 2 — готово, 3 — ошибка, 4 — мышь не ответила, 5 — не поддерживается;
	// отрицательный — запрос не прошёл (см. Reason).
	private static byte[]? Razer(SafeFileHandle h, byte tid, byte cls, byte cmd, byte size, out int status)
	{
		byte[] array = new byte[91];
		array[2] = tid;
		array[6] = size;
		array[7] = cls;
		array[8] = cmd;
		byte b = 0;
		for (int i = 3; i < 89; i++)
		{
			b ^= array[i];
		}
		array[89] = b;
		if (!Hid.SetFeature(h, array))
		{
			status = -3;
			return null;
		}
		byte[] array2 = new byte[91];
		status = -4;
		for (int j = 0; j < 8; j++)
		{
			Thread.Sleep(60);
			if (!Hid.GetFeature(h, array2))
			{
				status = -4;
				return null;
			}
			status = array2[1];
			if (array2[1] == 2 && array2[7] == cls && array2[8] == cmd)
			{
				return array2;
			}
			if (array2[1] != 1)
			{
				return null;
			}
		}
		return null;
	}
}
