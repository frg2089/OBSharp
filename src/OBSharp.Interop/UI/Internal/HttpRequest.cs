namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 要转给 Kestrel 的请求（已经拆成 HTTP/1.1 需要的样子）。
/// </summary>
/// <remarks>
/// <see cref="Target"/> 是 path + query，<see cref="Host"/> 只用于 <c>Host:</c> 头。
/// 对应旧 <c>obsharp::HttpRequest</c>。
/// </remarks>
internal sealed class HttpRequest
{
    /// <summary>HTTP 方法，默认 <c>GET</c>。</summary>
    public string Method { get; set; } = "GET";

    /// <summary>path + query，默认 <c>/</c>。</summary>
    public string Target { get; set; } = "/";

    /// <summary><c>Host:</c> 头的值；空时用 <c>obsharp</c>。</summary>
    public string Host { get; set; } = "";

    /// <summary>原请求头。发送时会被滤掉 host / connection / accept-encoding / content-length。</summary>
    public List<KeyValuePair<string, string>> Headers { get; } = [];

    /// <summary>请求体原始字节。</summary>
    public byte[] Body { get; set; } = [];
}
