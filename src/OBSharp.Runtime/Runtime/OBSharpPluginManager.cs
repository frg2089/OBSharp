using System.Text;

using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

/// <summary>
/// 插件管理：读插件列表、按需加载和卸载插件、把启停状态写回列表文件。
/// 宿主（<see cref="OBSharpHost"/>）用它转接 OBS 的生命周期回调，插件界面用它启停插件。
/// </summary>
public sealed partial class OBSharpPluginManager
{
    private const string ListFileName = "plugins.ini";

    // 写回列表时顶上的那行注释。
    private const string ListHeader = "; OB# 插件列表：<程序集路径> = <true|false>，由 OB# 维护，写回时手写的注释会被覆盖。";

    private readonly OBSharpHostOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OBSharpPluginManager> _logger;
    private readonly string _listPath;
    private readonly List<OBSharpPluginEntry> _entries = [];

    // 启停插件期间一直抱着这把锁：插件的 Load/Unload 可能从不同线程进来。
    // 插件回调里调本类的方法没问题（同一个线程，Lock 可重入），但别在回调里等别的线程来调这里。
    private readonly Lock _gate = new();

    internal OBSharpPluginManager(OBSharpHostOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OBSharpPluginManager>();
        _listPath = Path.Combine(options.DataPath, ListFileName);

        LogPluginList(_listPath);
        ReadPluginList();
    }

    /// <summary>
    /// 插件列表快照，包含未启用和加载失败的项。
    /// </summary>
    public IReadOnlyList<OBSharpPluginEntry> Plugins
    {
        get
        {
            lock (_gate)
                return [.. _entries];
        }
    }

    /// <summary>
    /// 按程序集路径（相对路径按数据目录解析）或插件名找一项，找不到返回 null。
    /// 插件名只有加载成功之后才有，没加载的项只能按路径找。
    /// </summary>
    public OBSharpPluginEntry? Find(string pathOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrName);

        string path = ResolvePath(pathOrName);
        lock (_gate)
        {
            return _entries.Find(entry =>
                entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Name, pathOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 把插件加进列表（默认停用，要跑起来再调 <see cref="Enable"/>），并把列表写回文件。
    /// 路径已经在列表里的话直接返回原来那一项。
    /// </summary>
    public OBSharpPluginEntry Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        OBSharpPluginEntry entry = new(ResolvePath(path), enabled: false);
        lock (_gate)
        {
            if (_entries.Find(item => item.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)) is { } exists)
                return exists;

            _entries.Add(entry);
            Save();
        }

        return entry;
    }

    /// <summary>
    /// 先停用插件，再从列表里删掉，并把列表写回文件。
    /// </summary>
    public bool Remove(OBSharpPluginEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (!_entries.Remove(entry))
                return false;

            Unload(entry);
            Save();
            return true;
        }
    }

    /// <summary>
    /// 启用插件：加载程序集、构造插件、附加 <see cref="ObsPluginContext"/>，
    /// 然后调用 <see cref="ObsPlugin.Load"/> 和 <see cref="ObsPlugin.PostLoad"/>。
    /// 失败会回滚（<see cref="ObsPlugin.Unload"/> 加卸载加载上下文），状态变成
    /// <see cref="OBSharpPluginStatus.Failed"/>，原因记在 <see cref="OBSharpPluginEntry.Error"/>。
    /// </summary>
    public bool Enable(OBSharpPluginEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (!_entries.Contains(entry))
                throw new ArgumentException($"Plugin \"{entry.Path}\" is not in the plugin list.", nameof(entry));

            if (entry.Status is OBSharpPluginStatus.Enabled)
                return true;

            bool loaded = Load(entry);

            // 运行时启用的插件错过了模块的 post_load 阶段，紧跟着它自己的 Load 补上。
            if (loaded)
                PostLoad(entry.Plugin!);

            Save();
            return loaded;
        }
    }

    /// <summary>
    /// 停用插件：调用 <see cref="ObsPlugin.Unload"/>、释放插件，再卸掉它的加载上下文。
    /// 之后可以重新启用，程序集是重新加载的。已经停用的插件是空操作。
    /// </summary>
    public bool Disable(OBSharpPluginEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (!_entries.Contains(entry))
                throw new ArgumentException($"Plugin \"{entry.Path}\" is not in the plugin list.", nameof(entry));

            if (entry.Status is OBSharpPluginStatus.Disabled)
                return true;

            Unload(entry);
            Save();
            return true;
        }
    }

    // 宿主 obs_module_load：把列表里标了 true 的插件都启起来，单个插件失败不影响别的插件。
    internal void LoadPlugins()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.ToArray())
            {
                if (entry.StartEnabled && entry.Status is OBSharpPluginStatus.Disabled)
                    Load(entry);
            }
        }
    }

    // 宿主 obs_module_post_load：所有插件都 Load 完之后调用。
    internal void PostLoadPlugins()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.ToArray())
            {
                if (entry is { Status: OBSharpPluginStatus.Enabled, Plugin: { } plugin })
                    PostLoad(plugin);
            }
        }
    }

    // 宿主 obs_module_unload：全部停用。列表文件保持原样，下次启动照旧。
    internal void UnloadPlugins()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.ToArray())
                Unload(entry);
        }
    }

    // 加载插件。任何一步失败都回滚成 Failed 并留下原因。
    private bool Load(OBSharpPluginEntry entry)
    {
        // 列表里那一项已经处理过了；之后是不是启用只看 Status。
        entry.StartEnabled = false;

        try
        {
            if (LoadPlugin(entry))
            {
                LogLoadedPlugin(entry.Path);
                return true;
            }

            // Load() 自己说失败，不是异常：记一条警告再回滚。
            LogPluginLoadFailed(entry.Path);
            Rollback(entry, "Load() returned false.");
        }
        catch (Exception ex)
        {
            LogLoadPluginFailed(ex, entry.Path);
            Rollback(entry, ex.Message);
        }

        return false;
    }

    // 挂上加载上下文、构造插件、附加 Context，最后调插件的 Load。
    private bool LoadPlugin(OBSharpPluginEntry entry)
    {
        if (!File.Exists(entry.Path))
            throw new FileNotFoundException("Plugin assembly not found.", entry.Path);

        // 先把加载上下文挂到 entry 上：后面哪一步失败，Rollback 都能把它卸掉。
        entry.SetLoadContext(new OBSharpPluginLoadContext(entry.Path));

        Type type = FindPluginType(entry.LoadContext!, entry.Path);
        if (Activator.CreateInstance(type) is not ObsPlugin plugin)
            throw new InvalidOperationException($"Cannot create plugin \"{type}\".");

        // 插件构造函数必须无参，需要什么都在 Context 里；Context 也只有宿主能造。
        // 名字和描述只在这里取一次：名字既当数据目录名，也当界面上的显示名，之后不再问插件
        //（插件名是插件自己的属性，恶意插件可能每次取值都往里塞东西）。
        string name = plugin.Name;
        string? description = plugin.Description;
        plugin.Attach(new ObsPluginContext(
            GetDataFolder(name),
            _options.RuntimeFramework,
            _loggerFactory.CreateLogger(type.FullName ?? type.Name)));
        entry.SetLoaded(plugin, name, description);

        return plugin.Load();
    }

    // 回滚没加载起来的插件：插件自己的 Unload/Dispose 抛异常也要把加载上下文卸掉。
    private void Rollback(OBSharpPluginEntry entry, string error)
    {
        Unload(entry);
        entry.SetFailed(error);
    }

    // 调用方持锁。停用插件：先让插件自己收拾，再卸掉加载上下文并清掉引用
    //（引用留着的话，可卸载的程序集就卸不掉了）。
    private void Unload(OBSharpPluginEntry entry)
    {
        if (entry.Plugin is { } plugin)
        {
            LogUnloadPlugin(entry.Path);
            try
            {
                plugin.Unload();
                if (plugin is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception ex)
            {
                LogUnloadPluginFailed(ex, entry.Path);
            }
        }

        OBSharpPluginLoadContext? loadContext = entry.LoadContext;
        entry.SetDisabled();
        loadContext?.Unload();
    }

    private void PostLoad(ObsPlugin plugin)
    {
        try
        {
            plugin.PostLoad();
        }
        catch (Exception ex)
        {
            LogPostLoadFailed(ex, plugin.GetType());
        }
    }

    // 一个程序集里只允许一个 ObsPlugin 子类：多了少了都是配置错误。
    private static Type FindPluginType(OBSharpPluginLoadContext loadContext, string path)
    {
        var assembly = loadContext.LoadFromAssemblyPath(path);
        Type[] types = [.. assembly.ExportedTypes.Where(static type =>
            type is { IsClass: true, IsAbstract: false } && typeof(ObsPlugin).IsAssignableFrom(type))];

        return types.Length switch
        {
            1 => types[0],
            0 => throw new InvalidOperationException($"Plugin \"{path}\" has no ObsPlugin implementation."),
            _ => throw new InvalidOperationException($"Plugin \"{path}\" has {types.Length} ObsPlugin implementations."),
        };
    }

    // 插件名会直接当目录名用，别让它跳出数据目录。
    private string GetDataFolder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathRooted(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Plugin name \"{name}\" is not a valid folder name.", nameof(name));

        string dataFolder = Path.Combine(_options.DataPath, name);
        Directory.CreateDirectory(dataFolder);
        return dataFolder;
    }

    // 相对路径按插件列表文件所在目录（也就是 OB# 数据目录）解析。
    private string ResolvePath(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_options.DataPath, path));

    // 读插件列表：<程序集路径> = <true|false>，分号和 # 后面是注释。
    private void ReadPluginList()
    {
        if (!File.Exists(_listPath))
            return;

        foreach (string raw in File.ReadLines(_listPath))
        {
            ReadOnlySpan<char> line = raw;
            int comment = line.IndexOfAny(';', '#');
            if (comment >= 0)
                line = line[..comment];

            line = line.Trim();
            if (line.IsEmpty)
                continue;

            int separator = line.IndexOf('=');
            ReadOnlySpan<char> path = (separator < 0 ? line : line[..separator]).Trim();
            ReadOnlySpan<char> status = (separator < 0 ? default : line[(separator + 1)..]).Trim();
            if (path.IsEmpty)
                continue;

            string fullPath;
            try
            {
                fullPath = ResolvePath(path.ToString());
            }
            catch (ArgumentException ex)
            {
                LogInvalidPluginPath(ex, path.ToString());
                continue;
            }

            // 同一个路径只留一项。
            if (_entries.Exists(entry => entry.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            // 只有 true 才启用，别的值（包括写错的）都只是列在表里，不会加载。
            _entries.Add(new OBSharpPluginEntry(fullPath, enabled: bool.TryParse(status, out bool enabled) && enabled));
        }
    }

    // 写回插件列表。写不进去也不能影响插件运行（比如文件是只读的）。
    private void Save()
    {
        try
        {
            string[] lines =
            [
                ListHeader,
                .. _entries.Select(entry =>
                    $"{entry.Path} = {(entry.Status is OBSharpPluginStatus.Disabled ? "false" : "true")}")
            ];
            File.WriteAllLines(_listPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            LogSavePluginListFailed(ex, _listPath);
        }
    }

    [LoggerMessage(LogLevel.Debug, "Plugin list: {ListPath}")]
    private partial void LogPluginList(string listPath);

    [LoggerMessage(LogLevel.Information, "Loaded plugin {PluginPath}")]
    private partial void LogLoadedPlugin(string pluginPath);

    [LoggerMessage(LogLevel.Warning, "Plugin {PluginPath} failed to load: Load() returned false")]
    private partial void LogPluginLoadFailed(string pluginPath);

    [LoggerMessage(LogLevel.Error, "Cannot load plugin {PluginPath}")]
    private partial void LogLoadPluginFailed(Exception exception, string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Unload plugin {PluginPath}")]
    private partial void LogUnloadPlugin(string pluginPath);

    [LoggerMessage(LogLevel.Error, "Plugin {PluginPath} failed to unload")]
    private partial void LogUnloadPluginFailed(Exception exception, string pluginPath);

    [LoggerMessage(LogLevel.Error, "Plugin {PluginType} failed in PostLoad")]
    private partial void LogPostLoadFailed(Exception exception, Type pluginType);

    [LoggerMessage(LogLevel.Warning, "Skip invalid plugin path {PluginPath}")]
    private partial void LogInvalidPluginPath(Exception exception, string pluginPath);

    [LoggerMessage(LogLevel.Error, "Cannot write plugin list {ListPath}")]
    private partial void LogSavePluginListFailed(Exception exception, string listPath);
}
