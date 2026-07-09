using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OBSharp.Plugins;

using ObsInterop;

namespace OBSharp.Hosts;

public sealed class OBSModuleHostOptions
{
    [Required]
    public string ContentPath { get; set; } = default!;
}

public sealed partial class OBSModuleHost(
    PluginManager pluginManager,
    IOptionsMonitor<OBSModuleHostOptions> options,
    ILogger<OBSModuleHost> logger,
    IEnumerable<ObsPlugin> plugins)
{
    private unsafe obs_module* _obs_module_pointer;
    public OBSModuleHostOptions Options => options.CurrentValue;

    public unsafe void SetPointer(obs_module* module)
    {
        _obs_module_pointer = module;
        logger.LogDebug("SetPointer");
        LogContentPath(Options.ContentPath);
        logger.LogInformation("Initialized.");
    }

    public bool Load()
    {
        logger.LogDebug("Load");

        foreach (var plugin in plugins)
        {
            if (!plugin.Load())
                logger.LogWarning("Plugin {} failed to load.", plugin.GetType());
        }

        return true;
    }

    public void Unload()
    {
        logger.LogDebug("Unload");
        foreach (var plugin in plugins)
        {
            plugin.Unload();
            pluginManager.UnloadPlugins(plugin.GetType());
        }
    }

    public void PostLoad()
    {
        logger.LogDebug("PostLoad");
        foreach (var plugin in plugins)
        {
            plugin.PostLoad();
        }
    }

    public void SetLocale(string? locale)
    {
        LogLocale(locale);
    }

    public void FreeLocale()
    {
        logger.LogDebug("FreeLocale");
    }

    [LoggerMessage(LogLevel.Debug, "ContentPath: {ContentPath}")]
    private partial void LogContentPath(string contentPath);

    [LoggerMessage(LogLevel.Information, "Locale: {Locale}")]
    private partial void LogLocale(string? locale);
}
