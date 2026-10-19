namespace OBSharp.Interop.Browser;

/// <summary>
/// 窗口事件。全部在 Qt 主线程上回调。
/// </summary>
public sealed class WindowCallbacks
{
    /// <summary>
    /// 用户点了关闭（只有工具窗口会触发）：窗口已经被隐藏，不会自动销毁。
    /// 停靠窗口不用它（关不关由 OBS 的 dock 决定）。
    /// </summary>
    public Action? CloseRequested { get; init; }
}
