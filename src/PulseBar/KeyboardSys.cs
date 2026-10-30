using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PulseBar;

// Настройки клавиатуры Windows (как в «Панель управления → Клавиатура»): сохраняются в профиле пользователя.
internal static class KeyboardSys
{
	private const uint SPIF_UPDATEINIFILE_SENDCHANGE = 3u;

	// Задержка перед повтором: 0 (≈250 мс) … 3 (≈1 с).
	public static int RepeatDelay
	{
		get
		{
			int v = 1;
			SystemParametersInfoW(0x16, 0, ref v, 0);
			return v;
		}
		set
		{
			SystemParametersInfoW(0x17, (uint)Math.Clamp(value, 0, 3), IntPtr.Zero, SPIF_UPDATEINIFILE_SENDCHANGE);
		}
	}

	// Скорость повтора: 0 (≈2,5 в секунду) … 31 (≈30 в секунду).
	public static int RepeatRate
	{
		get
		{
			int v = 31;
			SystemParametersInfoW(0x0A, 0, ref v, 0);
			return v;
		}
		set
		{
			SystemParametersInfoW(0x0B, (uint)Math.Clamp(value, 0, 31), IntPtr.Zero, SPIF_UPDATEINIFILE_SENDCHANGE);
		}
	}

	// Мигание курсора, мс; 0 — не мигает. Действует сразу и сохраняется в реестре (CursorBlinkRate, -1 — не мигает).
	public static int CaretBlinkMs
	{
		get
		{
			uint t = GetCaretBlinkTime();
			return (t == uint.MaxValue) ? 0 : (int)t;
		}
		set
		{
			uint t = (value <= 0) ? uint.MaxValue : (uint)Math.Clamp(value, 200, 1200);
			SetCaretBlinkTime(t);
			try
			{
				using RegistryKey key = Registry.CurrentUser.CreateSubKey("Control Panel\\Desktop");
				key.SetValue("CursorBlinkRate", (value <= 0) ? "-1" : t.ToString(System.Globalization.CultureInfo.InvariantCulture));
			}
			catch (Exception ex)
			{
				Log.Write("caret blink: " + ex.Message);
			}
		}
	}

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfoW(uint a, uint u, ref int v, uint f);

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfoW(uint a, uint u, nint v, uint f);

	[DllImport("user32.dll")]
	private static extern uint GetCaretBlinkTime();

	[DllImport("user32.dll")]
	private static extern bool SetCaretBlinkTime(uint ms);
}
