using System.Text.Json.Serialization;

using OBSharp.Loader.Models;

namespace OBSharp.Loader.Models;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RuntimeConfig))]
internal partial class SourceGenerationContext : JsonSerializerContext { }
