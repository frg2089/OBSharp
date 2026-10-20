using System.Diagnostics;

using OBSharp.Interop.Browser;

namespace OBSharp;

/// <summary>
/// 一块 CEF 浏览器，可以放进任何 <c>QWidget</c> 容器：OBS 的停靠面板、OB# 的普通窗口，
/// 或者插件自己拿 <see cref="Widget"/> 安排的布局。
/// </summary>
/// <remarks>
/// <para>
/// 这正是「窗口」和「浏览器」解耦的结果：<see cref="ObsBrowser"/> 只管一块页面，
/// 不知道自己要挂到哪里。常见的三种用法：
/// </para>
/// <list type="number">
/// <item><b>停靠面板</b>：<c>host.AddDockPanel("id", "标题", browser)</c>，OBS 会建
/// <c>QDockWidget</c>、挂进主窗口、**并自动加「停靠窗口」菜单项**。</item>
/// <item><b>普通窗口</b>：<see cref="ObsWindow"/> 建一个 <c>Qt::Window</c>，把
/// <see cref="Widget"/> 放进去。</item>
/// <item><b>自己安排</b>：直接拿 <see cref="Widget"/> 塞进任何 Qt 布局。</item>
/// </list>
/// <para>
/// 销毁时只销毁浏览器和它自己的 Widget；已经交给 OBS 的（<c>AddDockById</c>）不要
/// 自己 <see cref="Dispose"/> —— 那种情况下用 <c>host.RemoveDockPanel()</c>。
/// </para>
/// <para>
/// 创建要在后台线程做（等 CEF 冷启动可能几秒，并且会同步切到 Qt 主线程）。
/// </para>
/// </remarks>
public sealed class ObsBrowser : IDisposable
{
    private readonly CefBrowser? _browser;
    private bool _disposed;

    private ObsBrowser(CefBrowser browser)
    {
        _browser = browser;
    }

    /// <summary>
    /// 建一块浏览器。
    /// </summary>
    /// <param name="host">页面所属的 host：地址和 endpoint 都从它推出来。</param>
    /// <param name="url">
    /// 初始地址。为 <see langword="null"/> 时用 <see cref="ObsHost.Url"/>；
    /// 相对地址（比如 <c>"/settings"</c>）会拼在 host 地址后面。
    /// </param>
    /// <remarks>
    /// <b>会阻塞</b>：等 CEF 就绪（冷启动可能几秒）。不要在 OBS 的 Qt 主线程上调它——
    /// 那会把整个界面卡住。要非阻塞就先 <see cref="ObsHost.StartAsync"/> 起来，再从
    /// 后台线程建（或者用 <see cref="ObsHost.AddDockPanelAsync"/> 让它替你安排）。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> 是 null。</exception>
    /// <exception cref="OBSharpNativeException">CEF 不可用或创建失败。</exception>
    public static ObsBrowser Create(ObsHost host, string? url = null)
        => CreateCore(host, url, startupScript: null);

    /// <summary>
    /// 建一块浏览器，<b>不阻塞调用方</b>。
    /// </summary>
    /// <param name="host">页面所属的 host。</param>
    /// <param name="url">初始地址；为 <see langword="null"/> 时用 host 的地址。</param>
    /// <remarks>
    /// 把「等 CEF」挪到线程池上，所以可以安全地在 OBS 的 Qt 主线程里 await 它。
    /// </remarks>
    public static Task<ObsBrowser> CreateAsync(ObsHost host, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Task.Run(() => Create(host, url));
    }

    /// <summary>
    /// 建一块浏览器，并注入一段在页面文档创建前执行的脚本。
    /// </summary>
    /// <param name="host">页面所属的 host。</param>
    /// <param name="url">初始地址；为 null 时用 host 的地址。</param>
    /// <param name="startupScript">每个文档创建前注入的脚本。</param>
    public static ObsBrowser Create(ObsHost host, string? url, string? startupScript)
        => CreateCore(host, url, startupScript);

    private static ObsBrowser CreateCore(ObsHost host, string? url, string? startupScript)
    {
        ArgumentNullException.ThrowIfNull(host);

        OBSharpResult result = OBSharpNative.Initialize(host.PluginName);
        if (result is not OBSharpResult.Ok)
            throw new OBSharpNativeException(result, $"OB# 浏览器不可用：{result}。");

        CefBrowser browser = CefBrowser.Create(new BrowserOptions
        {
            Host = host.Name,
            Url = ResolveUrl(host, url),
            Endpoint = host.Endpoint,
            EndpointKind = OBSharpEndpointKind.Auto,
            StartupScript = startupScript,
        });

        return new ObsBrowser(browser);
    }

    /// <summary>
    /// 底层的 <c>QWidget*</c>。可以交给 <see cref="ObsHost.AddDockPanel"/>，或者塞进
    /// <see cref="ObsWindow.Content"/>。
    /// </summary>
    /// <remarks>
    /// Widget 归本对象所有，<b>不要</b>自己销毁。交给 OBS 之前不要先挂到别的父级上。
    /// </remarks>
    public nint Widget => _browser?.Widget ?? 0;

    /// <summary>
    /// 当前文档地址；浏览器已经释放时是空串。
    /// </summary>
    public string CurrentUrl => _browser?.CurrentUrl ?? string.Empty;

    /// <summary>
    /// 导航到新地址（可以写 <c>obsharp://</c> 相对路径）。
    /// </summary>
    public void Load(string url)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _browser?.Load(url);
    }

    /// <summary>
    /// 重新加载当前页面。
    /// </summary>
    public void Reload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _browser?.Reload();
    }

    /// <summary>
    /// 给页面发一条消息，页面里会收到 <c>CustomEvent("obsharp-message")</c>。
    /// </summary>
    /// <param name="json">合法 JSON 文本。</param>
    public void PostMessage(string json)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _browser?.PostMessage(json);
    }

    /// <summary>
    /// 在页面里执行一段脚本。
    /// </summary>
    public void ExecuteScript(string script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _browser?.ExecuteScript(script);
    }

    /// <summary>
    /// 销毁浏览器和它的 Widget。
    /// </summary>
    /// <remarks>
    /// 已经交给 OBS 的停靠面板<b>不要</b>调这个：那种情况下 Widget 归 OBS，
    /// 用 <see cref="ObsHost.RemoveDockPanel"/> 撤掉面板。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _browser?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[OBSharp.SDK] 销毁浏览器失败：{exception}");
        }
    }

    // 相对地址拼到 host 地址后面；绝对地址（obsharp:// 或 http(s)://）原样用。
    private static string ResolveUrl(ObsHost host, string? url)
    {
        if (string.IsNullOrEmpty(url))
            return host.Url;

        if (url.StartsWith("obsharp://", StringComparison.OrdinalIgnoreCase)
            || url.Contains("://", StringComparison.Ordinal))
        {
            return url;
        }

        return url.StartsWith('/')
            ? host.Url.TrimEnd('/') + url
            : host.Url.TrimEnd('/') + "/" + url;
    }
}
