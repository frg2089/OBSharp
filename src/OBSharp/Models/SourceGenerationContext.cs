using System.Text.Json.Serialization;

namespace OBSharp.Models;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RuntimeConfig))]
internal partial class SourceGenerationContext : JsonSerializerContext { }
