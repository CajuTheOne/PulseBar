using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using PulseBar.Logi;

namespace PulseBar;

internal sealed class Overlay : Form
{
	private const int TimerId = 1;

	private const int RecheckTimerId = 2;

	private const int WM_DPICHANGED = 736;

	private readonly Settings _s;

	private readonly Sensors _sensors;

	private readonly BarRenderer _renderer = new BarRenderer();

	private readonly Native.WinEventProc _foregroundProc;

	private readonly uint _msgTaskbarCreated;

	private Palette _palette;

	private bool _lightTaskbar;

	private long _themeCheckAt;

	private long _trimAt = Environment.TickCount64 + 8000;

	private nint _tray;

	private nint _notify;

	private nint _hook;

	private nint _prevForeground;

	private bool _visible;

	private bool _dirty = true;

	private Native.RECT _placed;

	private int _signature;

	private Readings _r;

	private ContextMenuStrip? _menu;

	private nint _memDc;

	private nint _dib;

	private nint _bits;

	private Bitmap? _bmp;

	private Graphics? _g;

	private int _dibW;

	private int _dibH;

	private readonly LogiService _logi;

	private NotifyIcon? _trayIcon;

	private Icon? _trayIco;

	private LogiForm? _logiForm;

	private readonly BarWindow _bar = new BarWindow();

	private readonly Updater _updater = new Updater();

	private long _updateCheckAt = Environment.TickCount64 + 60000;

	// Сама форма — невидимое окно для сообщений, таймеров и меню; строку рисует _bar.
	protected override CreateParams CreateParams
	{
		get
		{
			CreateParams createParams = base.CreateParams;
			createParams.Style = int.MinValue;
			createParams.ExStyle |= 0x8000080;
			return createParams;
		}
	}

	protected override bool ShowWithoutActivation => true;

	protected override void SetVisibleCore(bool value)
	{
		if (!base.IsHandleCreated)
		{
			CreateHandle();
		}
		base.SetVisibleCore(value: false);
	}

	public Overlay(Settings s)
	{
		_s = s;
		L.Set(s.Language);
		_sensors = new Sensors(s.Gpu);
		_lightTaskbar = ReadLightTaskbar();
		_palette = Palette.Create(_lightTaskbar, s.BlueNormal);
		_foregroundProc = OnForegroundChanged;
		_msgTaskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
		_logi = new LogiService(LogiConfig.Load());
		_logi.Changed += OnLogiChanged;
		Log.Write($"start {Updater.Current}: admin={Environment.IsPrivilegedProcess}; GPU: {_sensors.Gpu.Current?.Name ?? "none"}");
		Text = "PulseBar";
		base.FormBorderStyle = FormBorderStyle.None;
		base.ShowInTaskbar = false;
		base.StartPosition = FormStartPosition.Manual;
		base.Location = new Point(-32000, -32000);
		base.Size = new Size(1, 1);
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		Native.ChangeWindowMessageFilterEx(base.Handle, _msgTaskbarCreated, 1u, IntPtr.Zero);
		Native.ChangeWindowMessageFilterEx(base.Handle, 26u, 1u, IntPtr.Zero);
		_hook = Native.SetWinEventHook(3u, 3u, IntPtr.Zero, _foregroundProc, 0u, 0u, 2u);
		_bar.RightClick += ShowMenu;
		_bar.DoubleClick += OpenTaskManager;
		StartTimer();
		BeginInvoke(Tick);
		UpdateTrayIcon();
		_logi.Start();
	}

	private void StartTimer()
	{
		Native.KillTimer(base.Handle, 1);
		Native.SetCoalescableTimer(base.Handle, 1, (uint)_s.IntervalMs, IntPtr.Zero, (uint)Math.Min(_s.IntervalMs / 10, 100));
	}

	protected override void WndProc(ref Message m)
	{
		switch (m.Msg)
		{
		case 275:
			if (m.WParam == 1)
			{
				Tick();
				return;
			}
			if (m.WParam == 2)
			{
				Native.KillTimer(base.Handle, 2);
				EnsureAboveTaskbar();
				return;
			}
			break;
		case 1024:
			BeginUpdateCheck();
			return;
		case 736:
			_dirty = true;
			return;
		case 26:
			_themeCheckAt = 0L;
			break;
		case 537:
			if (m.WParam == 32768 || m.WParam == 32772 || m.WParam == 7)
			{
				_logi.DevicesChanged();
			}
			break;
		}
		if (_msgTaskbarCreated != 0 && m.Msg == _msgTaskbarCreated)
		{
			_tray = IntPtr.Zero;
			_dirty = true;
			if (_bar.Embedded)
			{
				_bar.Destroy();
			}
		}
		base.WndProc(ref m);
	}

	private void Tick()
	{
		ContextMenuStrip menu = _menu;
		if (menu != null && menu.Visible)
		{
			EnsureAboveTaskbar();
			return;
		}
		if (!TryGetPlacement(out var _))
		{
			SetShown(show: false);
			return;
		}
		_r = _sensors.Read();
		FillBattery();
		long tickCount = Environment.TickCount64;
		if (tickCount >= _updateCheckAt)
		{
			_updateCheckAt = tickCount + 86400000;
			BeginUpdateCheck();
		}
		if (_trimAt != 0L && tickCount >= _trimAt)
		{
			_trimAt = 0L;
			Native.TrimMemory();
		}
		if (tickCount >= _themeCheckAt)
		{
			_themeCheckAt = tickCount + 10000;
			bool flag = ReadLightTaskbar();
			if (flag != _lightTaskbar)
			{
				_lightTaskbar = flag;
				_palette = Palette.Create(flag, _s.BlueNormal);
				_dirty = true;
			}
		}
		UpdateOverlay();
	}

	private void OnForegroundChanged(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
	{
		ContextMenuStrip menu = _menu;
		if (menu != null && menu.Visible)
		{
			EnsureAboveTaskbar();
		}
		else
		{
			UpdateOverlay();
		}
		RecheckSoon();
		_logi.OnForeground(hwnd);
	}

	private void UpdateOverlay()
	{
		if (!TryGetPlacement(out var target))
		{
			SetShown(show: false);
			return;
		}
		if (target.Width != _dibW || target.Height != _dibH)
		{
			CreateBuffer(target.Width, target.Height);
			_dirty = true;
		}
		int num = BarRenderer.Signature(in _r, _s, _lightTaskbar) ^ ((_updater.Available != null) ? 1540483477 : 0);
		bool flag = target.Left != _placed.Left || target.Top != _placed.Top || target.Width != _placed.Width || target.Height != _placed.Height;
		if (_dirty || num != _signature)
		{
			_g.Clear(Color.FromArgb(1, 0, 0, 0));
			_renderer.Draw(_g, in _r, _s, _palette);
			if (_updater.Available != null)
			{
				DrawUpdateDot();
			}
			_g.Flush(FlushIntention.Sync);
			_signature = num;
			_dirty = false;
			Present(target);
		}
		else if (flag)
		{
			Present(target);
		}
		_placed = target;
		SetShown(show: true);
		EnsureAboveTaskbar();
	}

	private unsafe bool TryGetPlacement(out Native.RECT target)
	{
		target = default(Native.RECT);
		if (_tray == IntPtr.Zero || Native.IsWindow(_tray) == 0)
		{
			_tray = Native.FindWindowW("Shell_TrayWnd", null);
			_notify = ((_tray != IntPtr.Zero) ? Native.FindWindowExW(_tray, IntPtr.Zero, "TrayNotifyWnd", null) : IntPtr.Zero);
		}
		if (_tray == IntPtr.Zero || Native.IsWindowVisible(_tray) == 0 || Native.GetWindowRect(_tray, out var r) == 0)
		{
			return false;
		}
		if (!EnsureBar())
		{
			return false;
		}
		if (r.Height <= 0 || r.Height > r.Width)
		{
			return false;
		}
		nint num = Native.MonitorFromWindow(_tray, 2u);
		Native.MONITORINFO mi = new Native.MONITORINFO
		{
			CbSize = sizeof(Native.MONITORINFO)
		};
		if (Native.GetMonitorInfoW(num, ref mi) == 0)
		{
			return false;
		}
		if (Math.Min(r.Bottom, mi.Monitor.Bottom) - Math.Max(r.Top, mi.Monitor.Top) < r.Height / 2)
		{
			return false;
		}
		if (_s.HideFullscreen && IsFullscreenOn(num, mi.Monitor))
		{
			return false;
		}
		float num2 = (float)Native.GetDpiForWindow(_tray) / 96f;
		if (num2 <= 0f)
		{
			num2 = 1f;
		}
		if (_renderer.Configure(num2, r.Height, _s))
		{
			_dirty = true;
		}
		int num3 = r.Right - (int)(400f * num2);
		if (_notify != IntPtr.Zero && Native.GetWindowRect(_notify, out var r2) != 0 && r2.Left > r.Left && r2.Left < r.Right)
		{
			num3 = r2.Left;
		}
		num3 -= (int)MathF.Round((float)_s.TrayGap * num2);
		target = new Native.RECT
		{
			Left = num3 - _renderer.Width,
			Top = r.Top,
			Right = num3,
			Bottom = r.Bottom
		};
		return true;
	}

	private bool IsFullscreenOn(nint monitor, Native.RECT m)
	{
		nint foregroundWindow = Native.GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero || foregroundWindow == base.Handle || foregroundWindow == _bar.Handle || foregroundWindow == _tray)
		{
			return false;
		}
		if (Native.MonitorFromWindow(foregroundWindow, 2u) != monitor)
		{
			return false;
		}
		if (Native.GetWindowRect(foregroundWindow, out var r) == 0)
		{
			return false;
		}
		if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom)
		{
			return false;
		}
		bool flag;
		switch (Native.ClassName(foregroundWindow))
		{
		case "Progman":
		case "WorkerW":
		case "Shell_TrayWnd":
		case "Shell_SecondaryTrayWnd":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		return !flag;
	}

	private void SetShown(bool show)
	{
		if (show != _visible)
		{
			_visible = show;
			Native.ShowWindow(_bar.Handle, show ? 4 : 0);
		}
	}

	private void RecheckSoon()
	{
		Native.SetCoalescableTimer(base.Handle, 2, 120u, IntPtr.Zero, 0u);
	}

	private void EnsureAboveTaskbar()
	{
		if (!_visible || _tray == IntPtr.Zero || !_bar.Exists)
		{
			return;
		}
		if (_bar.Embedded)
		{
			if (Native.GetWindow(_tray, 5u) != _bar.Handle)
			{
				Native.SetWindowPos(_bar.Handle, IntPtr.Zero, 0, 0, 0, 0, 19u);
			}
			return;
		}
		int num = 0;
		nint window = Native.GetWindow(_bar.Handle, 3u);
		while (window != IntPtr.Zero && num < 256)
		{
			if (window == _tray)
			{
				nint window2 = Native.GetWindow(_tray, 3u);
				bool flag = window2 != IntPtr.Zero && (Native.GetWindowLongPtrW(window2, -20) & 8) != 0;
				Native.SetWindowPos(_bar.Handle, flag ? window2 : Native.HWND_TOPMOST, 0, 0, 0, 0, 531u);
				break;
			}
			window = Native.GetWindow(window, 3u);
			num++;
		}
	}

	private unsafe void CreateBuffer(int w, int h)
	{
		FreeBuffer();
		Native.BITMAPINFOHEADER bITMAPINFOHEADER = new Native.BITMAPINFOHEADER
		{
			Size = sizeof(Native.BITMAPINFOHEADER),
			Width = w,
			Height = -h,
			Planes = 1,
			BitCount = 32
		};
		_memDc = Native.CreateCompatibleDC(IntPtr.Zero);
		_dib = Native.CreateDIBSection(_memDc, &bITMAPINFOHEADER, 0u, out _bits, IntPtr.Zero, 0u);
		Native.SelectObject(_memDc, _dib);
		_bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, _bits);
		_g = Graphics.FromImage(_bmp);
		_dibW = w;
		_dibH = h;
	}

	private void FreeBuffer()
	{
		_g?.Dispose();
		_bmp?.Dispose();
		if (_memDc != IntPtr.Zero)
		{
			Native.DeleteDC(_memDc);
		}
		if (_dib != IntPtr.Zero)
		{
			Native.DeleteObject(_dib);
		}
		nint zero = IntPtr.Zero;
		nint zero2 = IntPtr.Zero;
		nint zero3 = IntPtr.Zero;
		_g = null;
		_bmp = null;
		_memDc = zero;
		_dib = zero2;
		_bits = zero3;
		_dibW = 0;
		_dibH = 0;
	}

	private unsafe void Present(Native.RECT t)
	{
		Native.POINT pOINT = new Native.POINT
		{
			X = t.Left,
			Y = t.Top
		};
		Native.POINT pOINT2 = default(Native.POINT);
		Native.SIZE sIZE = new Native.SIZE
		{
			Cx = _dibW,
			Cy = _dibH
		};
		Native.BLENDFUNCTION bLENDFUNCTION = new Native.BLENDFUNCTION
		{
			SourceConstantAlpha = byte.MaxValue,
			AlphaFormat = 1
		};
		if (_bar.Embedded)
		{
			// Дочернее окно: координаты в клиентской области панели, положение задаёт SetWindowPos.
			Native.GetWindowRect(_tray, out var r);
			Native.SetWindowPos(_bar.Handle, IntPtr.Zero, t.Left - r.Left, t.Top - r.Top, _dibW, _dibH, 16u);
			Native.UpdateLayeredWindow(_bar.Handle, IntPtr.Zero, null, &sIZE, _memDc, &pOINT2, 0u, &bLENDFUNCTION, 2u);
			return;
		}
		Native.UpdateLayeredWindow(_bar.Handle, IntPtr.Zero, &pOINT, &sIZE, _memDc, &pOINT2, 0u, &bLENDFUNCTION, 2u);
	}

	private static bool ReadLightTaskbar()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
			return registryKey?.GetValue("SystemUsesLightTheme") is int num && num != 0;
		}
		catch
		{
			return false;
		}
	}

	private void ShowMenu()
	{
		_menu?.Dispose();
		_menu = BuildMenu();
		_prevForeground = Native.GetForegroundWindow();
		Native.SetForegroundWindow(base.Handle);
		_menu.Show(System.Windows.Forms.Cursor.Position);
	}

	private ContextMenuStrip BuildMenu()
	{
		ToolStripManager.Renderer = (_lightTaskbar ? new ToolStripProfessionalRenderer() : new DarkMenuRenderer());
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		ToolStripItemCollection items = contextMenuStrip.Items;
		Updater.Release available = _updater.Available;
		if (available != null)
		{
			items.Add(new ToolStripMenuItem(_updater.Busy ? L.F("Downloading {0}…", available.Version) : L.F("Update to {0}", available.Version), null, delegate
			{
				InstallUpdate();
			})
			{
				Enabled = !_updater.Busy,
				Font = new Font(contextMenuStrip.Font, FontStyle.Bold)
			});
			items.Add(new ToolStripSeparator());
		}
		if (_logi.Config.Enabled && _logi.Config.Profiles.Count > 1)
		{
			ToolStripMenuItem toolStripMenuItem = Sub(items, L.T("Profile"));
			for (int i = 0; i < _logi.Config.Profiles.Count; i++)
			{
				int n = i;
				Radio(toolStripMenuItem.DropDownItems, ((i < 3) ? $"M{i + 1} · " : "") + _logi.Config.Profiles[i].Name, _logi.Current == i, delegate
				{
					_logi.SelectProfile(n);
				});
			}
		}
		Item(items, L.T("Keyboard and mouse…"), OpenLogi);
		items.Add(new ToolStripSeparator());
		Radio(items, L.T("One line"), _s.Layout == 0, delegate
		{
			_s.Layout = 0;
		});
		Radio(items, L.T("Two lines, vertical labels"), _s.Layout == 1, delegate
		{
			_s.Layout = 1;
		});
		items.Add(new ToolStripSeparator());
		ToolStripMenuItem toolStripMenuItem2 = Sub(items, L.T("Normal color"));
		Radio(toolStripMenuItem2.DropDownItems, L.T("Green"), !_s.BlueNormal, delegate
		{
			_s.BlueNormal = false;
		});
		Radio(toolStripMenuItem2.DropDownItems, L.T("Blue"), _s.BlueNormal, delegate
		{
			_s.BlueNormal = true;
		});
		ToolStripMenuItem toolStripMenuItem3 = Sub(items, L.T("Text size"));
		string[] array = new string[3]
		{
			L.T("Small"),
			L.T("Normal"),
			L.T("Large")
		};
		for (int num = 0; num < array.Length; num++)
		{
			int v = num;
			Radio(toolStripMenuItem3.DropDownItems, array[num], _s.TextSize == v, delegate
			{
				_s.TextSize = v;
			});
		}
		ToolStripMenuItem toolStripMenuItem4 = Sub(items, L.T("Spacing between blocks"));
		toolStripMenuItem4.Enabled = _s.Layout != 0;
		int[] array2 = new int[7] { 6, 10, 14, 20, 26, 32, 40 };
		foreach (int px in array2)
		{
			Radio(toolStripMenuItem4.DropDownItems, $"{px} px", _s.GroupGap == px, delegate
			{
				_s.GroupGap = px;
			});
		}
		ToolStripMenuItem toolStripMenuItem5 = Sub(items, L.T("Spacing from tray"));
		for (int num3 = 0; num3 <= 100; num3 += 10)
		{
			int px2 = num3;
			Radio(toolStripMenuItem5.DropDownItems, $"{px2} px", _s.TrayGap == px2, delegate
			{
				_s.TrayGap = px2;
			});
		}
		ToolStripMenuItem toolStripMenuItem6 = Sub(items, L.T("Update interval"));
		array2 = new int[4] { 500, 1000, 2000, 5000 };
		foreach (int ms in array2)
		{
			Radio(toolStripMenuItem6.DropDownItems, (ms < 1000) ? L.T("0.5 s") : L.F("{0} s", ms / 1000), _s.IntervalMs == ms, delegate
			{
				_s.IntervalMs = ms;
				StartTimer();
			});
		}
		if (_sensors.Gpu.Adapters.Count > 1)
		{
			ToolStripMenuItem toolStripMenuItem7 = Sub(items, L.T("Graphics card"));
			Radio(toolStripMenuItem7.DropDownItems, L.T("Auto (discrete)"), _s.Gpu == "", delegate
			{
				SelectGpu("");
			});
			foreach (GpuSensor.Adapter a in _sensors.Gpu.Adapters)
			{
				Radio(toolStripMenuItem7.DropDownItems, a.Name, _s.Gpu == a.Key, delegate
				{
					SelectGpu(a.Key);
				});
			}
		}
		Check(items, L.T("Show VRAM"), _s.ShowVram, delegate(bool showVram)
		{
			_s.ShowVram = showVram;
		});
		Check(items, L.T("Show memory amount (GB)"), _s.ShowRamGb, delegate(bool showRamGb)
		{
			_s.ShowRamGb = showRamGb;
		});
		Item(items, L.T("Thresholds…"), ShowThresholds);
		ToolStripMenuItem toolStripMenuItem8 = Sub(items, (L.Code == "en") ? "Language" : (L.T("Language") + " (Language)"));
		(string, string)[] languages = L.Languages;
		for (int num4 = 0; num4 < languages.Length; num4++)
		{
			var (code, text) = languages[num4];
			Radio(toolStripMenuItem8.DropDownItems, text, L.Code == code, delegate
			{
				_s.Language = code;
				L.Set(code);
			});
		}
		items.Add(new ToolStripSeparator());
		Check(items, L.T("Hide in full-screen apps"), _s.HideFullscreen, delegate(bool hideFullscreen)
		{
			_s.HideFullscreen = hideFullscreen;
		});
		Check(items, L.T("Show keyboard and mouse battery"), _s.ShowBattery, delegate(bool showBattery)
		{
			_s.ShowBattery = showBattery;
		});
		Check(items, L.T("Show tray icon"), _s.TrayIcon, delegate(bool trayIcon)
		{
			_s.TrayIcon = trayIcon;
			UpdateTrayIcon();
		});
		Check(items, L.T("Embed in the taskbar"), _s.Embed, delegate(bool embed)
		{
			_s.Embed = embed;
			_bar.Destroy();
			_visible = false;
			_dirty = true;
		});
		Check(items, L.T("Start with Windows"), Autostart.IsEnabled(), SetAutostart, save: false);
		Check(items, L.T("Get beta versions"), _s.Beta, delegate(bool beta)
		{
			_s.Beta = beta;
			Native.PostMessageW(base.Handle, 1024u, IntPtr.Zero, IntPtr.Zero);
		});
		if (!_sensors.CpuTemp.Available)
		{
			items.Add(new ToolStripMenuItem(L.F("CPU temperature: {0}", _sensors.CpuTemp.Status))
			{
				Enabled = false
			});
		}
		Item(items, L.T("Task Manager"), OpenTaskManager);
		items.Add(new ToolStripSeparator());
		items.Add(new ToolStripMenuItem("PulseBar " + Updater.Current)
		{
			Enabled = false
		});
		Item(items, L.T("Exit"), delegate
		{
			BeginInvoke(base.Close);
		});
		contextMenuStrip.Closed += delegate
		{
			_trimAt = Environment.TickCount64 + 3000;
			BeginInvoke(delegate
			{
				if (Native.GetForegroundWindow() == base.Handle && _prevForeground != IntPtr.Zero)
				{
					Native.SetForegroundWindow(_prevForeground);
				}
				EnsureAboveTaskbar();
				RecheckSoon();
			});
		};
		return contextMenuStrip;
	}

	private static ToolStripMenuItem Sub(ToolStripItemCollection items, string text)
	{
		ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(text);
		items.Add(toolStripMenuItem);
		return toolStripMenuItem;
	}

	private void Radio(ToolStripItemCollection items, string text, bool selected, Action apply)
	{
		items.Add(new ToolStripMenuItem(text, null, delegate
		{
			apply();
			Changed();
		})
		{
			Checked = selected,
			Tag = "radio"
		});
	}

	private void Check(ToolStripItemCollection items, string text, bool value, Action<bool> apply, bool save = true)
	{
		items.Add(new ToolStripMenuItem(text, null, delegate
		{
			apply(!value);
			if (save)
			{
				Changed();
			}
		})
		{
			Checked = value
		});
	}

	private static void Item(ToolStripItemCollection items, string text, Action action)
	{
		items.Add(new ToolStripMenuItem(text, null, delegate
		{
			action();
		}));
	}

	private void Changed()
	{
		_s.Save();
		_palette = Palette.Create(_lightTaskbar, _s.BlueNormal);
		_dirty = true;
		UpdateOverlay();
	}

	private void SelectGpu(string key)
	{
		_s.Gpu = key;
		_sensors.Gpu.Open(key);
		_r = _sensors.Read();
	}

	private void ShowThresholds()
	{
		using (ThresholdsForm thresholdsForm = new ThresholdsForm(_s, !_lightTaskbar))
		{
			if (thresholdsForm.ShowDialog() == DialogResult.OK)
			{
				Changed();
			}
		}
		_trimAt = Environment.TickCount64 + 3000;
	}

	private void SetAutostart(bool enable)
	{
		if (!(enable ? Autostart.Enable(Environment.ProcessPath) : Autostart.Disable()))
		{
			MessageBox.Show(L.T("Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."), "PulseBar", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
		}
	}

	// Строка создаётся при первом показе и заново после перезапуска Проводника.
	private bool EnsureBar()
	{
		if (_bar.Exists)
		{
			return true;
		}
		_bar.Destroy();
		_visible = false;
		_placed = default(Native.RECT);
		_dirty = true;
		if (!_bar.Create(_tray, _s.Embed))
		{
			return false;
		}
		Log.Write(_bar.Embedded ? "bar: inside the taskbar" : "bar: over the taskbar");
		return true;
	}

	private async void BeginUpdateCheck()
	{
		bool had = _updater.Available != null;
		await _updater.CheckAsync(_s.Beta);
		if (had != (_updater.Available != null))
		{
			_dirty = true;
			UpdateOverlay();
		}
	}

	private async void InstallUpdate()
	{
		if (await _updater.InstallAsync())
		{
			BeginInvoke(base.Close);
		}
		else
		{
			MessageBox.Show(L.T("Could not install the update (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."), "PulseBar", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
		}
	}

	// Точка «есть обновление» в правом верхнем углу строки.
	private void DrawUpdateDot()
	{
		float d = Math.Max(4f, _dibH / 8f);
		SmoothingMode smoothingMode = _g.SmoothingMode;
		_g.SmoothingMode = SmoothingMode.AntiAlias;
		using (SolidBrush brush = new SolidBrush(Color.FromArgb(76, 194, 255)))
		{
			_g.FillEllipse(brush, _dibW - d - 1f, 1f, d, d);
		}
		_g.SmoothingMode = smoothingMode;
	}

	private static void OpenTaskManager()
	{
		try
		{
			Process.Start(new ProcessStartInfo("taskmgr.exe")
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Log.Write("taskmgr: " + ex.Message);
		}
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		Native.KillTimer(base.Handle, 1);
		Native.KillTimer(base.Handle, 2);
		if (_hook != IntPtr.Zero)
		{
			Native.UnhookWinEvent(_hook);
		}
		_menu?.Dispose();
		_logi.Changed -= OnLogiChanged;
		_logiForm?.Close();
		if (_trayIcon != null)
		{
			_trayIcon.Visible = false;
			_trayIcon.Dispose();
		}
		_trayIco?.Dispose();
		_logi.Dispose();
		_bar.Destroy();
		_sensors.Dispose();
		_renderer.Dispose();
		FreeBuffer();
		base.OnFormClosed(e);
	}

	private void FillBattery()
	{
		_r.KbBattery = _logi.KeyboardBattery;
		_r.KbCharging = _logi.KeyboardCharging;
		(string, float, bool, bool) currentMouse = _logi.CurrentMouse;
		_r.MouseBattery = currentMouse.Item2;
		_r.MouseCharging = currentMouse.Item3;
	}

	private void OnLogiChanged()
	{
		if (base.IsHandleCreated && !base.IsDisposed)
		{
			BeginInvoke(UpdateTrayIcon);
		}
	}

	private void UpdateTrayIcon()
	{
		if (!_s.TrayIcon)
		{
			if (_trayIcon != null)
			{
				_trayIcon.Visible = false;
				_trayIcon.Dispose();
				_trayIcon = null;
			}
			return;
		}
		if (_trayIcon == null)
		{
			if (_trayIco == null)
			{
				_trayIco = MakeTrayIcon();
			}
			_trayIcon = new NotifyIcon
			{
				Icon = _trayIco,
				Text = "PulseBar",
				Visible = true
			};
			_trayIcon.MouseClick += delegate(object? _, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Left)
				{
					OpenLogi();
				}
				else if (e.Button == MouseButtons.Right)
				{
					ShowMenu();
				}
			};
		}
		string text = "PulseBar";
		if (_logi.Config.Enabled)
		{
			text = text + "\n" + L.T("Profile") + ": " + _logi.Config.Profiles[_logi.Current].Name + (_logi.AutoSwitched ? (" " + L.T("(auto)")) : "");
			string text2 = _logi.ShortStatus();
			if (text2.Length > 0)
			{
				text = text + "\n" + text2;
			}
		}
		_trayIcon.Text = ((text.Length > 127) ? text.Substring(0, 127) : text);
	}

	private void OpenLogi()
	{
		if (_logiForm != null && !_logiForm.IsDisposed)
		{
			if (_logiForm.WindowState == FormWindowState.Minimized)
			{
				_logiForm.WindowState = FormWindowState.Normal;
			}
			_logiForm.Activate();
			return;
		}
		_logiForm = new LogiForm(_logi, !_lightTaskbar);
		_logiForm.FormClosed += delegate
		{
			_logiForm = null;
			_trimAt = Environment.TickCount64 + 3000;
		};
		_logiForm.Show();
		_logiForm.Activate();
	}

	private static Icon MakeTrayIcon()
	{
		int num = Math.Max(16, SystemInformation.SmallIconSize.Width);
		float num2 = (float)num / 16f;
		using Bitmap bitmap = new Bitmap(num, num, PixelFormat.Format32bppArgb);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.Clear(Color.Transparent);
			using GraphicsPath graphicsPath = new GraphicsPath();
			float num3 = 3.5f * num2;
			float num4 = num - 1;
			graphicsPath.AddArc(0f, 0f, num3 * 2f, num3 * 2f, 180f, 90f);
			graphicsPath.AddArc(num4 - num3 * 2f, 0f, num3 * 2f, num3 * 2f, 270f, 90f);
			graphicsPath.AddArc(num4 - num3 * 2f, num4 - num3 * 2f, num3 * 2f, num3 * 2f, 0f, 90f);
			graphicsPath.AddArc(0f, num4 - num3 * 2f, num3 * 2f, num3 * 2f, 90f, 90f);
			graphicsPath.CloseFigure();
			using SolidBrush brush = new SolidBrush(Color.FromArgb(28, 32, 38));
			graphics.FillPath(brush, graphicsPath);
			PointF[] points = new PointF[7]
			{
				new PointF(1.8f * num2, 8.5f * num2),
				new PointF(5f * num2, 8.5f * num2),
				new PointF(6.6f * num2, 3.6f * num2),
				new PointF(9.2f * num2, 12.6f * num2),
				new PointF(10.8f * num2, 6.6f * num2),
				new PointF(11.8f * num2, 8.5f * num2),
				new PointF(14.2f * num2, 8.5f * num2)
			};
			using Pen pen = new Pen(Color.FromArgb(61, 220, 132), 1.7f * num2)
			{
				LineJoin = LineJoin.Round,
				StartCap = LineCap.Round,
				EndCap = LineCap.Round
			};
			graphics.DrawLines(pen, points);
		}
		nint hicon = bitmap.GetHicon();
		Icon result = (Icon)System.Drawing.Icon.FromHandle(hicon).Clone();
		DestroyIcon(hicon);
		return result;
	}

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint h);
}
