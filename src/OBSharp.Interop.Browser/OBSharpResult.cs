namespace OBSharp.Interop.Browser;

/// <summary>
/// 对应原生头文件里的 <c>obsharp_result</c>：0 是成功，负数是错误。
/// </summary>
public enum OBSharpResult
{
    /// <summary>
    /// 成功。
    /// </summary>
    Ok = 0,

    /// <summary>
    /// 参数不合法（空指针、size 不匹配……）。
    /// </summary>
    InvalidArgument = -1,

    /// <summary>
    /// 还没调用 <see cref="OBSharpNative.Initialize"/>，或者初始化失败。
    /// </summary>
    NotInitialized = -2,

    /// <summary>
    /// 进程里没有可用的 obs-browser / CEF。
    /// </summary>
    BrowserUnavailable = -3,

    /// <summary>
    /// 在错误的线程上调用了只能在 UI 线程调用的函数。
    /// </summary>
    WrongThread = -4,

    /// <summary>
    /// 命名管道 / UDS 连接失败。
    /// </summary>
    Endpoint = -5,

    /// <summary>
    /// 其它内部错误。
    /// </summary>
    Internal = -6,
}
