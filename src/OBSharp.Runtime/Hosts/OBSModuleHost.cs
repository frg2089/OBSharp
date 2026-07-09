using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OBSharp.Plugins;

namespace OBSharp.Hosts;

internal sealed class OBSModuleHostOptions
{
    public bool UseAspNet { get; set; }
    public bool UseWindowsDesktop { get; set; }
    [Required]
    public string ContentPath { get; set; } = default!;
}

internal sealed partial class OBSModuleHost(
    PluginManager pluginManager,
    IOptions<OBSModuleHostOptions> options,
    ILogger<OBSModuleHost> logger,
    IEnumerable<ObsPlugin> plugins) : IDataFolderProvider, IRuntimeInfoProvider
{
    private unsafe nint _obs_module_pointer;
    public OBSModuleHostOptions Options => options.Value;

    public bool UseAspNet => Options.UseAspNet;
    public bool UseWindowsDesktop => Options.UseWindowsDesktop;

    public string GetDataFolder(string name) => Path.Combine(Options.ContentPath, name);

    public unsafe void SetPointer(nint module)
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

            foreach (var plugin in plugins)
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
            foreach (var plugin in plugins)
            {
                plugin.Unload();
                pluginManager.UnloadPlugins(plugin.GetType());
            }
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
            foreach (var plugin in plugins)
            {
                plugin.PostLoad();
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

    [LoggerMessage(LogLevel.Error, "Unhandled Exception in {Method}")]
    private partial void LogError(Exception ex, [CallerMemberName] string method = "");
}
