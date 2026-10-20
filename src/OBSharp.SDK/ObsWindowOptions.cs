namespace OBSharp;

/// <summary>
/// 建一个 <see cref="ObsWindow"/> 所需的参数。
/// </summary>
/// <remarks>
/// 窗口是<b>纯容器</b>：这里没有 host / URL，页面用 <see cref="ObsBrowser"/> 另外建，
/// 再通过 <see cref="ObsWindow.Content"/> 放进来。
/// </remarks>
public sealed class ObsWindowOptions
{
    /// <summary>
    /// 窗口标题。
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// 初始宽度，小于等于 0 时用 720。
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// 初始高度，小于等于 0 时用 480。
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// 是否置顶（<c>Qt::WindowStaysOnTopHint</c>）。
    /// </summary>
    public bool Topmost { get; init; }

    /// <summary>
    /// 是否允许用户缩放，默认允许。
    /// </summary>
    public bool Resizable { get; init; } = true;

    /// <summary>
    /// 初始化 CEF 时用的日志前缀，默认 "OBSharp"。只是影响日志，容易忽略。
    /// </summary>
    internal string InitializeTag { get; init; } = "OBSharp";
}
