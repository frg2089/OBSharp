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
            foreach (var plugin in Plugins)
            {
                try
                {
                    if (plugin.Instance is { } instance)
                        action(instance);
                }
                catch (Exception ex)
                {
                    plugin.Exception = ex;
                    LogPluginInvokeError(ex, plugin.Path);
                }
            }
        }
    }

    public void LoadAll()
    {
        lock (_lock)
        {
            foreach (var plugin in Plugins)
                Load(plugin);
        }
    }

    public void UnloadAll()
    {
        lock (_lock)
        {
            foreach (var plugin in Plugins)
                Unload(plugin);
        }
    }

    public bool Load(OBSharpPluginInfo pluginInfo)
    {
        if (pluginInfo.Status is OBSharpPluginStatus.Failed)
            return false;
        if (pluginInfo.Status is OBSharpPluginStatus.Loading or OBSharpPluginStatus.Loaded)
            return true;

        try
        {
            pluginInfo.Status = OBSharpPluginStatus.Loading;
            pluginInfo.LoadContext = new(pluginInfo.Path);
            var asm = pluginInfo.LoadContext.LoadFromAssemblyPath(pluginInfo.Path);
            var type = asm.ExportedTypes.FirstOrDefault(i => typeof(ObsPlugin).IsAssignableFrom(i) && !i.IsAbstract);
            if (type is null)
            {
                LogPluginTypeNotFound(pluginInfo.Path);
                pluginInfo.Status = OBSharpPluginStatus.Failed;
                return false;
            }

            if (ActivatorUtilities.CreateInstance(serviceProvider, type) is not ObsPlugin plugin)
            {
                LogPluginLoadError(pluginInfo.Path, type);
                pluginInfo.Status = OBSharpPluginStatus.Failed;
                return false;
            }
            plugin.Load();
            pluginInfo.Name = plugin.Name;
            pluginInfo.Description = plugin.Description;
            pluginInfo.Instance = plugin;
            pluginInfo.Status = OBSharpPluginStatus.Loaded;
            if (CanLoadedAll)
                plugin.PostLoad();
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
            return;

        try
        {
            pluginInfo.Status = OBSharpPluginStatus.Unloading;
            pluginInfo.Instance?.Unload();
            (pluginInfo.Instance as IDisposable)?.Dispose();
            pluginInfo.Instance = null;
            pluginInfo.LoadContext?.Unload();
            pluginInfo.LoadContext = null;
            pluginInfo.Status = OBSharpPluginStatus.Unload;
        }
        catch (Exception ex)
        {
            LogPluginUnloadError(ex, pluginInfo.Path);
            pluginInfo.Status = OBSharpPluginStatus.Failed;
            pluginInfo.Exception = ex;
        }
    }

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
