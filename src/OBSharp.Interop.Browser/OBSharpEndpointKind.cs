namespace OBSharp.Interop.Browser;

/// <summary>
/// 对应原生头文件里的 <c>obsharp_endpoint_kind</c>：<c>endpoint</c> 该怎么解释。
/// </summary>
public enum OBSharpEndpointKind
{
    /// <summary>
    /// 按平台推断：Windows = 命名管道，其余 = UDS。
    /// </summary>
    Auto = 0,

    /// <summary>
    /// 命名管道（只支持 Windows）。
    /// </summary>
    NamedPipe = 1,

    /// <summary>
    /// Unix domain socket（只支持非 Windows）。
    /// </summary>
    UnixSocket = 2,
}
