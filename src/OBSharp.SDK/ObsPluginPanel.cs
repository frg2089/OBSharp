using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OBSharp.Interop;
using OBSharp.Interop.Browser;

namespace OBSharp;

/// <summary>
/// 把插件的 Web 应用挂成 OBS 的一个停靠面板：Web 应用起来（Kestrel 开始监听）之后，
/// 用一个 <see cref="CefBrowser"/> 连上它自己的 <c>endpoint</c>，再把 Widget 交给
/// <c>obs_frontend_add_dock_by_id()</c>；应用停下时用 <c>obs_frontend_remove_dock()</c>
/// 把面板撤掉。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="ObsPluginContext.CreateBuilder"/> 注册成宿主服务，所以面板跟着 Web 应用的
/// 生命周期走。进程里没有 obs-browser、CEF 起不来、或者面板 id 被占了，都只记一条日志，
/// 不影响插件本身。
/// </para>
/// <para>
/// 建浏览器要等 CEF 就绪（冷启动几秒），而且 <see cref="OBSharpNative.InvokeUi"/> 会同步
/// 切到 Qt 主线程，所以注册放在后台做（<see cref="StartAsync"/> 立刻返回）；停止时不后台
/// 任务，避免在 Qt 主线程上停应用时和它互相等。
/// </para>
/// </remarks>
internal sealed partial class ObsPluginPanel(string name, string endpoint, ILogger logger) : IHostedService
{
    private readonly Lock _gate = new();
    private CefBrowser? _browser;
    private bool _stopped;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(Setup, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        CefBrowser? browser;
        lock (_gate)
        {
            _stopped = true;
            browser = _browser;
            _browser = null;
        }

        if (browser is null)
            return Task.CompletedTask;

        try
        {
            OBSharpNative.InvokeUi(() => ObsFrontend.RemoveDock(name));
        }
        catch (Exception ex)
        {
            LogRemoveDockFailed(ex, name);
        }

        // Widget 一旦交给 dock 就归 OBS 了：RemoveDock 会让 OBS 销毁它，这里再销毁浏览器
        // 等于二次释放。丢掉引用并挡掉终结器，原生那份留给进程回收。原生侧以后要是能只销毁
        // 浏览器、不碰已经交出去的 Widget，这里就不必漏。
        GC.SuppressFinalize(browser);
        return Task.CompletedTask;
    }

    private void Setup()
    {
        try
        {
            OBSharpResult result = OBSharpNative.Initialize(name);
            if (result is not OBSharpResult.Ok)
            {
                // 大多是没有 obs-browser：插件照常跑，只是没有面板。
                LogBrowserUnavailable(result, name);
                return;
            }

            // 内部会等 CEF 就绪（等不到就抛 OBSharpNativeException）。
            CefBrowser browser = CefBrowser.Create(new BrowserOptions
            {
                Endpoint = endpoint,
            });

            OBSharpNative.InvokeUi(() => Register(browser));
        }
        catch (Exception ex)
        {
            LogSetupFailed(ex, name);
        }
    }

    // 在 Qt 主线程上把浏览器挂成 dock。
    private void Register(CefBrowser browser)
    {
        if (!ObsFrontend.AddDockById(name, name, browser.Widget))
        {
            // id 被占了，或者根本没有前端（不在 OBS 里）：没被 dock 收养，自己收拾掉。
            browser.Dispose();
            LogDockRejected(name);
            return;
        }

        bool stopped;
        lock (_gate)
        {
            stopped = _stopped;
            if (!stopped)
                _browser = browser;
        }

        if (stopped)
        {
            // 应用正好在注册的同时停下了：刚挂上的面板立刻撤掉，浏览器归 dock 就不再销毁。
            ObsFrontend.RemoveDock(name);
            GC.SuppressFinalize(browser);
            return;
        }

        LogPanelRegistered(name);
    }

    [LoggerMessage(LogLevel.Debug, "Browser panel registered for plugin {PluginName}.")]
    private partial void LogPanelRegistered(string pluginName);

    [LoggerMessage(LogLevel.Warning, "Browser panel unavailable for plugin {PluginName}: {Result}.")]
    private partial void LogBrowserUnavailable(OBSharpResult result, string pluginName);

    [LoggerMessage(LogLevel.Warning, "OBS rejected the dock panel for plugin {PluginName} (is the id already in use?).")]
    private partial void LogDockRejected(string pluginName);

    [LoggerMessage(LogLevel.Warning, "Could not remove the dock panel for plugin {PluginName}.")]
    private partial void LogRemoveDockFailed(Exception exception, string pluginName);

    [LoggerMessage(LogLevel.Error, "Setting up the dock panel for plugin {PluginName} failed.")]
    private partial void LogSetupFailed(Exception exception, string pluginName);
}
