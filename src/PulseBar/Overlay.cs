using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

	// Уведомления о включении и выключении экрана (для подсветки клавиатуры).
	private nint _powerNotify;

	private readonly BarWindow _bar = new BarWindow();

	private readonly Updater _updater = new Updater();

	private readonly ClaudeUsage _claude;

	private readonly Ai.AiUsage _ai;

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

	// Раскладка: элементы в координатах экрана, левый край окна строки, край трея, левый край панели, масштаб.
	private readonly List<(string Id, int L, int R)> _elements = new List<(string, int, int)>();

	private readonly List<(int G, int I, string Id, int L, int R)> _arranged = new List<(int, int, string, int, int)>();

	private int _winLeft;

	private int _trayEdge;

	private int _taskbarLeft;

	private float _scale = 1f;

	private int _layoutSig;

	// Режим правки: элементы перетаскиваются мышью (Shift — все сразу); Esc или пункт меню — выход с сохранением.
	private bool _edit;

	private string? _dragId;

	private bool _dragAll;

	private List<Settings.Group>? _dragBase;

	private int _dragL0;

	private int _dragR0;

	private nint _keyHook;

	private KeyboardProc? _keyProc;

	private string? _menuHit;

	// Первая проверка обновлений — через 5 с после запуска, дальше раз в сутки (и кнопкой в «О программе»).
	private long _updateCheckAt = Environment.TickCount64 + 5000;

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
		_claude.FollowApp = s.ClaudeFollowApp;
		_claude.Changed += OnClaudeChanged;
		// Строка ИИ: опрашиваются только включённые сервисы; ключ DeepSeek хранится зашифрованным.
		_ai = new Ai.AiUsage((string id) => _s.IsShown(id), () => Ai.Secret.Unprotect(_s.DeepSeekKey));
		_ai.Changed += OnAiChanged;
		_lightTaskbar = ReadLightTaskbar();
		_palette = Palette.Create(_lightTaskbar, s);
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
		_bar.RightClick += ShowMenuAt;
		_bar.DoubleClick += OpenTaskManager;
		_bar.Hover += OnBarHover;
		_bar.Leave += HideTip;
		_bar.DragStart += OnDragStart;
		_bar.Drag += OnDrag;
		_bar.DragEnd += OnDragEnd;
		StartTimer();
		BeginInvoke(Tick);
		UpdateTrayIcon();
		_logi.Start();
		Guid display = DisplayStateGuid;
		_powerNotify = RegisterPowerSettingNotification(base.Handle, ref display, 0);
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
		case 536:
			// WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE: экран 0 — выключен, 1 — включён, 2 — притушен.
			if (m.WParam == 0x8013 && m.LParam != IntPtr.Zero)
			{
				_logi.DisplayChanged(Marshal.ReadInt32(m.LParam, 20) != 0);
			}
			break;
		case 537:
			if (m.WParam == 32768 || m.WParam == 32772 || m.WParam == 7)
			{
				_logi.DevicesChanged();
			}
			break;
		case 22:
			// WM_ENDSESSION: выключение или выход из Windows — клавиатуру вернуть её памяти, пока процесс жив.
			if (m.WParam != IntPtr.Zero)
			{
				ReleaseDevices();
			}
			break;
		}
		if (m.Msg == SettingsProcess.MsgSettingsChanged)
		{
			ReloadSettings();
			return;
		}
		if (m.Msg == SettingsProcess.MsgEditMode)
		{
			SetEditMode(m.WParam != IntPtr.Zero);
			return;
		}
		if (m.Msg == SettingsProcess.MsgResetLayout)
		{
			ResetLayout();
			return;
		}
		if (m.Msg == SettingsProcess.MsgAddSeparator)
		{
			AddSeparator(null);
			return;
		}
		if (m.Msg == SettingsProcess.MsgOpenSettings)
		{
			OpenSettings();
			return;
		}
		if (m.Msg == SettingsProcess.MsgLogiReload)
		{
			_logi.ReloadConfig();
			return;
		}
		if (m.Msg == SettingsProcess.MsgLogiRestore)
		{
			_logi.RestoreFactory();
			return;
		}
		if (m.Msg == SettingsProcess.MsgLogiBackup)
		{
			_logi.BackupNow();
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
		FillAi();
		RecordHistory();
		if (SettingsOpen)
		{
			SettingsProcess.WriteStatus(in _r, _claude, _lightTaskbar, _sensors.Gpu.Adapters, _edit, _logi, _ai);
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
				_palette = Palette.Create(flag, _s);
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
		int num = BarRenderer.Signature(in _r, _s, _lightTaskbar) ^ ((_updater.Available != null) ? 1540483477 : 0) ^ _layoutSig ^ (_edit ? 1013904242 : 0);
		bool flag = target.Left != _placed.Left || target.Top != _placed.Top || target.Width != _placed.Width || target.Height != _placed.Height;
		if (_dirty || num != _signature)
		{
			_g.Clear(Color.Transparent);
			DrawElements(target.Left);
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
		_scale = num2;
		_trayEdge = num3;
		_taskbarLeft = r.Left;
		Arrange(_s.LayoutGroups, _arranged);
		_elements.Clear();
		int num4 = 17;
		foreach (var (_, _, id, l, rr) in _arranged)
		{
			_elements.Add((id, l, rr));
			num4 = HashCode.Combine(num4, id, l, rr);
		}
		_layoutSig = num4;
		if (_elements.Count == 0)
		{
			return false;
		}
		int num5 = (int)MathF.Ceiling(_renderer.Pad) + (int)(6f * num2);
		target = new Native.RECT
		{
			Left = Math.Max(r.Left, _elements.Min(((string Id, int L, int R) e) => e.L) - num5),
			Top = r.Top,
			Right = Math.Min(r.Right, _elements.Max(((string Id, int L, int R) e) => e.R) + num5),
			Bottom = r.Bottom
		};
		_winLeft = target.Left;
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

	private void ShowMenuAt(int clientX)
	{
		_menuHit = ((clientX >= 0) ? ElementAt(_winLeft + clientX)?.Id : null);
		ShowMenu();
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
		Check(items, L.T("Edit mode"), _edit, SetEditMode, save: false);
		if (_edit)
		{
			string hit = _menuHit;
			Item(items, L.T("Add separator"), delegate
			{
				AddSeparator(hit);
			});
			if (hit != null && BarRenderer.IsSeparator(hit))
			{
				Item(items, L.T("Remove separator"), delegate
				{
					RemoveSeparator(hit);
				});
			}
			Item(items, L.T("Reset layout"), ResetLayout);
		}
		_menuHit = null;
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
		_palette = Palette.Create(_lightTaskbar, _s);
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

	// Клавиатуру Logitech — её памяти (повторный вызов ничего не делает). Нужен и вне OnFormClosed: WM_CLOSE от taskkill
	// или установщика закрывает скрытое окно строки без событий формы (проверено 31.10.2026).
	internal void ReleaseDevices()
	{
		_logi.Dispose();
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		Log.Write($"exit ({e.CloseReason})");
		Native.KillTimer(base.Handle, 1);
		Native.KillTimer(base.Handle, 2);
		if (_hook != IntPtr.Zero)
		{
			Native.UnhookWinEvent(_hook);
		}
		_menu?.Dispose();
		if (_powerNotify != IntPtr.Zero)
		{
			UnregisterPowerSettingNotification(_powerNotify);
		}
		_logi.Changed -= OnLogiChanged;
		if (_trayIcon != null)
		{
			_trayIcon.Visible = false;
			_trayIcon.Dispose();
		}
		_trayIco?.Dispose();
		_logi.Dispose();
		_tip?.Dispose();
		if (_keyHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_keyHook);
		}
		_claude.Changed -= OnClaudeChanged;
		_claude.Dispose();
		_ai.Changed -= OnAiChanged;
		_ai.Dispose();
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
		SettingsProcess.WriteStatus(in _r, _claude, _lightTaskbar, _sensors.Gpu.Adapters, _edit, _logi, _ai);
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
		_claude.FollowApp = _s.ClaudeFollowApp;
		UpdateTrayIcon();
		if (_bar.Exists && _bar.Embedded != _s.Embed)
		{
			_bar.Destroy();
			_visible = false;
		}
		FillClaude();
		FillAi();
		_ai.PollNow();
		_palette = Palette.Create(_lightTaskbar, _s);
		_dirty = true;
		UpdateOverlay();
	}

	// ---------- Раскладка элементов по панели ----------

	private bool IsVisibleElement(string id)
	{
		if (BarRenderer.IsSeparator(id))
		{
			return true;
		}
		if (id == "claude")
		{
			if (_s.ClaudeStyle != 0)
			{
				return _claude.Available;
			}
			return false;
		}
		return _s.IsShown(id);
	}

	// Группа прижатых элементов кладётся справа налево от своего правого края (X от трея); группа не уходит
	// за левый край панели. Результат — координаты экрана.
	private void Arrange(List<Settings.Group> groups, List<(int G, int I, string Id, int L, int R)> list)
	{
		list.Clear();
		for (int i = 0; i < groups.Count; i++)
		{
			Settings.Group group = groups[i];
			int count = list.Count;
			int num = _trayEdge - (int)MathF.Round((float)group.X * _scale);
			string right = null;
			for (int num2 = group.Items.Count - 1; num2 >= 0; num2--)
			{
				string text = group.Items[num2];
				if (IsVisibleElement(text))
				{
					if (right != null)
					{
						num -= (int)MathF.Round(_renderer.Gap(text, right));
					}
					int num3 = Math.Max(1, (int)MathF.Ceiling(_renderer.ElementWidth(text)));
					list.Add((i, num2, text, num - num3, num));
					num -= num3;
					right = text;
				}
			}
			if (list.Count > count)
			{
				int num4 = _taskbarLeft + 2 - list[list.Count - 1].L;
				if (num4 > 0)
				{
					for (int j = count; j < list.Count; j++)
					{
						var (g, item, id, l, r) = list[j];
						list[j] = (g, item, id, l + num4, r + num4);
					}
				}
			}
		}
	}

	private (string Id, int L, int R)? ElementAt(int screenX)
	{
		foreach (var element in _elements)
		{
			int num = (BarRenderer.IsSeparator(element.Id) ? ((int)(6f * _scale)) : 0);
			if (screenX >= element.L - num && screenX < element.R + num)
			{
				return element;
			}
		}
		return null;
	}

	// Промежутки между группами прозрачны (alpha 0) — мышь проходит к панели задач; под элементами — почти прозрачная
	// подложка (alpha 1), чтобы ловить наведение и щелчки. В режиме правки — пунктирные рамки.
	private void DrawElements(int winLeft)
	{
		int num = (int)MathF.Ceiling(_renderer.Pad);
		int num2 = (int)(6f * _scale);
		using (SolidBrush brush = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
		{
			foreach (var (id, l, r) in _elements)
			{
				int num3 = (BarRenderer.IsSeparator(id) ? num2 : num);
				_g.FillRectangle(brush, l - winLeft - num3, 0, r - l + 2 * num3, _dibH);
				_renderer.DrawElement(_g, id, l - winLeft, in _r, _s, _palette);
			}
		}
		if (!_edit)
		{
			return;
		}
		using Pen pen = new Pen(Color.FromArgb(76, 194, 255), Math.Max(1f, _scale))
		{
			DashStyle = DashStyle.Dash
		};
		foreach (var (id2, l2, r2) in _elements)
		{
			int num4 = (BarRenderer.IsSeparator(id2) ? num2 : 3);
			_g.DrawRectangle(pen, l2 - winLeft - num4, 3, r2 - l2 + 2 * num4 - 1, _dibH - 7);
		}
	}

	// ---------- Режим правки ----------

	private void SetEditMode(bool on)
	{
		if (on == _edit)
		{
			return;
		}
		_edit = on;
		_bar.EditMode = on;
		HideTip();
		if (on)
		{
			_keyProc ??= OnKey;
			_keyHook = SetWindowsHookExW(13, _keyProc, GetModuleHandleW(null), 0u);
		}
		else
		{
			if (_keyHook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_keyHook);
			}
			_keyHook = IntPtr.Zero;
			_s.Save();
		}
		Log.Write(on ? ("layout: edit mode on; " + string.Join(" ", _elements.Select(((string Id, int L, int R) e) => $"{e.Id}:{e.L}-{e.R}"))) : "layout: edit mode off, layout saved");
		_dirty = true;
		UpdateOverlay();
	}

	// Esc завершает режим правки; клавиша не перехватывается, только замечается.
	private nint OnKey(int code, nint wParam, nint lParam)
	{
		if (code >= 0 && wParam == 256 && Marshal.ReadInt32(lParam) == 27)
		{
			BeginInvoke(delegate
			{
				SetEditMode(on: false);
			});
		}
		return CallNextHookEx(_keyHook, code, wParam, lParam);
	}

	private void OnDragStart(int clientX, bool shift)
	{
		HideTip();
		_dragId = null;
		_dragAll = false;
		_dragBase = null;
		if (!_edit)
		{
			return;
		}
		_dragBase = _s.LayoutGroups.Select((Settings.Group g) => g.Clone()).ToList();
		if (shift)
		{
			_dragAll = true;
			return;
		}
		(string Id, int L, int R)? tuple = ElementAt(_winLeft + clientX);
		if (tuple.HasValue)
		{
			(_dragId, _dragL0, _dragR0) = tuple.Value;
		}
	}

	// Перетаскиваемый элемент вынимается из своей группы; рядом с краем соседа (ближе 24 px) он к нему прилипает,
	// иначе становится отдельной группой там, где его отпустили. С Shift двигаются все группы.
	private void OnDrag(int dx)
	{
		if (_dragBase == null || (!_dragAll && _dragId == null))
		{
			return;
		}
		List<Settings.Group> list = _dragBase.Select((Settings.Group g) => g.Clone()).ToList();
		if (_dragAll)
		{
			int num = (int)MathF.Round((float)dx / _scale);
			foreach (Settings.Group item in list)
			{
				item.X = Math.Max(0, item.X - num);
			}
		}
		else
		{
			RemoveElement(list, _dragId);
			int num2 = _dragL0 + dx;
			int num3 = _dragR0 + dx;
			if (!_s.Snap || !TrySnap(list, _dragId, num2, num3))
			{
				Settings.Group group = new Settings.Group
				{
					X = Math.Max(0, (int)MathF.Round((float)(_trayEdge - num3) / _scale))
				};
				group.Items.Add(_dragId);
				list.Add(group);
			}
		}
		_s.Groups = list;
		_dirty = true;
		UpdateOverlay();
	}

	private void OnDragEnd()
	{
		if (_dragBase != null && (_dragAll || _dragId != null))
		{
			_s.Save();
			Log.Write("layout: moved " + (_dragAll ? "all" : _dragId));
		}
		_dragBase = null;
		_dragId = null;
		_dragAll = false;
	}

	// Убрать элемент из группы так, чтобы остальные элементы группы остались на месте.
	private void RemoveElement(List<Settings.Group> groups, string id)
	{
		for (int i = 0; i < groups.Count; i++)
		{
			Settings.Group group = groups[i];
			int num = group.Items.IndexOf(id);
			if (num < 0)
			{
				continue;
			}
			bool flag = true;
			for (int j = num + 1; j < group.Items.Count; j++)
			{
				if (IsVisibleElement(group.Items[j]))
				{
					flag = false;
					break;
				}
			}
			string left = null;
			for (int num2 = num - 1; num2 >= 0; num2--)
			{
				if (IsVisibleElement(group.Items[num2]))
				{
					left = group.Items[num2];
					break;
				}
			}
			if (flag && left != null && IsVisibleElement(id))
			{
				group.X += (int)MathF.Round((_renderer.ElementWidth(id) + _renderer.Gap(left, id)) / _scale);
			}
			group.Items.RemoveAt(num);
			if (group.Items.Count == 0)
			{
				groups.RemoveAt(i);
			}
			break;
		}
	}

	// Куда вставить перетаскиваемый элемент: если его центр над группой — в ближайший промежуток этой группы;
	// иначе — к краю соседней группы, если до края меньше 24 px. false — не прилип.
	private bool TrySnap(List<Settings.Group> groups, string id, int l, int r)
	{
		List<(int G, int I, string Id, int L, int R)> list = new List<(int, int, string, int, int)>();
		Arrange(groups, list);
		int width = r - l;
		int center = (l + r) / 2;
		int catchPx = (int)(24f * _scale);
		int bestScore = int.MaxValue;
		int bestGroup = -1;
		int bestIndex = -1;
		bool bestEnd = false;
		int bestRight = 0;
		for (int i = 0; i < groups.Count; i++)
		{
			List<(int G, int I, string Id, int L, int R)> items = (from e in list
				where e.G == i
				orderby e.L
				select e).ToList();
			if (items.Count == 0)
			{
				continue;
			}
			var first = items[0];
			var last = items[items.Count - 1];
			int gapL = (int)MathF.Round(_renderer.Gap(id, first.Id));
			int gapR = (int)MathF.Round(_renderer.Gap(last.Id, id));
			bool over = center >= first.L - catchPx && center <= last.R + catchPx;
			Consider(over ? Math.Abs(center - (first.L - gapL / 2)) : Math.Abs(r + gapL - first.L), i, first.I, atEnd: false, 0);
			Consider(over ? Math.Abs(center - (last.R + gapR / 2)) : Math.Abs(l - gapR - last.R), i, last.I + 1, atEnd: true, last.R + gapR + width);
			if (over)
			{
				for (int j = 0; j + 1 < items.Count; j++)
				{
					Consider(Math.Abs(center - (items[j].R + items[j + 1].L) / 2), i, items[j + 1].I, atEnd: false, 0);
				}
			}
			void Consider(int distance, int group, int index, bool atEnd, int newRight)
			{
				int score = (over ? distance : ((distance < catchPx) ? (100000 + distance) : int.MaxValue));
				if (score < bestScore)
				{
					bestScore = score;
					bestGroup = group;
					bestIndex = index;
					bestEnd = atEnd;
					bestRight = newRight;
				}
			}
		}
		if (bestGroup < 0)
		{
			return false;
		}
		groups[bestGroup].Items.Insert(bestIndex, id);
		if (bestEnd)
		{
			groups[bestGroup].X = Math.Max(0, (int)MathF.Round((float)(_trayEdge - bestRight) / _scale));
		}
		return true;
	}

	// Разделитель — справа от элемента, по которому щёлкнули; без него — в начало ближайшей к трею группы.
	private void AddSeparator(string? afterId)
	{
		List<Settings.Group> layoutGroups = _s.LayoutGroups;
		string item = _s.NewSeparatorId();
		Settings.Group group = ((afterId != null) ? layoutGroups.FirstOrDefault((Settings.Group g) => g.Items.Contains(afterId)) : null);
		if (group != null)
		{
			group.Items.Insert(group.Items.IndexOf(afterId) + 1, item);
		}
		else
		{
			layoutGroups.OrderBy((Settings.Group g) => g.X).First().Items.Insert(0, item);
		}
		_s.Save();
		_dirty = true;
		UpdateOverlay();
	}

	private void RemoveSeparator(string id)
	{
		List<Settings.Group> layoutGroups = _s.LayoutGroups;
		RemoveElement(layoutGroups, id);
		_s.Save();
		_dirty = true;
		UpdateOverlay();
	}

	private void ResetLayout()
	{
		_s.ResetLayout();
		_s.Save();
		Log.Write("layout: reset");
		_dirty = true;
		UpdateOverlay();
	}

	private delegate nint KeyboardProc(int code, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern nint SetWindowsHookExW(int id, KeyboardProc proc, nint module, uint thread);

	[DllImport("user32.dll")]
	private static extern bool UnhookWindowsHookEx(nint hook);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern nint GetModuleHandleW(string? name);

	private static readonly Guid DisplayStateGuid = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");

	[DllImport("user32.dll")]
	private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, int flags);

	[DllImport("user32.dll")]
	private static extern bool UnregisterPowerSettingNotification(nint handle);

	private void RecordHistory()
	{
		if (!_s.HoverChart)
		{
			return;
		}
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
		if (!_s.HoverTip || _edit)
		{
			return;
		}
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
		if (_hoverX < 0 || !_visible || _edit || (menu != null && menu.Visible))
		{
			return;
		}
		(string Id, int L, int R)? tuple = ElementAt(_winLeft + _hoverX);
		string key = ((tuple.HasValue && !BarRenderer.IsSeparator(tuple.Value.Id)) ? tuple.Value.Id : null);
		float left = (tuple.HasValue ? ((float)(tuple.Value.L - _winLeft)) : 0f);
		float right = (tuple.HasValue ? ((float)(tuple.Value.R - _winLeft)) : 0f);
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
		if (!_s.HoverChart)
		{
			series.Clear();
		}
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
			string account = _claude.AccountLabel;
			if (account.Length > 0)
			{
				rows.Add((L.T("Account"), account, null));
			}
			if (c != null)
			{
				rows.Add((L.T("5 hours"), Pct(c.FiveHour) + Resets(c.FiveHourReset, c.ResetsEstimated), c.Stale ? _palette.NoData : _palette.Level(c.FiveHour, _s.Claude)));
				rows.Add((L.T("7 days"), Pct(c.SevenDay) + Resets(c.SevenDayReset, c.ResetsEstimated), c.Stale ? _palette.NoData : _palette.Level(c.SevenDay, _s.Claude)));
				int ago = (int)Math.Max(0.0, (DateTimeOffset.Now - c.Updated).TotalMinutes);
				rows.Add((L.T("Updated"), L.F("{0} min ago", ago) + " (" + ((c.Source == "api") ? "claude.ai" : L.T("Claude app")) + ")", c.Stale ? _palette.Warn : null));
			}
			if (_claude.AppStatus.Length > 0)
			{
				rows.Add((L.T("Claude app"), _claude.AppStatus, _palette.Warn));
			}
			if (_claude.ApiStatus.Length > 0)
			{
				rows.Add(("API", _claude.ApiStatus, _palette.NoData));
			}
			return "Claude";
		}
		case "chatgpt":
		case "grok":
		case "deepseek":
		{
			Ai.AiSnapshot? a = _ai.Current.GetValueOrDefault(key);
			if (a == null)
			{
				rows.Add((L.T("Status"), L.T("waiting for data…"), _palette.NoData));
			}
			else
			{
				if (key == "deepseek")
				{
					rows.Add((L.T("Balance"), BarRenderer.Money(a.Balance is decimal b ? (float)b : float.NaN, a.Currency), a.Stale ? _palette.NoData : null));
				}
				else if (key == "grok")
				{
					rows.Add((L.T("Usage"), Pct(a.First) + Resets(a.FirstReset), a.Stale ? _palette.NoData : _palette.Level(a.First, _s.Grok)));
				}
				else
				{
					rows.Add((L.T("5 hours"), Pct(a.First) + Resets(a.FirstReset), a.Stale ? _palette.NoData : _palette.Level(a.First, _s.ChatGpt)));
					rows.Add((L.T("7 days"), Pct(a.Second) + Resets(a.SecondReset), a.Stale ? _palette.NoData : _palette.Level(a.Second, _s.ChatGpt)));
				}
				int ago = (int)Math.Max(0.0, (DateTimeOffset.Now - a.Updated).TotalMinutes);
				string source = a.Source switch
				{
					"codex logs" => L.T("Codex logs"),
					"api" => (key == "chatgpt") ? "chatgpt.com" : ((key == "deepseek") ? "api.deepseek.com" : "api"),
					_ => a.Source
				};
				rows.Add((L.T("Updated"), L.F("{0} min ago", ago) + ((source.Length > 0) ? (" (" + source + ")") : ""), a.Stale ? _palette.Warn : null));
				if (a.Status.Length > 0)
				{
					rows.Add((L.T("Status"), L.T(a.Status), _palette.Warn));
				}
			}
			return key switch
			{
				"chatgpt" => "ChatGPT (Codex)",
				"grok" => "Grok",
				_ => "DeepSeek"
			};
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

	private static string Resets(DateTimeOffset? at, bool estimated = false)
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
		return " · " + L.F("reset in {0}", (estimated ? "≈" : "") + left);
	}

	private static float MinutesLeft(DateTimeOffset? at)
	{
		if (!at.HasValue)
		{
			return float.NaN;
		}
		return (float)(at.Value - DateTimeOffset.Now).TotalMinutes;
	}

	private void FillClaude()
	{
		ClaudeUsage.Snapshot current = _claude.Current;
		_r.ClaudeShown = _s.ShowClaude && current != null;
		_r.ClaudeFiveHour = current?.FiveHour ?? float.NaN;
		_r.ClaudeSevenDay = current?.SevenDay ?? float.NaN;
		_r.ClaudeStale = current?.Stale ?? false;
		_r.ClaudeFiveHourLeft = MinutesLeft(current?.FiveHourReset);
		_r.ClaudeSevenDayLeft = MinutesLeft(current?.SevenDayReset);
	}

	// Строка ИИ → показания для отрисовки.
	private void FillAi()
	{
		IReadOnlyDictionary<string, Ai.AiSnapshot> cur = _ai.Current;
		Ai.AiSnapshot? gpt = cur.GetValueOrDefault("chatgpt");
		Ai.AiSnapshot? grok = cur.GetValueOrDefault("grok");
		Ai.AiSnapshot? ds = cur.GetValueOrDefault("deepseek");
		_r.GptFirst = gpt?.First ?? float.NaN;
		_r.GptSecond = gpt?.Second ?? float.NaN;
		_r.GptStale = gpt?.Stale ?? false;
		_r.GptFirstLeft = MinutesLeft(gpt?.FirstReset);
		_r.GptSecondLeft = MinutesLeft(gpt?.SecondReset);
		_r.GrokUsed = grok?.First ?? float.NaN;
		_r.GrokStale = grok?.Stale ?? false;
		_r.DsBalance = (ds?.Balance is decimal b) ? (float)b : float.NaN;
		_r.DsCurrency = ds?.Currency;
		_r.DsStale = ds?.Stale ?? false;
	}

	private void OnAiChanged()
	{
		if (base.IsHandleCreated && !base.IsDisposed)
		{
			BeginInvoke(delegate
			{
				FillAi();
				_dirty = true;
				UpdateOverlay();
			});
		}
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
					// Значок в трее (настройка «Значок в трее»): клик — вкладка «Клавиатура» окна настроек.
					OpenSettings(2);
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
