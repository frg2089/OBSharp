namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 响应 body 的传输方式（对应旧 <c>HttpResponseHead::Framing</c>）。
/// </summary>
internal enum HttpFraming
{
    /// <summary>
    /// 没有 body（1xx / 204 / 304）。
    /// </summary>
    None,

    /// <summary>
    /// <c>Content-Length</c> 给定长度。
    /// </summary>
    Length,

    /// <summary>
    /// <c>Transfer-Encoding: chunked</c>。
    /// </summary>
    Chunked,

    /// <summary>
    /// 一直读到对端关闭。
    /// </summary>
    UntilClose,
}
