namespace OBSharp.Interop.UI;

/// <summary>
/// 页面加载结束事件参数。
/// </summary>
/// <param name="errorCode"></param>
/// <param name="httpStatusCode"></param>
/// <param name="url"></param>
public class LoadEndEventArgs(int errorCode, int httpStatusCode, string? url) : EventArgs
{
    public int ErrorCode { get; } = errorCode;
    public int HttpStatusCode { get; } = httpStatusCode;
    public string? Url { get; } = url;
}
