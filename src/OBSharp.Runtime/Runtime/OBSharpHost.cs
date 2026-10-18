using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

/// <summary>
/// OB# 这个 OBS 模块的宿主：把 OBS 的生命周期回调转给 <see cref="OBSharpPluginManager"/>，
/// 顺手同步语言。插件本身不归它管。
/// </summary>
internal sealed partial class OBSharpHost(
    OBSharpPluginManager pluginManager,
    ILoggerFactory loggerFactory,
    ILogger<OBSharpHost> logger)
{
    /// <summary>
    /// 插件管理，插件界面用它启停插件。
    /// </summary>
    public OBSharpPluginManager PluginManager => pluginManager;

    public bool Load()
    {
        try
        {
            logger.LogDebug("Load");
            pluginManager.LoadPlugins();
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
            pluginManager.PostLoadPlugins();
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
            pluginManager.UnloadPlugins();
            logger.LogInformation("Unloaded.");
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
        finally
        {
            // OBS 退出时才卸载 OB#，日志工厂顺手收掉。
            loggerFactory.Dispose();
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

    [LoggerMessage(LogLevel.Information, "Locale: {Locale}")]
    private partial void LogLocale(string? locale);

    [LoggerMessage(LogLevel.Information, "CultureInfo: {culture}")]
    private partial void LogCultureInfo(CultureInfo culture);

    [LoggerMessage(LogLevel.Error, "Unhandled Exception in {Method}")]
    private partial void LogError(Exception ex, [CallerMemberName] string method = "");
}
