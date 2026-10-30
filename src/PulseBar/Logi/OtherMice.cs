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

	public static bool ReadBattery(PointingDevice d)
	{
		if (d.RazerPath == null)
		{
			return false;
		}
		try
		{
			using SafeFileHandle safeFileHandle = Hid.OpenSync(d.RazerPath);
			if (safeFileHandle.IsInvalid)
			{
				return false;
			}
			byte[] array = ((d.RazerTid == 0) ? new byte[3] { 31, 255, 63 } : new byte[1] { d.RazerTid });
			foreach (byte b in array)
			{
				byte[] array2 = Razer(safeFileHandle, b, 7, 128, 2);
				if (array2 != null)
				{
					d.RazerTid = b;
					d.BatteryPercent = (int)Math.Round((double)(array2[10] * 100) / 255.0);
					byte[] array3 = Razer(safeFileHandle, b, 7, 132, 2);
					d.Charging = array3 != null && array3[10] != 0;
					return true;
				}
			}
		}
		catch (Exception ex)
		{
			Log.Write("razer: " + ex.Message);
		}
		return false;
	}

	private static byte[]? Razer(SafeFileHandle h, byte tid, byte cls, byte cmd, byte size)
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
			return null;
		}
		byte[] array2 = new byte[91];
		for (int j = 0; j < 5; j++)
		{
			Thread.Sleep(60);
			if (!Hid.GetFeature(h, array2))
			{
				return null;
			}
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
