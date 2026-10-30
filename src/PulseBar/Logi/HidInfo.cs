namespace PulseBar.Logi;

internal sealed record HidInfo(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, int InLen, int OutLen, int FeatureLen, string Product);
