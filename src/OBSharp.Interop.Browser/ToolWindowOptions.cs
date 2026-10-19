namespace OBSharp.Interop.Browser;

/// <summary>
/// 建一个工具窗口所需的参数。对应原生头文件里的 <c>obsharp_tool_window_options</c>。
/// </summary>
/// <remarks>
/// 工具窗口是一个独立的顶层窗口（<c>Qt::Window | Qt::Tool</c>），内含浏览器 Widget。
/// 用户点关闭时只会隐藏窗口并触发 <see cref="WindowCallbacks.CloseRequested"/>，
/// 要真正销毁请调 <see cref="CefToolWindow.Dispose"/>。
/// </remarks>
public sealed class ToolWindowOptions
{
    /// <summary>
    /// 窗口标题。为 <see langword="null"/> 时用 host 名，没有 host 时用 URL 的 host。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 初始宽度，小于等于 0 时用 720。
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// 初始高度，小于等于 0 时用 480。
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// 创建后是否立刻显示，默认不显示。
    /// </summary>
    public bool Visible { get; init; }

    /// <summary>
    /// 是否置顶（<c>Qt::WindowStaysOnTopHint</c>）。
    /// </summary>
    public bool Topmost { get; init; }

    /// <summary>
    /// 是否允许用户缩放窗口，默认允许。
    /// </summary>
    public bool Resizable { get; init; } = true;

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
