using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

internal sealed partial class OBSharpPluginManager(
    OBSharpPluginRegister register,
    ILogger<OBSharpPluginManager> logger,
    IServiceProvider serviceProvider)
{
    private readonly Lock _lock = new();
    public bool CanLoadedAll { get; set; }
    public List<OBSharpPluginInfo> Plugins { get; } = [.. register.Plugins
        .Where(i => i.Value)
        .Select(i=> new OBSharpPluginInfo(i.Key))];

    public OBSharpPluginInfo? Add(string path, bool enabled = true)
    {
        register.Add(path, enabled);
        if (!enabled)
            return null;

        OBSharpPluginInfo pluginInfo = new(path);
        Load(pluginInfo);
        lock (_lock)
            Plugins.Add(pluginInfo);

        return pluginInfo;
    }

    public void Remove(OBSharpPluginInfo item)
    {
        lock (_lock)
        {
            if (!Plugins.Remove(item))
                return;
        }

        Unload(item);
        register.Remove(item.Path);
    }

    public void InvokeAll(Action<ObsPlugin> action)
    {
        lock (_lock)
        {
            LogInvokeAllStarting(Plugins.Count);

            var invoked = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var plugin in Plugins)
            {
                try
                {
                    if (plugin.Instance is { } instance)
                    {
                        LogInvokeOne(plugin.Name ?? plugin.Path);
                        action(instance);
                        invoked++;
                    }
                    else
                    {
                        LogInvokeSkipped(plugin.Path, plugin.Status);
                        skipped++;
                    }
                }
                catch (Exception ex)
                {
                    plugin.Exception = ex;
                    LogPluginInvokeError(ex, plugin.Path);
                    failed++;
                }
            }

            LogInvokeAllFinished(invoked, skipped, failed);
        }
    }

    public void LoadAll()
    {
        lock (_lock)
        {
            LogLoadAllStarting(Plugins.Count);

            var loaded = 0;
            var failed = 0;

            foreach (var plugin in Plugins)
            {
                if (Load(plugin))
                    loaded++;
                else
                    failed++;
            }

            LogLoadAllFinished(loaded, failed, Plugins.Count);
        }
    }

    public void UnloadAll()
    {
        lock (_lock)
        {
            LogUnloadAllStarting(Plugins.Count);

            foreach (var plugin in Plugins)
                Unload(plugin);

            LogUnloadAllFinished(Plugins.Count);
        }
    }

    public bool Load(OBSharpPluginInfo pluginInfo)
    {
        if (pluginInfo.Status is OBSharpPluginStatus.Failed)
        {
            LogLoadSkippedFailed(pluginInfo.Path);
            return false;
        }

        if (pluginInfo.Status is OBSharpPluginStatus.Loading or OBSharpPluginStatus.Loaded)
        {
            LogLoadSkippedAlready(pluginInfo.Path, pluginInfo.Status);
            return true;
        }

        try
        {
            LogLoadStarting(pluginInfo.Path);

            if (!File.Exists(pluginInfo.Path))
            {
                // 列表里记着但文件没了（插件被删掉、或者部署不完整）。单独记一条：
                // 后面 LoadFromAssemblyPath 抛的 FileNotFoundException 看不出是「列表过期」。
                LogLoadFileMissing(pluginInfo.Path);
                pluginInfo.Status = OBSharpPluginStatus.Failed;
                return false;
            }

            pluginInfo.Status = OBSharpPluginStatus.Loading;
            LogLoadCreatingContext(pluginInfo.Path);
            pluginInfo.LoadContext = new(pluginInfo.Path, logger);

            LogLoadLoadingAssembly(pluginInfo.Path);
            var asm = pluginInfo.LoadContext.LoadFromAssemblyPath(pluginInfo.Path);
            LogLoadAssemblyLoaded(asm.FullName ?? "(unknown)", asm.GetName().Version?.ToString() ?? "(no version)");

            var exported = asm.ExportedTypes.ToList();
            LogLoadScanningTypes(exported.Count);

            var type = exported.FirstOrDefault(i => typeof(ObsPlugin).IsAssignableFrom(i) && !i.IsAbstract);
            if (type is null)
            {
                LogPluginTypeNotFound(pluginInfo.Path);
                pluginInfo.Status = OBSharpPluginStatus.Failed;
                return false;
            }

            LogLoadTypeFound(type.FullName ?? type.Name);

            LogLoadCreatingInstance(type.FullName ?? type.Name);
            if (ActivatorUtilities.CreateInstance(serviceProvider, type) is not ObsPlugin plugin)
            {
                LogPluginLoadError(pluginInfo.Path, type);
                pluginInfo.Status = OBSharpPluginStatus.Failed;
                return false;
            }

            LogLoadCallingLoad(type.FullName ?? type.Name);
            plugin.Load();
            LogLoadSucceeded(pluginInfo.Path, plugin.Name);

            pluginInfo.Name = plugin.Name;
            pluginInfo.Description = plugin.Description;
            pluginInfo.Instance = plugin;
            pluginInfo.Status = OBSharpPluginStatus.Loaded;

            if (CanLoadedAll)
            {
                LogLoadCallingPostLoad(pluginInfo.Name ?? pluginInfo.Path);
                plugin.PostLoad();
            }
            else
            {
                // 启动阶段 LoadAll() 时 CanLoadedAll 还是 false，PostLoad 要等所有人
                // 都 Load 完再统一调 —— 这条日志说明「现在轮不到我」。
                LogLoadPostLoadDeferred(pluginInfo.Path);
            }

            return true;
        }
        catch (Exception ex)
        {
            LogPluginLoadError(ex, pluginInfo.Path);
            pluginInfo.Status = OBSharpPluginStatus.Failed;
            pluginInfo.Exception = ex;
            return false;
        }
    }

    public void Unload(OBSharpPluginInfo pluginInfo)
    {
        if (pluginInfo.Status is OBSharpPluginStatus.Unload or OBSharpPluginStatus.Unloading or OBSharpPluginStatus.Failed)
        {
            LogUnloadSkipped(pluginInfo.Path, pluginInfo.Status);
            return;
        }

        try
        {
            LogUnloadStarting(pluginInfo.Path, pluginInfo.Name ?? "(no name)");
            pluginInfo.Status = OBSharpPluginStatus.Unloading;

            if (pluginInfo.Instance is { } instance)
            {
                LogUnloadCallingUnload(pluginInfo.Name ?? pluginInfo.Path);
                instance.Unload();

                if (instance is IDisposable disposable)
                {
                    LogUnloadDisposing(pluginInfo.Path);
                    disposable.Dispose();
                }
            }
            else
            {
                LogUnloadNoInstance(pluginInfo.Path);
            }

            pluginInfo.Instance = null;

            if (pluginInfo.LoadContext is { } context)
            {
                LogUnloadUnloadingContext(context.Name ?? "(unnamed)");
                pluginInfo.LoadContext = null;

                // 先摘引用再 Unload：不同调用上下文里的程序集卸载是异步的，
                // 拿不到就说明还有活跃引用（比如插件把类型塞进了静态字段）。
                context.Unload();
                LogUnloadContextReleased(pluginInfo.Path);
            }
            else
            {
                LogUnloadNoContext(pluginInfo.Path);
            }

            pluginInfo.Status = OBSharpPluginStatus.Unload;
            LogUnloadSucceeded(pluginInfo.Path);
        }
        catch (Exception ex)
        {
            LogPluginUnloadError(ex, pluginInfo.Path);
            pluginInfo.Status = OBSharpPluginStatus.Failed;
            pluginInfo.Exception = ex;
        }
    }

    [LoggerMessage(LogLevel.Debug, "LoadAll: {Count} plugin(s) in the list")]
    private partial void LogLoadAllStarting(int count);

    [LoggerMessage(LogLevel.Debug, "LoadAll finished: {Loaded} loaded, {Failed} failed, {Total} total")]
    private partial void LogLoadAllFinished(int loaded, int failed, int total);

    [LoggerMessage(LogLevel.Debug, "UnloadAll: {Count} plugin(s) in the list")]
    private partial void LogUnloadAllStarting(int count);

    [LoggerMessage(LogLevel.Debug, "UnloadAll finished: {Count} plugin(s) processed")]
    private partial void LogUnloadAllFinished(int count);

    [LoggerMessage(LogLevel.Debug, "InvokeAll: {Count} plugin(s) in the list")]
    private partial void LogInvokeAllStarting(int count);

    [LoggerMessage(LogLevel.Debug, "InvokeAll finished: {Invoked} invoked, {Skipped} skipped, {Failed} failed")]
    private partial void LogInvokeAllFinished(int invoked, int skipped, int failed);

    [LoggerMessage(LogLevel.Debug, "InvokeAll: invoking on {PluginName}")]
    private partial void LogInvokeOne(string pluginName);

    [LoggerMessage(LogLevel.Debug, "InvokeAll: skipped {PluginPath} (status={Status})")]
    private partial void LogInvokeSkipped(string pluginPath, OBSharpPluginStatus status);

    [LoggerMessage(LogLevel.Debug, "Load: skipped {PluginPath}; it previously failed")]
    private partial void LogLoadSkippedFailed(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Load: skipped {PluginPath}; already {Status}")]
    private partial void LogLoadSkippedAlready(string pluginPath, OBSharpPluginStatus status);

    [LoggerMessage(LogLevel.Debug, "Load: starting {PluginPath}")]
    private partial void LogLoadStarting(string pluginPath);

    [LoggerMessage(LogLevel.Error, "Load: plugin assembly not found: {PluginPath}")]
    private partial void LogLoadFileMissing(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Load: creating load context for {PluginPath}")]
    private partial void LogLoadCreatingContext(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Load: loading assembly {PluginPath}")]
    private partial void LogLoadLoadingAssembly(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Load: assembly loaded: {AssemblyName} {AssemblyVersion}")]
    private partial void LogLoadAssemblyLoaded(string assemblyName, string assemblyVersion);

    [LoggerMessage(LogLevel.Debug, "Load: scanning {Count} exported type(s) for an ObsPlugin subclass")]
    private partial void LogLoadScanningTypes(int count);

    [LoggerMessage(LogLevel.Debug, "Load: found plugin type {PluginType}")]
    private partial void LogLoadTypeFound(string pluginType);

    [LoggerMessage(LogLevel.Debug, "Load: creating instance of {PluginType} via DI")]
    private partial void LogLoadCreatingInstance(string pluginType);

    [LoggerMessage(LogLevel.Debug, "Load: calling {PluginType}.Load()")]
    private partial void LogLoadCallingLoad(string pluginType);

    [LoggerMessage(LogLevel.Information, "Plugin loaded: {PluginName} ({PluginPath})")]
    private partial void LogLoadSucceeded(string pluginPath, string? pluginName);

    [LoggerMessage(LogLevel.Debug, "Load: calling PostLoad() on {PluginName}")]
    private partial void LogLoadCallingPostLoad(string pluginName);

    [LoggerMessage(LogLevel.Debug, "Load: PostLoad deferred for {PluginPath}; waiting for every plugin to load first")]
    private partial void LogLoadPostLoadDeferred(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Unload: skipped {PluginPath} (status={Status})")]
    private partial void LogUnloadSkipped(string pluginPath, OBSharpPluginStatus status);

    [LoggerMessage(LogLevel.Debug, "Unload: starting {PluginName} ({PluginPath})")]
    private partial void LogUnloadStarting(string pluginPath, string pluginName);

    [LoggerMessage(LogLevel.Debug, "Unload: calling Unload() on {PluginName}")]
    private partial void LogUnloadCallingUnload(string pluginName);

    [LoggerMessage(LogLevel.Debug, "Unload: disposing {PluginPath}")]
    private partial void LogUnloadDisposing(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Unload: {PluginPath} has no instance (never loaded?)")]
    private partial void LogUnloadNoInstance(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Unload: unloading load context {ContextName}")]
    private partial void LogUnloadUnloadingContext(string contextName);

    [LoggerMessage(LogLevel.Debug, "Unload: load context released for {PluginPath}")]
    private partial void LogUnloadContextReleased(string pluginPath);

    [LoggerMessage(LogLevel.Debug, "Unload: {PluginPath} has no load context")]
    private partial void LogUnloadNoContext(string pluginPath);

    [LoggerMessage(LogLevel.Information, "Plugin unloaded: {PluginPath}")]
    private partial void LogUnloadSucceeded(string pluginPath);

    [LoggerMessage(LogLevel.Error, "Cannot found ObsPlugin type for {PluginPath}")]
    private partial void LogPluginTypeNotFound(string pluginPath);

    [LoggerMessage(LogLevel.Error, "Failed to load plugin {PluginPath} for type {PluginType}")]
    private partial void LogPluginLoadError(string pluginPath, Type pluginType);

    [LoggerMessage(LogLevel.Error, "Failed to load plugin {PluginPath}")]
    private partial void LogPluginLoadError(Exception ex, string pluginPath);

    [LoggerMessage(LogLevel.Error, "Failed to unload plugin {PluginPath}")]
    private partial void LogPluginUnloadError(Exception ex, string pluginPath);
    [LoggerMessage(LogLevel.Error, "Failed to invoke plugin {PluginPath}")]
    private partial void LogPluginInvokeError(Exception ex, string pluginPath);
}
