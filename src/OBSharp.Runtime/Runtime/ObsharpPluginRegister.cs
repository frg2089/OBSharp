using System.Collections.Immutable;

using Microsoft.Extensions.Options;

namespace OBSharp.Runtime;

internal sealed class OBSharpPluginRegister(IOptions<OBSharpHostOptions> options)
{
    private const string ListFileName = "plugins.ini";
    private const string ListHeader = "; OB# 插件列表：<程序集路径> = <true|false>，由 OB# 维护，写回时手写的注释会被覆盖。";
    private readonly string _listPath = Path.Combine(options.Value.DataPath, ListFileName);

    private readonly Dictionary<string, bool> _plugins = [];

    public IReadOnlyDictionary<string, bool> Plugins => _plugins;

    public void Add(string path, bool enabled = true)
    {
        _plugins[ResolvePath(path)] = enabled;
        Save();
    }

    public bool Remove(string path)
    {
        var result = _plugins.Remove(ResolvePath(path));
        if (result)
            Save();
        return result;
    }

    public void Load()
    {
        if (!File.Exists(_listPath))
            return;

        using var reader = File.OpenText(_listPath);
        while (reader.ReadLine() is string raw)
        {
            ReadOnlySpan<char> line = raw;
            int comment = line.IndexOfAny(';', '#');
            if (comment >= 0)
                line = line[..comment];

            line = line.Trim();
            if (line.IsEmpty)
                continue;

            int separator = line.IndexOf('=');
            if (separator is not >= 0)
                continue;

            var path = line[..separator].Trim();
            var status = line[(separator + 1)..].Trim();
            if (path.IsEmpty)
                continue;

            _plugins[ResolvePath(path)] = bool.TryParse(status, out bool enabled) && enabled;
        }

    }

    public void Save()
    {
        using var writer = File.CreateText(_listPath);
        writer.WriteLine(ListHeader);
        writer.WriteLine();
        foreach (var kvp in _plugins)
        {
            writer.WriteLine($"{RestorePath(kvp.Key)} = {kvp.Value}");
        }
    }

    private string ResolvePath(ReadOnlySpan<char> path)
    {
        string prefix = string.Empty;
        if (path.StartsWith(@"$bin\") || path.StartsWith("$bin/"))
        {
            prefix = options.Value.BinaryPath;
            path = path[5..];
        }
        else if (path.StartsWith(@"$data\") || path.StartsWith("$data/"))
        {
            prefix = options.Value.DataPath;
            path = path[6..];
        }

        string final = string.IsNullOrEmpty(prefix)
            ? path.ToString()
            : Path.Combine(prefix, path.ToString());

        return Path.GetFullPath(final);
    }

    private string RestorePath(string path)
    {
        if (path.StartsWith(options.Value.BinaryPath))
            return Path.Combine("$bin", path[options.Value.BinaryPath.Length..]);
        else if (path.StartsWith(options.Value.DataPath))
            return Path.Combine("$data", path[options.Value.DataPath.Length..]);
        return path;
    }
}
