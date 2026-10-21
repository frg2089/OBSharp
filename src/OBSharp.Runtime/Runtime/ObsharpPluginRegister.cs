using System.Collections.Immutable;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OBSharp.Runtime;

internal sealed partial class OBSharpPluginRegister(
    IOptions<OBSharpHostOptions> options,
    ILogger<OBSharpPluginRegister> logger)
{
    private const string ListFileName = "plugins.ini";
    private const string ListHeader = "; OB# 插件列表：<程序集路径> = <true|false>，由 OB# 维护，写回时手写的注释会被覆盖。";
    private readonly string _listPath = Path.Combine(options.Value.DataPath, ListFileName);

    private readonly Dictionary<string, bool> _plugins = [];

    public IReadOnlyDictionary<string, bool> Plugins => _plugins;

    public void Add(string path, bool enabled = true)
    {
        var resolved = ResolvePath(path);
        LogRegisterAdd(resolved, enabled);
        _plugins[resolved] = enabled;
        Save();
    }

    public bool Remove(string path)
    {
        var resolved = ResolvePath(path);
        var result = _plugins.Remove(resolved);
        if (result)
        {
            LogRegisterRemove(resolved);
            Save();
        }
        else
        {
            LogRegisterRemoveMissing(resolved);
        }

        return result;
    }

    public void Load()
    {
        LogRegisterPath(_listPath);

        if (!File.Exists(_listPath))
        {
            // 第一次运行是正常情况：等第一次 Save() 才创建列表。
            LogRegisterListMissing(_listPath);
            return;
        }

        using var reader = File.OpenText(_listPath);
        var lineNumber = 0;
        var parsed = 0;
        var skipped = 0;
        while (reader.ReadLine() is string raw)
        {
            lineNumber++;

            ReadOnlySpan<char> line = raw;
            int comment = line.IndexOfAny(';', '#');
            if (comment >= 0)
                line = line[..comment];

            line = line.Trim();
            if (line.IsEmpty)
                continue;

            int separator = line.IndexOf('=');
            if (separator is not >= 0)
            {
                LogRegisterLineSkipped(lineNumber, "no '=' separator");
                skipped++;
                continue;
            }

            var path = line[..separator].Trim();
            var status = line[(separator + 1)..].Trim();
            if (path.IsEmpty)
            {
                LogRegisterLineSkipped(lineNumber, "empty path");
                skipped++;
                continue;
            }

            var resolved = ResolvePath(path);
            var enabled = bool.TryParse(status, out bool value) && value;
            _plugins[resolved] = enabled;
            parsed++;
            LogRegisterEntry(lineNumber, resolved, enabled);
        }

        LogRegisterLoaded(_listPath, parsed, skipped);
    }

    public void Save()
    {
        LogRegisterSaving(_listPath, _plugins.Count);

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

        // $binAny 必须排在 $bin 前面判断：两个前缀都以 "$bin" 开头，先判 $bin 会把
        // "$binAny/..." 里的 "$bin" 吃掉、剩下的 "Any/..." 拼到 bin/64bit 后面。
        //
        // 为什么需要这个前缀：OBS 只告诉模块两个目录 —— 二进制目录（bin/64bit）和
        // 数据目录（data），托管程序集所在的 bin/any 没有对应的 obs_module_t 字段。
        // Loader 是自己从 bin/64bit 往上一级再拼 "any" 找到 crypto 宿主的（见
        // OBSharp.Loader 的 EntryPoints），这里用同一套推导。
        if (path.StartsWith(@"$binAny\") || path.StartsWith("$binAny/"))
        {
            prefix = BinAnyPath;
            path = path[8..];
        }
        else if (path.StartsWith(@"$bin\") || path.StartsWith("$bin/"))
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
        // 顺序跟 ResolvePath 一致：bin/any 里的路径都以 BinaryPath 的父目录开头，
        // 先判 BinaryPath 不会命中，但先判 binAny 可以避免以后把 any 挪到别处时出错。
        if (path.StartsWith(BinAnyPath))
            return Path.Combine("$binAny", path[BinAnyPath.Length..]);
        else if (path.StartsWith(options.Value.BinaryPath))
            return Path.Combine("$bin", path[options.Value.BinaryPath.Length..]);
        else if (path.StartsWith(options.Value.DataPath))
            return Path.Combine("$data", path[options.Value.DataPath.Length..]);
        return path;
    }

    /// <summary>
    /// 托管程序集目录：<c>&lt;bundle&gt;/bin/any</c>。
    /// </summary>
    /// <remarks>
    /// 由二进制目录推导而来（<c>bin/64bit</c> 的同级 <c>any</c>），跟
    /// <c>OBSharp.Loader</c> 找 <c>OBSharp.Runtime.dll</c> 的算法完全一致 ——
    /// 两边各算各的，但必须得出同一个目录，否则宿主和 Loader 会看到不同的插件列表。
    /// </remarks>
    [LoggerMessage(LogLevel.Debug, "Plugin list file: {ListPath}")]
    private partial void LogRegisterPath(string listPath);

    [LoggerMessage(LogLevel.Debug, "Plugin list file does not exist yet: {ListPath}")]
    private partial void LogRegisterListMissing(string listPath);

    [LoggerMessage(LogLevel.Debug, "Plugin list loaded from {ListPath}: {Parsed} entries, {Skipped} skipped")]
    private partial void LogRegisterLoaded(string listPath, int parsed, int skipped);

    [LoggerMessage(LogLevel.Warning, "Skipped plugin list line {LineNumber}: {Reason}")]
    private partial void LogRegisterLineSkipped(int lineNumber, string reason);

    [LoggerMessage(LogLevel.Debug, "Plugin list line {LineNumber}: {PluginPath} (enabled={Enabled})")]
    private partial void LogRegisterEntry(int lineNumber, string pluginPath, bool enabled);

    [LoggerMessage(LogLevel.Debug, "Registering plugin {PluginPath} (enabled={Enabled})")]
    private partial void LogRegisterAdd(string pluginPath, bool enabled);

    [LoggerMessage(LogLevel.Debug, "Unregistered plugin {PluginPath}")]
    private partial void LogRegisterRemove(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Plugin {PluginPath} was not in the list; nothing to remove")]
    private partial void LogRegisterRemoveMissing(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Writing plugin list to {ListPath} ({Count} entries)")]
    private partial void LogRegisterSaving(string listPath, int count);

    private string BinAnyPath
    {
        get
        {
            string binaryPath = options.Value.BinaryPath;
            string? binBase = Path.GetDirectoryName(binaryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return binBase is null ? binaryPath : Path.Combine(binBase, "any");
        }
    }
}
