using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OBSharp.Interop;

namespace OBSharp.Runtime;

internal sealed unsafe partial class OBSharpHost(
    OBSharpPluginManager pluginManager,
    IOptions<OBSharpHostOptions> options,
    ILogger<OBSharpHost> logger,
    IServiceProvider services) : IDataFolderProvider, IRuntimeInfoProvider
{
    private ObsModule* _obs_module_pointer;
    public OBSharpHostOptions Options => options.Value;
    public RuntimeFramework RuntimeFramework => Options.RuntimeFramework;

    public string GetDataFolder(string name)
    {
        // 插件名会直接当目录名用，别让它跳出数据目录。
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathRooted(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Plugin name \"{name}\" is not a valid folder name.", nameof(name));

        return Path.Combine(Options.ContentPath, name);
    }

    public void SetPointer(ObsModule* module)
    {
        try
        {
            _obs_module_pointer = module;
            logger.LogDebug("SetPointer");
            LogContentPath(Options.ContentPath);
            logger.LogInformation("Initialized.");
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    public bool Load()
    {
        try
        {
            logger.LogDebug("Load");

            foreach (var plugin in pluginManager.GetPlugins())
            {
                try
                {
                    Attach(plugin);
                    if (!plugin.Load())
                        LogPluginFailed(plugin.GetType(), plugin.Name);
                }
                catch (Exception ex)
                {
                    // 单个插件出问题不该带走其他插件。
                    LogPluginError(ex, plugin.GetType());
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
    }

    public void PostLoad()
    {
        try
        {
            logger.LogDebug("PostLoad");
            foreach (var plugin in pluginManager.GetPlugins())
            {
                try
                {
                    plugin.PostLoad();
                }
                catch (Exception ex)
                {
                    LogPluginError(ex, plugin.GetType());
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    public void Unload()
    {
        try
        {
            logger.LogDebug("Unload");
            foreach (var plugin in pluginManager.GetPlugins())
            {
                try
                {
                    pluginManager.UnloadPlugin(plugin);
                }
                catch (Exception ex)
                {
                    LogPluginError(ex, plugin.GetType());
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    public void SetLocale(string? locale)
    {
        try
        {
            LogLocale(locale);
            if (locale is not null && CultureInfo.GetCultureInfo(locale) is { } culture)
            {
                LogCultureInfo(culture);

                // 插件的回调多半不在当前线程上跑，默认线程文化也要一起设，
                // 插件读 CultureInfo.CurrentUICulture 才能拿到正确的语言。
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    public void FreeLocale()
    {
        try
        {
            logger.LogDebug("FreeLocale");

            // 让之后新建的线程回到系统默认文化。
            CultureInfo.DefaultThreadCurrentUICulture = null;
            CultureInfo.DefaultThreadCurrentCulture = null;
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    // 构造插件需要的上下文并交给插件（ObsPlugin.Attach 只有宿主能调）。
    private void Attach(ObsPlugin plugin)
    {
        string dataFolder = GetDataFolder(plugin.Name);
        Directory.CreateDirectory(dataFolder);
        plugin.Attach(new ObsPluginContext(_obs_module_pointer, dataFolder, services, Options.RuntimeFramework));
    }

    [LoggerMessage(LogLevel.Debug, "ContentPath: {ContentPath}")]
    private partial void LogContentPath(string contentPath);

    [LoggerMessage(LogLevel.Information, "Locale: {Locale}")]
    private partial void LogLocale(string? locale);

    [LoggerMessage(LogLevel.Information, "CultureInfo: {culture}")]
    private partial void LogCultureInfo(CultureInfo culture);

    [LoggerMessage(LogLevel.Warning, "Plugin {PluginType} ({PluginName}) failed to load.")]
    private partial void LogPluginFailed(Type pluginType, string pluginName);

    [LoggerMessage(LogLevel.Error, "Plugin {PluginType} failed in {Method}.")]
    private partial void LogPluginError(Exception exception, Type pluginType, [CallerMemberName] string method = "");

    [LoggerMessage(LogLevel.Error, "Unhandled Exception in {Method}")]
    private partial void LogError(Exception ex, [CallerMemberName] string method = "");
}
