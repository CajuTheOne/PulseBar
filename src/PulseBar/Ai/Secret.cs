using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PulseBar.Ai;

// Ключ API пользователя в настройках — зашифрован DPAPI (CryptProtectData, область текущего пользователя Windows):
// расшифровать может только тот же пользователь на том же ПК. Без пакетов NuGet — прямой вызов crypt32.
internal static class Secret
{
	[StructLayout(LayoutKind.Sequential)]
	private struct DataBlob
	{
		public int Size;

		public IntPtr Data;
	}

	// Без окон с вопросами пользователю.
	private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

	// Дополнительная энтропия: чужая программа того же пользователя не расшифрует строку простым вызовом без неё.
	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PulseBar.Ai.Secret");

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

	[DllImport("crypt32.dll", SetLastError = true)]
	private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

	[DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
	private static extern bool CryptUnprotectDataNoEntropy(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr mem);

	// Данные другой программы под DPAPI текущего пользователя без энтропии (ключ шифрования приложения Claude, см.
	// ClaudeAppAuth); null — не расшифровалось. Вызывающий стирает результат после использования.
	public static byte[]? UnprotectUser(byte[] data)
	{
		GCHandle dataPin = GCHandle.Alloc(data, GCHandleType.Pinned);
		DataBlob output = default;
		try
		{
			DataBlob input = new DataBlob { Size = data.Length, Data = dataPin.AddrOfPinnedObject() };
			if (!CryptUnprotectDataNoEntropy(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output) || output.Data == IntPtr.Zero)
			{
				return null;
			}
			byte[] result = new byte[output.Size];
			Marshal.Copy(output.Data, result, 0, output.Size);
			return result;
		}
		finally
		{
			if (output.Data != IntPtr.Zero)
			{
				Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
				LocalFree(output.Data);
			}
			dataPin.Free();
		}
	}

	// Строка → base64 зашифрованных байтов; пустая строка остаётся пустой.
	public static string Protect(string plain)
	{
		if (string.IsNullOrEmpty(plain))
		{
			return "";
		}
		byte[] data = Encoding.UTF8.GetBytes(plain);
		try
		{
			byte[]? result = Run(data, protect: true);
			return result != null ? Convert.ToBase64String(result) : "";
		}
		finally
		{
			CryptographicOperations.ZeroMemory(data);
		}
	}

	// base64 → строка; "" при любой ошибке (испорчено, другой пользователь или ПК).
	public static string Unprotect(string base64)
	{
		if (string.IsNullOrWhiteSpace(base64))
		{
			return "";
		}
		byte[] data;
		try
		{
			data = Convert.FromBase64String(base64.Trim());
		}
		catch (FormatException)
		{
			return "";
		}
		byte[]? result = Run(data, protect: false);
		if (result == null)
		{
			return "";
		}
		try
		{
			return Encoding.UTF8.GetString(result);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(result);
		}
	}

	private static byte[]? Run(byte[] data, bool protect)
	{
		GCHandle dataPin = GCHandle.Alloc(data, GCHandleType.Pinned);
		GCHandle entropyPin = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
		DataBlob output = default;
		try
		{
			DataBlob input = new DataBlob { Size = data.Length, Data = dataPin.AddrOfPinnedObject() };
			DataBlob entropy = new DataBlob { Size = Entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
			bool ok = protect
				? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
				: CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
			if (!ok || output.Data == IntPtr.Zero)
			{
				return null;
			}
			byte[] result = new byte[output.Size];
			Marshal.Copy(output.Data, result, 0, output.Size);
			return result;
		}
		finally
		{
			if (output.Data != IntPtr.Zero)
			{
				// Расшифрованный ключ не оставляем в памяти, выделенной crypt32.
				Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
				LocalFree(output.Data);
			}
			dataPin.Free();
			entropyPin.Free();
		}
	}
}
