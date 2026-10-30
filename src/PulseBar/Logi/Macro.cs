using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PulseBar.Logi;

internal static class Macro
{
	private struct KEYBDINPUT
	{
		public ushort wVk;

		public ushort wScan;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	private struct MOUSEINPUT
	{
		public int dx;

		public int dy;

		public uint mouseData;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)]
		public MOUSEINPUT mi;

		[FieldOffset(0)]
		public KEYBDINPUT ki;
	}

	private struct INPUT
	{
		public int type;

		public InputUnion u;
	}

	private static readonly Dictionary<string, Keys> Aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
	{
		["ctrl"] = Keys.ControlKey,
		["control"] = Keys.ControlKey,
		["shift"] = Keys.ShiftKey,
		["alt"] = Keys.Menu,
		["win"] = Keys.LWin,
		["esc"] = Keys.Escape,
		["enter"] = Keys.Return,
		["del"] = Keys.Delete,
		["ins"] = Keys.Insert,
		["pgup"] = Keys.Prior,
		["pgdn"] = Keys.Next,
		["backspace"] = Keys.Back,
		["bksp"] = Keys.Back,
		["prtsc"] = Keys.Snapshot,
		["caps"] = Keys.Capital,
		["menu"] = Keys.Apps,
		["break"] = Keys.Pause,
		["playpause"] = Keys.MediaPlayPause,
		["next"] = Keys.MediaNextTrack,
		["prev"] = Keys.MediaPreviousTrack,
		["stop"] = Keys.MediaStop,
		["mute"] = Keys.VolumeMute,
		["volup"] = Keys.VolumeUp,
		["voldown"] = Keys.VolumeDown,
		["+"] = Keys.Oemplus,
		["-"] = Keys.OemMinus,
		[","] = Keys.Oemcomma,
		["."] = Keys.OemPeriod
	};

	private static readonly HashSet<ushort> Extended = new HashSet<ushort>
	{
		33, 34, 35, 36, 37, 38, 39, 40, 44, 45,
		46, 91, 92, 93, 111, 144, 163, 165, 166, 167,
		168, 169, 170, 171, 172, 173, 174, 175, 176, 177,
		178, 179
	};

	private const uint KEYEVENTF_EXTENDEDKEY = 1u;

	private const uint KEYEVENTF_KEYUP = 2u;

	private const uint KEYEVENTF_UNICODE = 4u;

	public static void Run(KeyAction a)
	{
		if (a.Type == "none" || string.IsNullOrWhiteSpace(a.Value))
		{
			return;
		}
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				Exec(a.Type, a.Value);
			}
			catch (Exception ex)
			{
				Log.Write("logi: macro " + a.Type + ": " + ex.Message);
			}
		});
	}

	private static void Exec(string type, string value)
	{
		switch (type)
		{
		case "keys":
		{
			string[] array = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < array.Length; i++)
			{
				if (TryParseChord(array[i], out List<ushort> vks))
				{
					PressChord(vks);
				}
			}
			break;
		}
		case "text":
			TypeText(value);
			break;
		case "run":
			Launch(value);
			break;
		case "macro":
		{
			string[] array = value.Split(new char[2] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			for (int i = 0; i < array.Length; i++)
			{
				Step(array[i]);
			}
			break;
		}
		}
	}

	private static void Step(string s)
	{
		int num = s.IndexOf(' ');
		string text = ((num > 0) ? s.Substring(0, num) : s).ToLowerInvariant();
		object obj;
		if (num <= 0)
		{
			obj = "";
		}
		else
		{
			int num2 = num + 1;
			obj = s.Substring(num2, s.Length - num2).Trim();
		}
		string text2 = (string)obj;
		switch (text)
		{
		case "wait":
		{
			if (int.TryParse(text2, out var result))
			{
				Thread.Sleep(Math.Clamp(result, 0, 60000));
			}
			break;
		}
		case "text":
			TypeText(text2);
			break;
		case "run":
			Launch(text2);
			break;
		case "down":
		{
			if (!TryParseChord(text2, out List<ushort> vks2))
			{
				break;
			}
			{
				foreach (ushort item in vks2)
				{
					Key(item, down: true);
				}
				break;
			}
		}
		case "up":
		{
			if (TryParseChord(text2, out List<ushort> vks))
			{
				for (int num3 = vks.Count - 1; num3 >= 0; num3--)
				{
					Key(vks[num3], down: false);
				}
			}
			break;
		}
		case "keys":
			Exec("keys", text2);
			break;
		default:
			Exec("keys", s);
			break;
		}
	}

	public static bool TryParseChord(string chord, out List<ushort> vks)
	{
		vks = new List<ushort>();
		string[] array;
		string[] array3;
		if (!chord.EndsWith("++"))
		{
			array = chord.Split('+');
		}
		else
		{
			string[] array2 = chord.Substring(0, chord.Length - 2).Split('+');
			int num = 0;
			array3 = new string[1 + array2.Length];
			ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array2);
			readOnlySpan.CopyTo(new Span<string>(array3).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			array3[num] = "+";
			array = array3;
		}
		array3 = array;
		for (int num = 0; num < array3.Length; num++)
		{
			string text = array3[num].Trim();
			if (text.Length != 0)
			{
				Keys result;
				if (Aliases.TryGetValue(text, out var value))
				{
					result = value;
				}
				else if (text.Length == 1 && char.IsDigit(text[0]))
				{
					result = (Keys)(48 + (text[0] - 48));
				}
				else if (!Enum.TryParse<Keys>(text, ignoreCase: true, out result) || result <= Keys.None || result > Keys.OemClear)
				{
					return false;
				}
				vks.Add((ushort)result);
			}
		}
		return vks.Count > 0;
	}

	public static string Describe(Keys keyData)
	{
		StringBuilder stringBuilder = new StringBuilder();
		if ((keyData & Keys.Control) != Keys.None)
		{
			stringBuilder.Append("Ctrl+");
		}
		if ((keyData & Keys.Shift) != Keys.None)
		{
			stringBuilder.Append("Shift+");
		}
		if ((keyData & Keys.Alt) != Keys.None)
		{
			stringBuilder.Append("Alt+");
		}
		Keys keys = keyData & Keys.KeyCode;
		if (((uint)(keys - 16) <= 2u || (uint)(keys - 160) <= 5u) ? true : false)
		{
			return stringBuilder.ToString().TrimEnd('+');
		}
		string text;
		switch (keys)
		{
		case Keys.D0:
		case Keys.D1:
		case Keys.D2:
		case Keys.D3:
		case Keys.D4:
		case Keys.D5:
		case Keys.D6:
		case Keys.D7:
		case Keys.D8:
		case Keys.D9:
			text = ((char)(48 + (keys - 48))).ToString();
			break;
		case Keys.Return:
			text = "Enter";
			break;
		case Keys.Escape:
			text = "Esc";
			break;
		case Keys.Back:
			text = "Backspace";
			break;
		case Keys.Next:
			text = "PageDown";
			break;
		default:
			text = keys.ToString();
			break;
		}
		string value = text;
		return stringBuilder.Append(value).ToString();
	}

	private static void PressChord(List<ushort> vks)
	{
		foreach (ushort vk in vks)
		{
			Key(vk, down: true);
		}
		for (int num = vks.Count - 1; num >= 0; num--)
		{
			Key(vks[num], down: false);
		}
		Thread.Sleep(15);
	}

	private static void Key(ushort vk, bool down)
	{
		uint num = ((!down) ? 2u : 0u);
		if (Extended.Contains(vk))
		{
			num |= 1;
		}
		ushort wScan = (ushort)MapVirtualKeyW(vk, 0u);
		INPUT iNPUT = new INPUT
		{
			type = 1,
			u = new InputUnion
			{
				ki = new KEYBDINPUT
				{
					wVk = vk,
					wScan = wScan,
					dwFlags = num
				}
			}
		};
		SendInput(1u, new INPUT[1] { iNPUT }, Marshal.SizeOf<INPUT>());
	}

	private static void TypeText(string text)
	{
		List<INPUT> list = new List<INPUT>(text.Length * 2);
		string text2 = text.Replace("\r\n", "\n");
		foreach (char c in text2)
		{
			if (c == '\n')
			{
				SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
				list.Clear();
				Key(13, down: true);
				Key(13, down: false);
				continue;
			}
			list.Add(new INPUT
			{
				type = 1,
				u = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wScan = c,
						dwFlags = 4u
					}
				}
			});
			list.Add(new INPUT
			{
				type = 1,
				u = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wScan = c,
						dwFlags = 6u
					}
				}
			});
		}
		if (list.Count > 0)
		{
			SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
		}
	}

	private static void Launch(string cmd)
	{
		cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
		if (cmd.Length == 0)
		{
			return;
		}
		string fileName = cmd;
		string arguments = "";
		if (cmd[0] == '"')
		{
			int num = cmd.IndexOf('"', 1);
			if (num > 0)
			{
				fileName = cmd.Substring(1, num - 1);
				string text = cmd;
				int num2 = num + 1;
				arguments = text.Substring(num2, text.Length - num2).Trim();
			}
		}
		else if (!File.Exists(cmd) && !Directory.Exists(cmd))
		{
			int num3 = cmd.IndexOf(' ');
			if (num3 > 0)
			{
				fileName = cmd.Substring(0, num3);
				string text = cmd;
				int num2 = num3 + 1;
				arguments = text.Substring(num2, text.Length - num2);
			}
		}
		Process.Start(new ProcessStartInfo(fileName, arguments)
		{
			UseShellExecute = true
		})?.Dispose();
	}

	[DllImport("user32.dll")]
	private static extern uint SendInput(uint n, INPUT[] inputs, int size);

	[DllImport("user32.dll")]
	private static extern uint MapVirtualKeyW(uint code, uint type);
}
