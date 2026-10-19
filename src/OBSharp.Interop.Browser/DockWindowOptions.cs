namespace OBSharp.Interop.Browser;

/// <summary>
/// 建一个停靠窗口所需的参数。对应原生头文件里的 <c>obsharp_dock_window_options</c>。
/// </summary>
/// <remarks>
/// 对象本身是一个 <c>QDockWidget</c>（内含浏览器 Widget），必须先用
/// <see cref="CefDockWindow.Widget"/> 把它交给 <c>obs_frontend_add_custom_qdock()</c>
/// 挂进 OBS，show / hide 才会正常。
/// </remarks>
public sealed class DockWindowOptions
{
    /// <summary>
    /// OBS 停靠面板 id（也是 <c>QDockWidget</c> 的 objectName）。为
    /// <see langword="null"/> 时用 <see cref="BrowserOptions.Host"/> 推导。
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// 面板标题。为 <see langword="null"/> 时用 host 名，没有 host 时用 URL 的 host。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 创建后是否立刻显示，默认不显示。
    /// </summary>
    public bool Visible { get; init; }

    /// <summary>
    /// 初始宽度，小于等于 0 时用 640。
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// 初始高度，小于等于 0 时用 480。
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// 窗口事件，默认什么都不挂。
    /// </summary>
    public WindowCallbacks Callbacks { get; init; } = new();

    /// <summary>
    /// 浏览器参数；<see cref="BrowserOptions.Url"/> 为 <see langword="null"/> 且给了
    /// <see cref="BrowserOptions.Host"/> 时访问 <c>obsharp://&lt;host&gt;/</c>。
    /// </summary>
    public BrowserOptions Browser { get; init; } = new();
}
