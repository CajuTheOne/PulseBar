using System.Text.Json.Serialization;

namespace PulseBar.Logi;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LogiConfig))]
internal sealed partial class LogiJson : JsonSerializerContext
{
}
