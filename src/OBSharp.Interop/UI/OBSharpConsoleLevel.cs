namespace OBSharp.Interop.UI;

/// <summary>
/// 对应原生头文件里的 <c>obsharp_console_level</c>：页面的 console 消息级别。
/// </summary>
public enum OBSharpConsoleLevel
{
    /// <summary>
    /// verbose / debug。
    /// </summary>
    Verbose = 0,

    /// <summary>
    /// log / info。
    /// </summary>
    Info = 1,

    /// <summary>
    /// warn。
    /// </summary>
    Warning = 2,

    /// <summary>
    /// error / assert。
    /// </summary>
    Error = 3,

    /// <summary>
    /// fatal。
    /// </summary>
    Fatal = 4,
}
