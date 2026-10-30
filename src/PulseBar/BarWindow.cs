using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PulseBar;

// Окно самой строки. Обычно — дочернее окно панели задач (Shell_TrayWnd), как у Claude Code Usage Monitor:
// Проводник сам держит его над панелью, прячет в полноэкранном режиме и при автоскрытии. Если встроиться
// не удалось (или выключено в меню) — прежнее окно «поверх всех» над панелью.
// Окно уничтожается вместе с панелью при перезапуске Проводника; Overlay создаёт его заново.
internal sealed class BarWindow : NativeWindow
{
	private const int WS_POPUP = unchecked((int)0x8000_0000);

	private const int WS_CHILD = 0x4000_0000;

	private const int WS_CLIPSIBLINGS = 0x0400_0000;

	private const int WS_EX_TOPMOST = 0x8;

	private const int WS_EX_TOOLWINDOW = 0x80;

	private const int WS_EX_LAYERED = 0x8_0000;

	private const int WS_EX_NOACTIVATE = 0x800_0000;

	private long _lastClick;

	public event Action? RightClick;

	public event Action? DoubleClick;

	// x в пикселях окна строки; Leave — мышь ушла со строки.
	public event Action<int>? Hover;

	public event Action? Leave;

	private bool _tracking;

	public bool Embedded { get; private set; }

	public bool Exists => Handle != IntPtr.Zero && Native.IsWindow(Handle) != 0;

	public bool Create(nint tray, bool embed)
	{
		if (embed && tray != IntPtr.Zero && Create(new CreateParams
		{
			Caption = "PulseBar",
			Parent = tray,
			Style = WS_CHILD | WS_CLIPSIBLINGS,
			ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
		}))
		{
			Embedded = true;
			return true;
		}
		Embedded = false;
		return Create(new CreateParams
		{
			Caption = "PulseBar",
			X = -32000,
			Y = -32000,
			Width = 1,
			Height = 1,
			Style = WS_POPUP,
			ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
		});
	}

	private bool Create(CreateParams cp)
	{
		try
		{
			CreateHandle(cp);
			return true;
		}
		catch (Exception ex)
		{
			Log.Write($"bar window ({(cp.Parent != IntPtr.Zero ? "in taskbar" : "over taskbar")}): {ex.Message}");
			return false;
		}
	}

	public void Destroy()
	{
		if (Handle != IntPtr.Zero)
		{
			DestroyHandle();
		}
	}

	protected override void WndProc(ref Message m)
	{
		switch (m.Msg)
		{
		case 0x21: // WM_MOUSEACTIVATE: не забирать фокус у панели и программ
			m.Result = 3; // MA_NOACTIVATE
			return;
		case 0x201: // WM_LBUTTONDOWN; двойной щелчок считаем сами — класс окна без CS_DBLCLKS
		{
			long now = Environment.TickCount64;
			if (now - _lastClick <= GetDoubleClickTime())
			{
				_lastClick = 0;
				DoubleClick?.Invoke();
			}
			else
			{
				_lastClick = now;
			}
			return;
		}
		case 0x205: // WM_RBUTTONUP
			RightClick?.Invoke();
			return;
		case 0x200: // WM_MOUSEMOVE
			if (!_tracking)
			{
				TRACKMOUSEEVENT t = new TRACKMOUSEEVENT
				{
					Size = Marshal.SizeOf<TRACKMOUSEEVENT>(),
					Flags = 2, // TME_LEAVE
					Hwnd = Handle
				};
				_tracking = TrackMouseEvent(ref t);
			}
			Hover?.Invoke((short)(m.LParam.ToInt64() & 0xFFFF));
			return;
		case 0x2A3: // WM_MOUSELEAVE
			_tracking = false;
			Leave?.Invoke();
			return;
		}
		base.WndProc(ref m);
	}

	[DllImport("user32.dll")]
	private static extern uint GetDoubleClickTime();

	private struct TRACKMOUSEEVENT
	{
		public int Size;

		public uint Flags;

		public nint Hwnd;

		public uint HoverTime;
	}

	[DllImport("user32.dll")]
	private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
}
