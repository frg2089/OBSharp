namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 端点探测：连上即关。
/// </summary>
/// <remarks>
/// 对应旧 <c>obsharp_browser_native_probe_endpoint</c> 里
/// <c>KestrelConnection::Connect</c> + <c>Close</c> 那两步——探测的是「连得上吗」，
/// 不发请求，所以不会打扰 Kestrel 的请求处理逻辑。
/// </remarks>
internal static class KestrelProbe
{
    /// <summary>
    /// 能不能连上 <paramref name="endpoint"/>。失败返回 <see langword="false"/> 并写
    /// <paramref name="error"/>。
    /// </summary>
    public static bool TryProbe(Endpoint endpoint, out string? error)
    {
        KestrelConnection? connection = KestrelConnection.Connect(endpoint, out error);
        if (connection is null)
            return false;

        connection.Close();
        return true;
    }
}
