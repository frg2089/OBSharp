using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OBSharp.Interop;

namespace OBSharp.Runtime;

internal sealed unsafe partial class OBSharpHost(
    OBSharpPluginManager pluginManager,
    IOptions<OBSharpHostOptions> options,
    ILogger<OBSharpHost> logger) : IDataFolderProvider, IRuntimeInfoProvider
{
    private ObsModule* _obs_module_pointer;
    public OBSharpHostOptions Options => options.Value;

    public bool UseAspNet => Options.UseAspNet;
    public bool UseWindowsDesktop => Options.UseWindowsDesktop;

    public string GetDataFolder(string name) => Path.Combine(Options.ContentPath, name);

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
                if (!plugin.Load())
                    logger.LogWarning("Plugin {} failed to load.", plugin.GetType());
            }

            return true;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
    }

    public void Unload()
    {
        try
        {
            logger.LogDebug("Unload");
            foreach (var plugin in pluginManager.GetPlugins())
                pluginManager.UnloadPlugin(plugin);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    public void PostLoad()
    {
        try
        {
            logger.LogDebug("PostLoad");
            foreach (var plugin in pluginManager.GetPlugins())
                plugin.PostLoad();
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
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
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
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    [LoggerMessage(LogLevel.Debug, "ContentPath: {ContentPath}")]
    private partial void LogContentPath(string contentPath);

    [LoggerMessage(LogLevel.Information, "Locale: {Locale}")]
    private partial void LogLocale(string? locale);

    [LoggerMessage(LogLevel.Information, "CultureInfo: {culture}")]
    private partial void LogCultureInfo(CultureInfo culture);

    [LoggerMessage(LogLevel.Error, "Unhandled Exception in {Method}")]
    private partial void LogError(Exception ex, [CallerMemberName] string method = "");
}
