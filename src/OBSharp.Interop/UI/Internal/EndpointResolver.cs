namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 规范化之后的 IPC 端点：地址 + 它该怎么解释。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>obsharp::Endpoint</c>（<c>KestrelClient.h</c>）。
/// 「Kind」在这里已经被解析成具体值（<see cref="OBSharpEndpointKind.Auto"/> 不会出现在
/// 结果里）：连接方（<c>KestrelClient</c> 的替代品）只需要按 kind 分支，不必再判一次平台。
/// </para>
/// <para>
/// <see cref="Address"/> 是<b>已经规范化</b>的地址：命名管道带 <c>\\.\pipe\</c> 前缀，
/// UDS 是文件系统路径。原样拿去开连接即可。
/// </para>
/// </remarks>
internal sealed class Endpoint
{
    /// <summary>
    /// 端点的实际类型（<see cref="OBSharpEndpointKind.NamedPipe"/> 或
    /// <see cref="OBSharpEndpointKind.UnixSocket"/>）。
    /// </summary>
    public OBSharpEndpointKind Kind;

    /// <summary>
    /// 规范化后的地址。
    /// </summary>
    public string Address = "";
}

/// <summary>
/// 把调用方给的 <c>endpoint</c> 字符串解释成可用的端点，以及按 host 推默认端点。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>NormalizeEndpoint</c> / <c>DefaultEndpointForHost</c>
/// （<c>KestrelClient.cpp</c>）。这两个函数是「托管侧 Kestrel 监听什么」和
/// 「浏览器连什么」之间唯一的约定：两边算出来的字符串必须一模一样，否则浏览器连不上，
/// 而且现象是「一直在转圈、没有任何错误」，极难排查。
/// </para>
/// <para>
/// 之所以把规范化放在托管侧而不是让原生侧自己拼：同一个规则既要用在
/// 「注册 scheme handler / 开连接」上，也要用在「打印日志、给出错误」上，
/// 集中一个实现才不会出现两份慢慢漂移的字符串拼接。
/// </para>
/// </remarks>
internal static class EndpointResolver
{
    /// <summary>
    /// 把 <paramref name="endpoint"/> 规范化成一个可用的 <see cref="Endpoint"/>。
    /// </summary>
    /// <param name="endpoint">调用方给的地址；空 / <see langword="null"/> 视为错误。</param>
    /// <param name="kind">
    /// 调用方声明的类型。<see cref="OBSharpEndpointKind.Auto"/> 按平台推断
    /// （Windows = 命名管道，其余 = UDS）。
    /// </param>
    /// <param name="result">规范化结果（失败时是一个占位对象，不要用）。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>规范化成功返回 <see langword="true"/>。</returns>
    public static bool TryNormalize(string? endpoint, OBSharpEndpointKind kind, out Endpoint result, out string? error)
    {
        // 先给 result 一个非 null 的占位：这个 out 参数在失败分支里也必须是确定值，
        // 否则调用方在 `if (!ok) { /* 忘了 return */ }` 时会拿到 null 引用崩掉。
        result = new Endpoint();

        if (string.IsNullOrEmpty(endpoint))
        {
            error = "endpoint is empty";
            return false;
        }

        // Auto 的含义是「按平台选默认的那种」。这个判断必须在托管侧做：原生侧同时跑在
        // 三种平台上，让它猜就等于把平台差异埋进 C++ 的 #ifdef 里，而我们现在正是要
        // 把这些 #ifdef 从 C++ 搬到 C#。
        OBSharpEndpointKind resolved = kind;
        if (resolved == OBSharpEndpointKind.Auto)
            resolved = OperatingSystem.IsWindows() ? OBSharpEndpointKind.NamedPipe : OBSharpEndpointKind.UnixSocket;

        result.Kind = resolved;

        switch (resolved)
        {
            case OBSharpEndpointKind.NamedPipe:
                if (!OperatingSystem.IsWindows())
                {
                    error = "named pipes are only available on Windows";
                    return false;
                }

                // Kestrel 的 ListenNamedPipe("foo") 实际监听的是 \\.\pipe\foo。调用方
                // 有时会把完整路径（\\server\pipe\name 或 //server/pipe/name）直接给我们，
                // 那种就原样用；否则补平台前缀——两边算法必须一致，所以这里刻意保留
                // 「两种前缀都算已带前缀」的宽松判断。
                result.Address = endpoint.StartsWith(@"\\", StringComparison.Ordinal) ||
                                 endpoint.StartsWith("//", StringComparison.Ordinal)
                    ? endpoint
                    : @"\\.\pipe\" + endpoint;
                error = null;
                return true;

            case OBSharpEndpointKind.UnixSocket:
                if (OperatingSystem.IsWindows())
                {
                    error = "unix domain sockets are not available on Windows";
                    return false;
                }

                // UDS 就是文件系统路径，没有需要补的前缀（相对路径也允许——由 Kestrel
                // 自己相对进程当前目录解析，旧行为如此，不改）。
                result.Address = endpoint;
                error = null;
                return true;

            default:
                // 只有三个枚举值，走到这里说明调用方传了个越界的整数（比如从原生结构体
                // 里读出来的脏值）。不抛异常：这是参数错误，跟其它分支一样用错误串表达。
                error = "unknown endpoint kind";
                return false;
        }
    }

    /// <summary>
    /// 按 host 推导默认端点地址（平台默认的那一种）。
    /// </summary>
    /// <param name="host">已经校验过的 host 名（<c>^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$</c>）。</param>
    /// <remarks>
    /// <para>
    /// Windows：<c>\\.\pipe\obsharp\&lt;host&gt;</c> —— 固定前缀 + host 一段，避免用 host 当
    /// 整个管道名（那会跟别的软件的管道撞名字）。
    /// </para>
    /// <para>
    /// Unix：<c>$TMPDIR/obsharp/&lt;host&gt;.socket</c>，<c>TMPDIR</c> 为空时退回 <c>/tmp</c>，
    /// 并且补尾斜杠。这跟托管侧 ObsHost 的 <c>Path.GetTempPath()</c> 是同一套规则
    /// （它也是 TMPDIR 优先、否则 /tmp）——两边对不上，浏览器连的路径和 Kestrel 监听的
    /// 路径就分家了。
    /// </para>
    /// <para>
    /// 注意这里<b>不</b>负责建目录：<c>obsharp/</c> 子目录由监听方（Kestrel 那一侧）创建。
    /// </para>
    /// </remarks>
    public static string DefaultEndpointForHost(string host)
    {
        if (OperatingSystem.IsWindows())
            return @"\\.\pipe\obsharp\" + host;

        string? tmp = Environment.GetEnvironmentVariable("TMPDIR");
        string basePath = string.IsNullOrEmpty(tmp) ? "/tmp" : tmp;

        // 尾斜杠：TMPDIR 通常不带（"/tmp"），但不保证（"/var/tmp/" 也合法），
        // 补之前先判一下，避免拼出 "//"。
        if (basePath.Length > 0 && basePath[^1] != '/')
            basePath += "/";

        return basePath + "obsharp/" + host + ".socket";
    }
}
