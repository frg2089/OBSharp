namespace OBSharp.Interop.Browser;

/// <summary>
/// 建一个普通顶层窗口所需的参数。对应原生头文件里的 <c>obsharp_window_options</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这是一个<b>纯 Qt 容器</b>：只建一个 <c>Qt::Window</c> 顶层窗口（普通标题栏、进任务栏、
/// 可单�� Alt+Tab），**不含浏览器**。页面请另外用 <see cref="CefBrowser"/> 建，再把它的
/// <see cref="CefBrowser.Widget"/> 交给 <see cref="CefWindow.Content"/>。
/// </para>
/// <para>
/// 这样拆开的好处：同一个浏览器 Widget 也能交给 OBS 的
/// <c>obs_frontend_add_dock_by_id()</c> 做停靠面板，不必为此再引入一套窗口类型。
/// </para>
/// </remarks>
public sealed class WindowOptions
{
    /// <summary>
    /// 窗口标题。为 <see langword="null"/> 时为空。
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
}
