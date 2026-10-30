using System.Runtime.InteropServices;

namespace PulseBar.Logi;

internal static class MouseSys
{
	private const uint Save = 3u;

	public static int Speed
	{
		get
		{
			int v = 10;
			SystemParametersInfoW(112u, 0u, ref v, 0u);
			return v;
		}
		set
		{
			SystemParametersInfoW(113u, 0u, value, 3u);
		}
	}

	public static bool Acceleration
	{
		get
		{
			int[] array = new int[3];
			SystemParametersInfoW(3u, 0u, array, 0u);
			return array[2] != 0;
		}
		set
		{
			int[] v = ((!value) ? new int[3] : new int[3] { 6, 10, 1 });
			SystemParametersInfoW(4u, 0u, v, 3u);
		}
	}

	public static int WheelLines
	{
		get
		{
			int v = 3;
			SystemParametersInfoW(104u, 0u, ref v, 0u);
			return v;
		}
		set
		{
			SystemParametersInfoW(105u, (uint)value, 0, 3u);
		}
	}

	public static int DoubleClickMs
	{
		get
		{
			return (int)GetDoubleClickTime();
		}
		set
		{
			SystemParametersInfoW(32u, (uint)value, 0, 3u);
		}
	}

	public static bool Swapped
	{
		get
		{
			return GetSystemMetrics(23) != 0;
		}
		set
		{
			SystemParametersInfoW(33u, value ? 1u : 0u, 0, 3u);
		}
	}

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfoW(uint a, uint u, ref int v, uint f);

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfoW(uint a, uint u, int[] v, uint f);

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfoW(uint a, uint u, nint v, uint f);

	[DllImport("user32.dll")]
	private static extern uint GetDoubleClickTime();

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int i);
}
