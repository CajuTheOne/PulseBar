using System;
using System.Collections.Generic;
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

	private Settings _s;

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

	private readonly ClaudeUsage _claude;

	private const int TipTimerId = 3;

	private readonly History _hCpuLoad = new History();

	private readonly History _hCpuTemp = new History();

	private readonly History _hGpuLoad = new History();

	private readonly History _hGpuTemp = new History();

	private readonly History _hVram = new History();

	private readonly History _hRam = new History();

	private TooltipForm? _tip;

	private Process? _settingsProcess;

	private int _hoverX = -1;

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
		_claude = new ClaudeUsage(s.ClaudeIntervalMin);
		_claude.Changed += OnClaudeChanged;
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
		_bar.Hover += OnBarHover;
		_bar.Leave += HideTip;
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
			if (m.WParam == TipTimerId)
			{
				Native.KillTimer(base.Handle, TipTimerId);
				ShowTip();
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
		if (m.Msg == SettingsProcess.MsgSettingsChanged)
		{
			ReloadSettings();
			return;
		}
		if (m.Msg == SettingsProcess.MsgOpenLogitech)
		{
			OpenLogi();
			return;
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
		FillClaude();
		RecordHistory();
		if (SettingsOpen)
		{
			SettingsProcess.WriteStatus(in _r, _claude, _lightTaskbar, _sensors.Gpu.Adapters);
		}
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
		if (_tip != null && _tip.Visible)
		{
			ShowTip();
		}
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
		if (_renderer.Configure(num2, r.Height, _s, ClaudeVisible))
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
			// Поднимать строку, только если над ней оказалось окно самого Проводника (содержимое панели).
			// Соседей из других программ (виджет Claude Code Usage Monitor) не перекрывать: они тоже держатся наверху,
			// и поочерёдный подъём заставлял оба окна перерисовываться — моргание при Win и переключении окон.
			Native.GetWindowThreadProcessId(_tray, out uint explorer);
			nint above = Native.GetWindow(_tray, 5u);
			for (int i = 0; above != IntPtr.Zero && above != _bar.Handle && i < 64; i++)
			{
				Native.GetWindowThreadProcessId(above, out uint pid);
				if (pid == explorer && Native.IsWindowVisible(above) != 0)
				{
					Native.SetWindowPos(_bar.Handle, IntPtr.Zero, 0, 0, 0, 0, 19u);
					break;
				}
				above = Native.GetWindow(above, 2u);
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
		HideTip();
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
		Item(items, L.T("Settings…"), delegate
		{
			OpenSettings();
		});
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
		Item(items, L.T("Task Manager"), OpenTaskManager);
		items.Add(new ToolStripSeparator());
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
		_tip?.Dispose();
		_claude.Changed -= OnClaudeChanged;
		_claude.Dispose();
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

	// Окно настроек — отдельный процесс (см. SettingsProcess): строка не загружает WPF.
	private void OpenSettings(int page = 0)
	{
		if (_settingsProcess != null && !_settingsProcess.HasExited)
		{
			_settingsProcess.Refresh();
			nint window = _settingsProcess.MainWindowHandle;
			if (window != IntPtr.Zero)
			{
				Native.ShowWindow(window, 9);
				Native.SetForegroundWindow(window);
			}
			return;
		}
		SettingsProcess.WriteStatus(in _r, _claude, _lightTaskbar, _sensors.Gpu.Adapters);
		try
		{
			_settingsProcess = Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--settings {page} {base.Handle.ToInt64()}")
			{
				UseShellExecute = false
			});
		}
		catch (Exception ex)
		{
			Log.Write("settings: " + ex.Message);
		}
	}

	private bool SettingsOpen
	{
		get
		{
			Process settingsProcess = _settingsProcess;
			if (settingsProcess != null)
			{
				return !settingsProcess.HasExited;
			}
			return false;
		}
	}

	// Окно настроек сохранило settings.json: перечитать и применить.
	private void ReloadSettings()
	{
		Settings settings = Settings.Load();
		Log.Write("settings: reloaded after the settings window");
		string gpu = _s.Gpu;
		_s = settings;
		if (gpu != _s.Gpu)
		{
			SelectGpu(_s.Gpu);
		}
		L.Set(_s.Language);
		StartTimer();
		_claude.IntervalMin = _s.ClaudeIntervalMin;
		UpdateTrayIcon();
		if (_bar.Exists && _bar.Embedded != _s.Embed)
		{
			_bar.Destroy();
			_visible = false;
		}
		FillClaude();
		_palette = Palette.Create(_lightTaskbar, _s.BlueNormal);
		_dirty = true;
		UpdateOverlay();
	}

	private void RecordHistory()
	{
		long t = Environment.TickCount64;
		_hCpuLoad.Add(t, _r.CpuLoad);
		_hCpuTemp.Add(t, _r.CpuTemp);
		_hGpuLoad.Add(t, _r.GpuLoad);
		_hGpuTemp.Add(t, _r.GpuAsleep ? float.NaN : _r.GpuTemp);
		_hVram.Add(t, _r.VramLoad);
		_hRam.Add(t, _r.RamLoad);
	}

	// Подсказка: через 0,45 с после наведения; дальше обновляется вместе со строкой и следует за мышью по блокам.
	private void OnBarHover(int x)
	{
		_hoverX = x;
		if (_tip != null && _tip.Visible)
		{
			ShowTip();
		}
		else
		{
			Native.SetCoalescableTimer(base.Handle, TipTimerId, 450u, IntPtr.Zero, 0u);
		}
	}

	private void HideTip()
	{
		Native.KillTimer(base.Handle, TipTimerId);
		_hoverX = -1;
		if (_tip != null && _tip.Visible)
		{
			_tip.Hide();
		}
	}

	private void ShowTip()
	{
		ContextMenuStrip menu = _menu;
		if (_hoverX < 0 || !_visible || (menu != null && menu.Visible))
		{
			return;
		}
		string key = null;
		float left = 0f;
		float right = 0f;
		foreach (var (k, l, r) in _renderer.Blocks)
		{
			if (_hoverX >= l && _hoverX < r)
			{
				key = k;
				left = l;
				right = r;
				break;
			}
		}
		if (key == null)
		{
			if (_tip != null && _tip.Visible)
			{
				_tip.Hide();
			}
			return;
		}
		if (_tip == null)
		{
			_tip = new TooltipForm();
		}
		List<(string, string, Color?)> rows = new List<(string, string, Color?)>();
		List<TooltipForm.Series> series = new List<TooltipForm.Series>();
		string title = TipContent(key, rows, series);
		_tip.SetContent(title, rows, series, !_lightTaskbar);
		Native.GetWindowRect(_bar.Handle, out var bar);
		Screen screen = Screen.FromHandle(_bar.Handle);
		int x = Math.Clamp(bar.Left + (int)((left + right) / 2f) - _tip.Width / 2, screen.Bounds.Left + 4, screen.Bounds.Right - _tip.Width - 4);
		int y = bar.Top - _tip.Height - 8;
		_tip.Location = new Point(x, y);
		if (!_tip.Visible)
		{
			_tip.Show();
		}
		Native.SetWindowPos(_tip.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, 19u);
	}

	private string TipContent(string key, List<(string, string, Color?)> rows, List<TooltipForm.Series> series)
	{
		Color loadColor = Color.FromArgb(76, 194, 255);
		Color tempColor = Color.FromArgb(255, 160, 64);
		switch (key)
		{
		case "cpu":
			rows.Add((L.T("Load"), Pct(_r.CpuLoad), _palette.Level(_r.CpuLoad, _s.CpuLoad)));
			rows.Add((L.T("Temperature"), float.IsNaN(_r.CpuTemp) ? _sensors.CpuTemp.Status : $"{_r.CpuTemp:0}°C", _palette.Level(_r.CpuTemp, _s.CpuTemp)));
			series.Add(new TooltipForm.Series(_hCpuLoad, loadColor, L.T("Load"), 100f));
			series.Add(new TooltipForm.Series(_hCpuTemp, tempColor, "°C", 100f));
			return "CPU";
		case "gpu":
			rows.Add((L.T("Load"), Pct(_r.GpuLoad), _palette.Level(_r.GpuLoad, _s.GpuLoad)));
			rows.Add((L.T("Temperature"), _r.GpuAsleep ? L.T("sleep") : (float.IsNaN(_r.GpuTemp) ? "—" : $"{_r.GpuTemp:0}°C"), _r.GpuAsleep ? null : _palette.Level(_r.GpuTemp, _s.GpuTemp)));
			series.Add(new TooltipForm.Series(_hGpuLoad, loadColor, L.T("Load"), 100f));
			series.Add(new TooltipForm.Series(_hGpuTemp, tempColor, "°C", 100f));
			return _sensors.Gpu.Current?.Name ?? "GPU";
		case "vram":
		{
			float total = (float)(_sensors.Gpu.Current?.DedicatedMemory ?? 0) / 1073741824f;
			rows.Add((L.T("Used"), Gb(_r.VramUsedGb) + ((total > 0f) ? (" / " + Gb(total)) : ""), null));
			rows.Add((L.T("Load"), Pct(_r.VramLoad), _palette.Level(_r.VramLoad, _s.Vram)));
			series.Add(new TooltipForm.Series(_hVram, loadColor, L.T("Load"), 100f));
			return "VRAM";
		}
		case "ram":
			rows.Add((L.T("Used"), Gb(_r.RamUsedGb) + ((_r.RamLoad > 0f) ? (" / " + Gb(_r.RamUsedGb * 100f / _r.RamLoad)) : ""), null));
			rows.Add((L.T("Load"), Pct(_r.RamLoad), _palette.Level(_r.RamLoad, _s.Ram)));
			series.Add(new TooltipForm.Series(_hRam, loadColor, L.T("Load"), 100f));
			return "RAM";
		case "claude":
		{
			ClaudeUsage.Snapshot c = _claude.Current;
			if (c != null)
			{
				rows.Add((L.T("5 hours"), Pct(c.FiveHour) + Resets(c.FiveHourReset), c.Stale ? _palette.NoData : _palette.Level(c.FiveHour, _s.Claude)));
				rows.Add((L.T("7 days"), Pct(c.SevenDay) + Resets(c.SevenDayReset), c.Stale ? _palette.NoData : _palette.Level(c.SevenDay, _s.Claude)));
				int ago = (int)Math.Max(0.0, (DateTimeOffset.Now - c.Updated).TotalMinutes);
				rows.Add((L.T("Updated"), L.F("{0} min ago", ago) + " (" + ((c.Source == "api") ? "claude.ai" : L.T("Claude app")) + ")", c.Stale ? _palette.Warn : null));
			}
			if (_claude.ApiStatus.Length > 0)
			{
				rows.Add(("API", _claude.ApiStatus, _palette.NoData));
			}
			return "Claude";
		}
		default:
			foreach (string part in _logi.ShortStatus().Split(", "))
			{
				int sp = part.LastIndexOf(' ');
				if (sp > 0)
				{
					rows.Add((part.Substring(0, sp), part.Substring(sp + 1), null));
				}
			}
			if (rows.Count == 0)
			{
				rows.Add((L.T("Keyboard and mouse…"), "—", null));
			}
			return L.T("Battery");
		}
	}

	private static string Pct(float v)
	{
		if (!float.IsNaN(v))
		{
			return $"{v:0}%";
		}
		return "—";
	}

	private static string Gb(float v)
	{
		if (!float.IsNaN(v))
		{
			return v.ToString("0.0 'GB'", System.Globalization.CultureInfo.InvariantCulture);
		}
		return "—";
	}

	private static string Resets(DateTimeOffset? at)
	{
		if (!at.HasValue)
		{
			return "";
		}
		TimeSpan d = at.Value - DateTimeOffset.Now;
		if (d <= TimeSpan.Zero)
		{
			return "";
		}
		string left = (d.TotalHours >= 24.0) ? (L.F("{0} d", (int)d.TotalDays) + " " + L.F("{0} h", d.Hours)) : ((d.TotalMinutes >= 60.0) ? (L.F("{0} h", (int)d.TotalHours) + " " + L.F("{0} min", d.Minutes)) : L.F("{0} min", (int)Math.Ceiling(d.TotalMinutes)));
		return " · " + L.F("reset in {0}", left);
	}

	private bool ClaudeVisible => _s.ShowClaude && _claude.Available;

	private void FillClaude()
	{
		ClaudeUsage.Snapshot current = _claude.Current;
		_r.ClaudeShown = _s.ShowClaude && current != null;
		_r.ClaudeFiveHour = current?.FiveHour ?? float.NaN;
		_r.ClaudeSevenDay = current?.SevenDay ?? float.NaN;
		_r.ClaudeStale = current?.Stale ?? false;
	}

	private void OnClaudeChanged()
	{
		if (base.IsHandleCreated && !base.IsDisposed)
		{
			BeginInvoke(delegate
			{
				FillClaude();
				_dirty = true;
				UpdateOverlay();
			});
		}
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
