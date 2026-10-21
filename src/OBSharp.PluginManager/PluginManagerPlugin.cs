using Microsoft.AspNetCore.Http.Connections;

using OBSharp.Interop;
using OBSharp.PluginManager.Components;
using OBSharp.Runtime;

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
    private const string HostName = "plugin-manager";
    private WebApplicationBuilder? _builder;
    private WebApplication? _app;

    public override string Name => "OB# PluginManager";

    /// <inheritdoc/>
    public override string? Description => "OB# 插件管理器：启用 / 停用 / 移除 / 添加插件。";

    public PluginManagerPlugin(IServiceProvider serviceProvider)
    {
        _builder = CreateBuilder(HostName);
        _builder.Services.AddSingleton(serviceProvider.GetRequiredService<OBSharpPluginManager>());

        // 交互式服务器渲染 + 跨组件的状态
        _builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        _builder.Services.AddSingleton<PluginManagerState>();
        _builder.Services.AddHostedService<PluginManagerMenuItem>();
        _builder.Services.AddTransient<PluginManagerWindow>();
    }

    /// <inheritdoc/>
    public override bool Load()
    {
        if (_builder is null)
            return false;

        _app = _builder.Build();
        _builder = null;

        if (!_app.Environment.IsDevelopment())
            _app.UseExceptionHandler("/error", createScopeForErrors: true);

        _app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        // 交互式 SSR 的表单 / DataAnnotations 也要过 antiforgery 中间件。
        _app.UseAntiforgery();

        // MapStaticAssets() 默认去 AppContext.BaseDirectory（那是宿主目录，不是插件目录）找
        // 清单；插件的清单在自己旁边，得把绝对路径显式喂进去。
        string assetManifest = Path.Combine(
            Path.GetDirectoryName(typeof(PluginManagerPlugin).Assembly.Location)!,
            $"{typeof(PluginManagerPlugin).Assembly.GetName().Name}.staticwebassets.endpoints.json");

        _app.MapStaticAssets(assetManifest);

        // 交互式渲染。必须用 .AddInteractiveServerRenderMode()：交互式组件要求服务端
        // 映射好那套端点，缺了它首页直接 500。
        _app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .WithStaticAssets(assetManifest);

        // 禁掉 SignalR 的 WebSocket，回退到 SSE + 长轮询。
        RestrictBlazorHubTransports(_app);

        _app.Start();
        return true;
    }


    /// <inheritdoc/>
    public override void Unload()
    {
        _app?.StopAsync().Wait();
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
