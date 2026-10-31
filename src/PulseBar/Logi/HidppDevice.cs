using System;
using System.Collections.Generic;
using System.Text;

namespace PulseBar.Logi;

internal sealed class HidppDevice
{
	public const ushort FeatureSet = 1;

	public const ushort DeviceName = 5;

	public const ushort BatteryLevel = 4096;

	public const ushort BatteryVoltage = 4097;

	public const ushort UnifiedBattery = 4100;

	public const ushort RgbEffects = 32881;

	public const ushort PerKeyRgb = 32897;

	public const ushort GKeys = 32784;

	public const ushort MKeys = 32800;

	public const ushort MacroRecord = 32816;

	public const ushort Brightness = 32832;

	public const ushort OnboardProfiles = 33024;

	public const ushort AdjustableDpi = 8705;

	private readonly Dictionary<ushort, byte> _features = new Dictionary<ushort, byte>();

	public HidppChannel Channel { get; }

	public byte Index { get; }

	public int Protocol { get; private set; }

	public string Name { get; private set; } = "";

	public bool Online { get; set; }

	public int BatteryPercent { get; set; } = -1;

	public bool Charging { get; set; }

	public bool IsKeyboard
	{
		get
		{
			if (!Has(32784) && !Has(32881))
			{
				return Name.Contains("KEYBOARD", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
	}

	public bool IsMouse => !IsKeyboard;

	private HidppDevice(HidppChannel ch, byte index)
	{
		Channel = ch;
		Index = index;
	}

	public IReadOnlyDictionary<ushort, byte> Features => _features;

	public bool Has(ushort feature)
	{
		return _features.ContainsKey(feature);
	}

	public byte Feat(ushort feature)
	{
		if (!_features.TryGetValue(feature, out var value))
		{
			return 0;
		}
		return value;
	}

	public byte[]? Call(ushort feature, byte fn, params byte[] args)
	{
		byte b = Feat(feature);
		if (b == 0)
		{
			return null;
		}
		return Channel.Call(Index, b, fn, args);
	}

	public static HidppDevice? Probe(HidppChannel ch, byte index, string pairedName)
	{
		HidppDevice hidppDevice = new HidppDevice(ch, index)
		{
			Name = pairedName
		};
		if (ch.Call(index, 0, 1, new byte[3] { 0, 0, 90 }, out var error, 1500) == null)
		{
			if (error == 1 && ch.IsReceiver)
			{
				hidppDevice.Protocol = 1;
				hidppDevice.Online = true;
				return hidppDevice;
			}
			if (pairedName.Length <= 0)
			{
				return null;
			}
			return hidppDevice;
		}
		hidppDevice.Online = hidppDevice.LoadFeatures();
		return hidppDevice;
	}

	public bool LoadFeatures()
	{
		int error;
		byte[] array = Channel.Call(Index, 0, 1, new byte[3] { 0, 0, 90 }, out error, 1500);
		if (array == null)
		{
			if (error != 1)
			{
				return false;
			}
			Protocol = 1;
			return true;
		}
		Protocol = array[0];
		byte[] array2 = Channel.Call(Index, 0, 0, 0, 1);
		if (array2 == null || array2[0] == 0)
		{
			return true;
		}
		byte feat = array2[0];
		byte[] array3 = Channel.Call(Index, feat, 0);
		if (array3 == null)
		{
			return true;
		}
		_features.Clear();
		for (int i = 1; i <= array3[0]; i++)
		{
			byte[] array4 = Channel.Call(Index, feat, 1, (byte)i);
			if (array4 != null)
			{
				_features[(ushort)((array4[0] << 8) | array4[1])] = (byte)i;
			}
		}
		if (Has(5))
		{
			byte[] array5 = Call(5, 0);
			if (array5 != null)
			{
				StringBuilder stringBuilder = new StringBuilder();
				while (stringBuilder.Length < array5[0])
				{
					byte[] array6 = Call(5, 1, (byte)stringBuilder.Length);
					if (array6 == null)
					{
						break;
					}
					byte[] array7 = array6;
					foreach (byte b in array7)
					{
						if (b == 0 || stringBuilder.Length >= array5[0])
						{
							break;
						}
						stringBuilder.Append((char)b);
					}
				}
				if (stringBuilder.Length > 0)
				{
					Name = stringBuilder.ToString().Trim();
				}
			}
		}
		return true;
	}

	public bool ReadBattery()
	{
		if (Protocol == 1)
		{
			byte[] array = Channel.Register(Index, 129, 13);
			if (array != null)
			{
				byte b = array[0];
				if (b > 0 && b <= 100)
				{
					BatteryPercent = array[0];
					return true;
				}
			}
			array = Channel.Register(Index, 129, 7);
			if (array != null && array[0] > 0)
			{
				BatteryPercent = Math.Clamp(array[0] * 100 / 7, 5, 100);
				return true;
			}
			return false;
		}
		if (Has(4100))
		{
			byte[] array2 = Call(4100, 1);
			if (array2 == null)
			{
				return false;
			}
			int batteryPercent = ((array2[0] <= 0) ? (array2[1] switch
			{
				8 => 90, 
				4 => 50, 
				2 => 20, 
				1 => 5, 
				_ => -1, 
			}) : array2[0]);
			BatteryPercent = batteryPercent;
			byte b = array2[2];
			bool charging = (uint)(b - 1) <= 1u;
			Charging = charging;
			return true;
		}
		if (Has(4097))
		{
			byte[] array3 = Call(4097, 0);
			if (array3 == null)
			{
				return false;
			}
			int mv = (array3[0] << 8) | array3[1];
			Charging = (array3[2] & 0x80) != 0;
			BatteryPercent = VoltageToPercent(mv);
			return true;
		}
		if (Has(4096))
		{
			byte[] array4 = Call(4096, 0);
			if (array4 == null)
			{
				return false;
			}
			BatteryPercent = array4[0];
			byte b = array4[2];
			bool charging = (uint)(b - 1) <= 2u;
			Charging = charging;
			return true;
		}
		return false;
	}

	private static int VoltageToPercent(int mv)
	{
		ReadOnlySpan<(int, int)> readOnlySpan = [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];
		if (mv >= readOnlySpan[0].Item1)
		{
			return 100;
		}
		for (int i = 1; i < readOnlySpan.Length; i++)
		{
			if (mv >= readOnlySpan[i].Item1)
			{
				var (num, num2) = readOnlySpan[i - 1];
				var (num3, num4) = readOnlySpan[i];
				return num4 + (mv - num3) * (num2 - num4) / (num - num3);
			}
		}
		return 0;
	}
}
