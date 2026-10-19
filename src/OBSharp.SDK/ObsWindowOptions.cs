namespace OBSharp;

/// <summary>
/// 建一个插件窗口（<see cref="ObsDockWindow"/> / <see cref="ObsToolWindow"/>）的参数。
/// </summary>
/// <remarks>
/// <para>
/// 一个窗口就是一块 CEF 浏览器加一个 OBS 容器：停靠窗口是 <c>QDockWidget</c>，
/// 工具窗口是独立的顶层窗口。窗口第一次 <see cref="ObsWindow.Show"/> 时才真正创建
/// 浏览器并导航，所以构造窗口对象本身很便宜。
/// </para>
/// <para>
/// <see cref="Host"/> 和 <see cref="Url"/> 至少要有一个：给了 <see cref="Host"/> 时
/// <see cref="Url"/> 默认是 <c>obsharp://&lt;host&gt;/</c>，页面走 host 的 IPC 端点；
/// <see cref="Url"/> 是普通地址（比如 <c>https://example.com/</c>）时可以不给 host
/// （host 与窗口是解耦的，见 README「host 与窗口」）。
/// </para>
/// </remarks>
public sealed class ObsWindowOptions
{
    /// <summary>
    /// 窗口 id：停靠面板在 OBS 里的 id，也是 <c>QDockWidget</c> 的 objectName。
    /// 同一个插件里不要重复。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 窗口标题。
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// 窗口页面所属的 host。为 <see langword="null"/> 时 <see cref="Url"/> 必须是完整的
    /// 普通地址，页面不经过 OB# 的 IPC 端点。
    /// </summary>
    public ObsHost? Host { get; init; }

    /// <summary>
    /// 初始地址。为 <see langword="null"/> 时用 host 的规范地址（<c>obsharp://&lt;host&gt;/</c>）。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// 创建后是否立刻显示，默认不显示（第一次 <see cref="ObsWindow.Show"/> 才创建浏览器）。
    /// </summary>
    public bool ShowOnCreate { get; init; }

    /// <summary>
    /// 初始宽度，默认 720。
    /// </summary>
    public int Width { get; init; } = 720;

    /// <summary>
    /// 初始高度，默认 480。
    /// </summary>
    public int Height { get; init; } = 480;

    /// <summary>
    /// 是否置顶。只对工具窗口有意义（<c>Qt::WindowStaysOnTopHint</c>），停靠窗口忽略它。
    /// </summary>
    public bool Topmost { get; init; }
}
