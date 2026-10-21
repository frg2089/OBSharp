namespace OBSharp.Runtime;

internal sealed class OBSharpPluginInfo(string path)
{
    public string Path { get; } = path;
    public OBSharpPluginStatus Status { get; set; }

    public OBSharpPluginLoadContext? LoadContext { get; set; }
    public ObsPlugin? Instance { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }

    public Exception? Exception { get; set; }
}
