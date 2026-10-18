namespace OBSharp.Interop.Browser;

/// <summary>
/// 浏览器事件。全部在 Qt 主线程上回调（原生侧会把页面消息切回主线程再派发）。
/// 传进来的字符串都是当次调用有效的副本，想留着就自己存。
/// </summary>
public sealed class BrowserCallbacks
{
    /// <summary>
    /// 页面标题变化。
    /// </summary>
    public Action<string>? TitleChanged { get; init; }

    /// <summary>
    /// 页面 URL 变化。
    /// </summary>
    public Action<string>? UrlChanged { get; init; }

    /// <summary>
    /// 一次主文档加载结束。<c>errorCode</c> 为 0 表示成功。
    /// </summary>
    /// <remarks>
    /// obs-browser 的面板接口没有暴露 CefLoadHandler，这个事件是页面里的引导脚本
    /// 报告 <c>load</c> 事件得到的，因此 <c>httpStatusCode</c> 恒为 200、
    /// <c>errorCode</c> 恒为 0，只在注册了 <c>endpoint</c> 时才有。
    /// </remarks>
    public Action<int, int, string>? LoadEnd { get; init; }

    /// <summary>
    /// 页面的 <c>console.*</c> 消息。同样来自引导脚本，<c>source</c> 是当前文档
    /// URL、<c>line</c> 恒为 0。
    /// </summary>
    public Action<OBSharpConsoleLevel, string, string, int>? ConsoleMessage { get; init; }

    /// <summary>
    /// 页面通过 <c>window.obsharp.post(json)</c> 发来的消息，<c>json</c> 是原文。
    /// </summary>
    public Action<string>? Message { get; init; }
}
