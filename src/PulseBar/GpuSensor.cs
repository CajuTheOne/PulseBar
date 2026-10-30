using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PulseBar;

internal sealed class GpuSensor : IDisposable
{
	public sealed record Adapter(string Key, string Name, ulong DedicatedMemory);

	private const int QaiSegmentSize = 3;

	private const int QaiNodeMetadata = 25;

	private const int QaiDeviceIds = 31;

	private const int QaiAdapterPerfData = 62;

	private const int QsAdapter = 0;

	private const int QsSegment = 3;

	private const int QsNode = 5;

	private const int QsSize = 808;

	private const int QsResult = 24;

	private const int QsQueryNode = 800;

	private const int SecondaryEvery = 4;

	private string _preferred = "";

	private uint _hAdapter;

	private uint _luidLow;

	private uint _devInst;

	private int _luidHigh;

	private int _tick;

	private long _retryAt;

	private uint[] _nodeIds = Array.Empty<uint>();

	private bool[] _primary = Array.Empty<bool>();

	private bool[] _active = Array.Empty<bool>();

	private long[] _prevRun = Array.Empty<long>();

	private long[] _prevTs = Array.Empty<long>();

	private uint[] _vramSegments = Array.Empty<uint>();

	private ulong _vramTotal;

	public List<Adapter> Adapters { get; } = new List<Adapter>();

	public Adapter? Current { get; private set; }

	public unsafe void Open(string preferredKey)
	{
		Close();
		_preferred = preferredKey;
		Adapters.Clear();
		byte* ptr = stackalloc byte[16];
		new Span<byte>(ptr, 16).Clear();
		if (Native.D3DKMTEnumAdapters2(ptr) != 0)
		{
			return;
		}
		uint num = *(uint*)ptr;
		if (num == 0)
		{
			return;
		}
		byte[] array = new byte[num * 20];
		List<(Adapter, uint, uint, int)> list = new List<(Adapter, uint, uint, int)>();
		byte* ptr2 = stackalloc byte[28];
		ulong* ptr3 = stackalloc ulong[3];
		uint devInst;
		fixed (byte* ptr4 = array)
		{
			*(byte**)(ptr + 8) = ptr4;
			if (Native.D3DKMTEnumAdapters2(ptr) != 0)
			{
				return;
			}
			num = *(uint*)ptr;
			for (int i = 0; i < num; i++)
			{
				byte* num2 = ptr4 + i * 20;
				uint num3 = *(uint*)num2;
				uint item = ((uint*)num2)[1];
				int item2 = ((int*)num2)[2];
				new Span<byte>(ptr2, 28).Clear();
				uint num4 = 0u;
				uint value = 0u;
				if (QueryInfo(num3, 31, ptr2, 28) == 0)
				{
					num4 = ((uint*)ptr2)[1];
					value = ((uint*)ptr2)[2];
				}
				if (num4 == 0 || num4 == 5140)
				{
					CloseAdapter(num3);
					continue;
				}
				*ptr3 = (ptr3[1] = (ptr3[2] = 0uL));
				QueryInfo(num3, 3, ptr3, 24);
				string text = $"VEN_{num4:X4}&DEV_{value:X4}";
				list.Add((new Adapter(text, FindName(text, out devInst) ?? text, *ptr3), num3, item, item2));
			}
		}
		if (list.Count == 0)
		{
			return;
		}
		(Adapter, uint, uint, int) tuple = list.FirstOrDefault<(Adapter, uint, uint, int)>(((Adapter a, uint h, uint lo, int hi) f) => f.a.Key == preferredKey);
		if (tuple.Item2 == 0)
		{
			tuple = list.OrderByDescending<(Adapter, uint, uint, int), ulong>(((Adapter a, uint h, uint lo, int hi) f) => f.a.DedicatedMemory).First();
		}
		foreach (var item5 in list)
		{
			Adapters.Add(item5.Item1);
			if (item5.Item2 != tuple.Item2)
			{
				CloseAdapter(item5.Item2);
			}
		}
		Current = tuple.Item1;
		devInst = tuple.Item2;
		uint item3 = tuple.Item3;
		int item4 = tuple.Item4;
		_hAdapter = devInst;
		_luidLow = item3;
		_luidHigh = item4;
		FindName(tuple.Item1.Key, out _devInst);
		byte* ptr5 = stackalloc byte[808];
		uint count = 0u;
		uint count2 = 0u;
		if (QueryStatistics(ptr5, 0, 0u))
		{
			count = ((uint*)ptr5)[6];
			count2 = ((uint*)(ptr5 + 24))[1];
		}
		SelectNodes(count2);
		SelectSegments(count);
		_vramTotal = tuple.Item1.DedicatedMemory;
		_tick = -1;
		Readings r = default(Readings);
		Read(ref r);
	}

	private unsafe void SelectSegments(uint count)
	{
		List<uint> list = new List<uint>();
		byte* ptr = stackalloc byte[808];
		for (uint num = 0u; num < count; num++)
		{
			if (QueryStatistics(ptr, 3, num) && ((uint*)(ptr + 24))[10] == 0)
			{
				list.Add(num);
			}
		}
		_vramSegments = list.ToArray();
	}

	private unsafe void SelectNodes(uint count)
	{
		List<uint> list = new List<uint>();
		List<bool> list2 = new List<bool>();
		byte* ptr = stackalloc byte[80];
		for (uint num = 0u; num < count; num++)
		{
			new Span<byte>(ptr, 80).Clear();
			*(uint*)ptr = num;
			if (QueryInfo(_hAdapter, 25, ptr, 78) != 0)
			{
				list.Add(num);
				list2.Add(item: true);
				continue;
			}
			int num2 = ((int*)ptr)[1];
			string text = new string((char*)ptr + 4, 0, 32);
			int num3 = text.IndexOf('\0');
			if (num3 >= 0)
			{
				text = text.Substring(0, num3);
			}
			bool flag = num2 switch
			{
				0 => text.Contains("Compute", StringComparison.OrdinalIgnoreCase), 
				1 => true, 
				_ => false, 
			};
			bool flag2 = (uint)(num2 - 2) <= 2u || num2 == 9 || (num2 == 0 && text.Contains("Video", StringComparison.OrdinalIgnoreCase));
			if (flag || flag2)
			{
				list.Add(num);
				list2.Add(flag);
			}
		}
		if (list.Count == 0)
		{
			for (uint num4 = 0u; num4 < count; num4++)
			{
				list.Add(num4);
				list2.Add(item: true);
			}
		}
		_nodeIds = list.ToArray();
		_primary = list2.ToArray();
		_active = new bool[list.Count];
		_prevRun = new long[list.Count];
		_prevTs = new long[list.Count];
	}

	public unsafe void Read(ref Readings r)
	{
		r.GpuLoad = (r.GpuTemp = (r.VramLoad = (r.VramUsedGb = float.NaN)));
		r.GpuAsleep = false;
		if (_hAdapter == 0)
		{
			if (Environment.TickCount64 < _retryAt)
			{
				return;
			}
			_retryAt = Environment.TickCount64 + 10000;
			Open(_preferred);
			if (_hAdapter == 0)
			{
				return;
			}
		}
		byte* ptr = stackalloc byte[808];
		long timestamp = Stopwatch.GetTimestamp();
		bool flag = ++_tick % 4 == 0;
		bool flag2 = false;
		double num = 0.0;
		for (int i = 0; i < _nodeIds.Length; i++)
		{
			if (!_primary[i] && !_active[i] && !flag)
			{
				continue;
			}
			if (!QueryStatistics(ptr, 5, _nodeIds[i]))
			{
				Close();
				_retryAt = Environment.TickCount64 + 5000;
				return;
			}
			long num2 = ((long*)ptr)[3];
			if (_prevTs[i] != 0L)
			{
				double num3 = (double)(timestamp - _prevTs[i]) * 10000000.0 / (double)Stopwatch.Frequency;
				if (num3 > 0.0)
				{
					double num4 = (double)(num2 - _prevRun[i]) * 100.0 / num3;
					_active[i] = num4 > 0.5;
					num = Math.Max(num, num4);
					flag2 = true;
				}
			}
			ref long reference = ref _prevRun[i];
			ref long reference2 = ref _prevTs[i];
			long num5 = num2;
			long num6 = timestamp;
			reference = num5;
			reference2 = num6;
		}
		if (flag2)
		{
			r.GpuLoad = (float)Math.Clamp(num, 0.0, 100.0);
		}
		if (_vramTotal != 0L && _vramSegments.Length != 0)
		{
			ulong num7 = 0uL;
			bool flag3 = true;
			uint[] vramSegments = _vramSegments;
			foreach (uint node in vramSegments)
			{
				if (!(flag3 = QueryStatistics(ptr, 3, node)))
				{
					break;
				}
				num7 += (ulong)((long*)(ptr + 24))[2];
			}
			if (flag3)
			{
				r.VramUsedGb = (float)((double)num7 / 1073741824.0);
				r.VramLoad = (float)Math.Clamp((double)num7 * 100.0 / (double)_vramTotal, 0.0, 100.0);
			}
		}
		if (_devInst != 0 && Native.DevicePowerState(_devInst) > 1)
		{
			r.GpuAsleep = true;
			return;
		}
		byte* ptr2 = stackalloc byte[64];
		new Span<byte>(ptr2, 64).Clear();
		if (QueryInfo(_hAdapter, 62, ptr2, 64) == 0)
		{
			uint num8 = ((uint*)ptr2)[14];
			if (num8 != 0)
			{
				r.GpuTemp = (float)num8 / 10f;
			}
		}
	}

	private unsafe bool QueryStatistics(byte* qs, int type, uint node)
	{
		new Span<byte>(qs, 808).Clear();
		*(int*)qs = type;
		((int*)qs)[1] = (int)_luidLow;
		((int*)qs)[2] = _luidHigh;
		((int*)qs)[200] = (int)node;
		return Native.D3DKMTQueryStatistics(qs) == 0;
	}

	private unsafe static int QueryInfo(uint hAdapter, int type, void* data, int size)
	{
		byte* num = stackalloc byte[24];
		new Span<byte>(num, 24).Clear();
		*(uint*)num = hAdapter;
		((int*)num)[1] = type;
		*(void**)(num + 8) = data;
		((int*)num)[4] = size;
		return Native.D3DKMTQueryAdapterInfo(num);
	}

	private static string? FindName(string key, out uint devInst)
	{
		devInst = 0u;
		foreach (string item in Native.DisplayDeviceIds())
		{
			if (item.Contains(key, StringComparison.OrdinalIgnoreCase) && Native.CM_Locate_DevNodeW(out devInst, item, 0u) == 0)
			{
				return Native.DeviceDescription(devInst);
			}
		}
		return null;
	}

	private unsafe static void CloseAdapter(uint h)
	{
		uint num = h;
		Native.D3DKMTCloseAdapter(&num);
	}

	private void Close()
	{
		if (_hAdapter != 0)
		{
			CloseAdapter(_hAdapter);
		}
		_hAdapter = 0u;
		_nodeIds = Array.Empty<uint>();
		_vramSegments = Array.Empty<uint>();
		Current = null;
	}

	public void Dispose()
	{
		Close();
	}
}
