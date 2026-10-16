using System.Text.Json.Serialization;

namespace OBSharp.Loader.Models;

internal sealed record AppSettings(
    [property: JsonPropertyName("Runtime")] Runtime Runtime
);

internal sealed record Runtime(
    [property: JsonPropertyName("UseAspNet")] bool UseAspNet,
    [property: JsonPropertyName("UseWindowsDesktop")] bool UseWindowsDesktop
);
