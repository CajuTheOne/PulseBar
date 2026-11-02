using System.Text.Json.Serialization;

namespace PulseBar.Logi;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LogiConfig))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, KnownDevice>))]
internal sealed partial class LogiJson : JsonSerializerContext
{
}
