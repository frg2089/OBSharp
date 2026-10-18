using System.Text.Json.Serialization;

namespace OBSharp.Loader.Models;

internal sealed record AppSettings(
    [property: JsonPropertyName("Runtime")] Runtime Runtime
);

internal sealed record Runtime(
    [property: JsonPropertyName("RuntimeFramework"), JsonConverter(typeof(JsonStringEnumConverter<RuntimeFramework>))]
    RuntimeFramework RuntimeFramework = RuntimeFramework.Core
);
