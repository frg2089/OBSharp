using System.Text.Json;
using System.Text.Json.Serialization;

namespace OBSharp.Models;

internal sealed record class RuntimeConfig
{
    [JsonPropertyName("runtimeOptions")]
    public RuntimeOptions RuntimeOptions { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

internal sealed record class RuntimeOptions
{
    [JsonPropertyName("tfm")]
    public string Tfm { get; set; } = "net10.0";

    [JsonPropertyName("framework")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Framework? Framework { get; set; }

    [JsonPropertyName("frameworks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Framework>? Frameworks { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

internal sealed record class Framework(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);
