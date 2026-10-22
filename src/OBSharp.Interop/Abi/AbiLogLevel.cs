namespace OBSharp.Interop.Abi;

/// <summary>
/// 原生库的日志级别。数值是 libobs 的 blog() 常量（util/base.h），可直接转给宿主。
/// 注意数值不按严重度排序（libobs 历史包袱），永远不要拿来做比较。
/// </summary>
internal enum AbiLogLevel
{
    /// <summary>libobs LOG_ERROR（500）。</summary>
    Error = 500,

    /// <summary>libobs LOG_WARNING（200）。</summary>
    Warning = 200,

    /// <summary>libobs LOG_INFO（300）。</summary>
    Info = 300,

    /// <summary>libobs LOG_DEBUG（400）。</summary>
    Debug = 400,
}
