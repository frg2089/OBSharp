using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;

using OBSharp.PluginManager.Components;

namespace OBSharp.PluginManager;

/// <summary>
/// OB# 的插件管理器：把宿主的插件管理 API（OBSharp.Runtime 的 internal 成员）做成一个
/// 页面，同时挂成 OBS 的停靠面板和「工具」菜单里的一项。
/// </summary>
/// <remarks>
/// <para>
/// 职责划分（SDK 不发明窗口体系，三件事分开）：
/// </para>
/// <list type="bullet">
/// <item><b>浏览器</b>：<see cref="ObsBrowser"/>，一块页面，不知道自己要挂到哪。</item>
/// <item><b>工具菜单</b>：<see cref="ObsHost.AddToolsMenuItem"/>，只加菜单项；窗口在
/// <b>点击回调里才创建</b>，关掉就销毁。这是本插件唯一的入口。</item>
/// </list>
/// <para>
/// 入口只有一个：OBS 的「工具」菜单。**没有**停靠面板 —— 实测 OBS 关闭 dock 后
/// 重新显示时，CEF 的 OSR 不再出帧、页面也停了（详见 <see cref="Load"/> 里的说明）。
/// </para>
/// <para>
/// 这个类里<b>没有</b> <c>Task.Run</c>：等 CEF 的活儿由 SDK 的
/// <see cref="ObsWindow.CreateAsync"/> / <see cref="ObsBrowser.CreateAsync"/> 负责，
/// 这里只管 await。
/// </para>
/// </remarks>
public sealed class PluginManagerPlugin : ObsPlugin
{
    /// <summary>
    /// host 名。同时是命名管道 / UDS 的一段、CEF 里 domain 的前缀。
    /// </summary>
    private const string HostName = "plugin-manager";

    /// <summary>
    /// 「工具」菜单和窗口标题上显示的文字。
    /// </summary>
    private const string MenuText = "OB# 插件管理器";

    private readonly Lock _gate = new();
    private ObsHost? _host;

    /// <summary>工具窗口。</summary>
    private ObsWindow? _window;

    /// <summary>工具窗口里那块浏览器。</summary>
    private ObsBrowser? _windowBrowser;

    /// <inheritdoc/>
    public override string? Description => "OB# 插件管理器：启用 / 停用 / 移除 / 添加插件。";

    /// <inheritdoc/>
    public override bool Load()
    {
        // 1. 注册 host（只注册、不启动）。同名幂等。
        ObsHost host = Context.RegisterHost(HostName);
        _host = host;

        // 2. 配 Kestrel：插件管理 API 由宿主建它的时候就做好了，这里塞进 DI 给页面用。
        WebApplicationBuilder builder = host.Builder;
        builder.Services.AddSingleton(OBSModule.PluginManager);

        // 交互式服务器渲染 + 跨组件的状态 + 浏览器工厂（Scoped）。
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton<PluginManagerState>();
        builder.Services.AddObsBrowsers(Context);

        WebApplication app = host.Build();

        if (!app.Environment.IsDevelopment())
            app.UseExceptionHandler("/error", createScopeForErrors: true);

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        // 交互式 SSR 的表单 / DataAnnotations 也要过 antiforgery 中间件。
        app.UseAntiforgery();

        // MapStaticAssets() 默认去 AppContext.BaseDirectory（那是宿主目录，不是插件目录）找
        // 清单；插件的清单在自己旁边，得把绝对路径显式喂进去。
        string assetManifest = Path.Combine(
            Path.GetDirectoryName(typeof(PluginManagerPlugin).Assembly.Location)!,
            $"{typeof(PluginManagerPlugin).Assembly.GetName().Name}.staticwebassets.endpoints.json");

        app.MapStaticAssets(assetManifest);

        // 交互式渲染。必须用 .AddInteractiveServerRenderMode()：交互式组件要求服务端
        // 映射好那套端点，缺了它首页直接 500。
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .WithStaticAssets(assetManifest);

        // 禁掉 SignalR 的 WebSocket，回退到 SSE + 长轮询。
        RestrictBlazorHubTransports(app);

        // 3. 起 Kestrel。起不来就抛出去，宿主会把这个插件回滚掉。
        host.Start();

        // 4. 加「工具」菜单项。**只加菜单项，不建窗口**：点开才创建，关掉就销毁。
        _ = host.AddToolsMenuItem(MenuText, ToggleWindow);

        // 5. 停靠面板：**不挂**。
        //
        // 原因（实测）：OBS 关闭 dock 时只隐藏它，之后重新显示时 CEF 的元窗口渲染
        // （OSR）不再出帧，页面也停了 —— 日志显示重新显示后连一个 HTTP 请求都没有
        // （第一次打开有 `/`、*.css、blazor.web.js 共 4 个请求，第二次之后是 0 个）。
        // 浏览器对象还活着（事件过滤器仍在收 show/hide），但页面已经死掉，靠重绘
        // 踢不回来。这层耦合在 obs-browser 的 dock 生命周期里，不在我们能改的范围。
        //
        // 工具窗口没有这个问题（关掉即销毁、下次重建），所以入口只保留工具菜单。
        // SDK 的 ObsHost.AddDockPanel 仍然保留给能接受这个限制的插件。
        //
        // _ = SetupDockPanelAsync(host);

        return true;
    }

    /// <summary>
    /// 显示或收起工具窗口。菜单项点一次切换一次。
    /// </summary>
    /// <remarks>
    /// 运行在 Qt 主线程上（工具菜单的回调）：<b>不能</b>在这里同步等 CEF，
    /// 所以走 <see cref="EnsureWindowAsync"/>，它把等待放到线程池上。
    /// </remarks>
    private void ToggleWindow()
    {
        ObsWindow? window;
        lock (_gate)
            window = _window;

        // 已经显示着：收起来。
        if (window is { IsVisible: true })
        {
            window.Hide();
            return;
        }

        // 异步补齐：这里不等它（菜单回调不能阻塞）。
        _ = ShowWindowAsync();
    }

    private async Task ShowWindowAsync()
    {
        try
        {
            ObsHost? host;
            lock (_gate)
                host = _host;

            if (host is null)
                return;

            ObsWindow window = await EnsureWindowAsync(host).ConfigureAwait(false);
            window.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OBSharp.PluginManager] 显示工具窗口失败：{ex}");
        }
    }

    /// <summary>
    /// 确保工具窗口存在（关掉过就重建），返回它。
    /// </summary>
    /// <remarks>
    /// 关掉的原生窗口不能复用 —— 里面的 Qt 资源已经回收，再 Show 只会得到一块没有
    /// CEF 的空壳。所以关闭时我们把 <c>_window</c> / <c>_windowBrowser</c> 置空并销毁，
    /// 这里发现是空的就整套重建。
    /// </remarks>
    private async Task<ObsWindow> EnsureWindowAsync(ObsHost host)
    {
        ObsWindow? existing;
        lock (_gate)
            existing = _window;

        if (existing is not null)
            return existing;

        ObsWindow window = await ObsWindow.CreateAsync(new ObsWindowOptions
        {
            Title = MenuText,
            Width = 960,
            Height = 640,
        }).ConfigureAwait(false);

        // 浏览器要等 CEF，同样不阻塞。
        ObsBrowser browser = await Context.CreateBrowserAsync(host).ConfigureAwait(false);
        window.Content = browser.Widget;

        ObsWindow? raced;
        lock (_gate)
        {
            // 两个入口同时点：别人已经建好了就用别人的，把自己这份收掉。
            raced = _window;
            if (raced is null)
            {
                window.CloseRequested += OnWindowCloseRequested;
                _window = window;
                _windowBrowser = browser;
            }
        }

        if (raced is not null)
        {
            window.Dispose();
            browser.Dispose();
            return raced;
        }

        return window;
    }

    /// <summary>
    /// 用户点了工具窗口的关闭按钮：销毁窗口和它里面的浏览器，下次点菜单重建。
    /// </summary>
    /// <remarks>
    /// 运行在 Qt 主线程上（原生 closeEvent 回调过来的）。这里<b>同步</b>把状态改掉，
    /// 销毁动作本身也是同步的（原生侧切线程由 SDK 处理），不起线程。
    /// </remarks>
    private void OnWindowCloseRequested(object? sender, EventArgs e)
    {
        ObsWindow? window;
        ObsBrowser? browser;

        lock (_gate)
        {
            window = _window;
            browser = _windowBrowser;
            _window = null;
            _windowBrowser = null;
        }

        if (window is null)
            return;

        window.CloseRequested -= OnWindowCloseRequested;

        try
        {
            // 先窗口后浏览器：内容的父级是窗口，先撤窗口再销毁内容。
            window.Dispose();
            browser?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OBSharp.PluginManager] 销毁工具窗口失败：{ex}");
        }
    }

    /// <inheritdoc/>
    public override void Unload()
    {
        ObsWindow? window;
        ObsBrowser? windowBrowser;

        lock (_gate)
        {
            window = _window;
            windowBrowser = _windowBrowser;
            _window = null;
            _windowBrowser = null;

            _host = null;
        }

        if (window is not null)
            window.CloseRequested -= OnWindowCloseRequested;

        try
        {
            // 先窗口后浏览器：内容的父级是窗口。
            window?.Dispose();
            windowBrowser?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OBSharp.PluginManager] 卸载清理失败：{ex}");
        }
    }

    /// <summary>
    /// 把 Blazor 的 SignalR Hub 限制成只走 SSE + 长轮询（禁掉 WebSocket）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 面板的 HTTP 全部从命名管道 / UDS 过，原生 <c>KestrelClient</c> 是「一次请求一次
    /// 连接、<c>Connection: close</c>」，接不住 WebSocket 的 <c>Upgrade</c>。所以要把
    /// 传输限制成 SSE（+ 长轮询兜底），否则 SignalR 会优先选 WebSocket 然后连不上。
    /// </para>
    /// <para>
    /// 为什么只能这么做：<c>AddInteractiveServerRenderMode()</c> 自己映射 <c>/_blazor</c>，
    /// 不暴露 <c>HttpConnectionDispatcherOptions</c>；<c>HubOptions</c> 上也没有
    /// <c>Transports</c>（那是 per-endpoint 的选项）。所以启动前遍历端点改那个实例。
    /// </para>
    /// </remarks>
    private static void RestrictBlazorHubTransports(WebApplication app)
    {
        const HttpTransportType allowed = HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;

        foreach (EndpointDataSource source in ((IEndpointRouteBuilder)app).DataSources)
        {
            foreach (Endpoint endpoint in source.Endpoints)
            {
                if (endpoint is not RouteEndpoint route)
                    continue;

                if (route.RoutePattern.RawText?.Contains("_blazor", StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                foreach (object metadata in route.Metadata)
                {
                    if (metadata is HttpConnectionDispatcherOptions options)
                        options.Transports = allowed;
                }
            }
        }
    }
}
