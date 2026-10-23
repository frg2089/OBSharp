namespace OBSharp.Interop.UI;

/// <summary>
/// 建一个浏览器所需的参数。对应原生头文件里的 <c>obsharp_browser_options</c>，
/// 但这里的字符串直到创建返回前都由托管侧托管，不用调用方操心。
/// </summary>
public sealed class WebViewOptions
{
    /// <summary>
    /// 初始 URL。为 <see langword="null"/> 时用 <c>obsharp://&lt;host&gt;/</c>（给了
    /// <see cref="Host"/> 的话），否则用 <c>&lt;scheme&gt;://&lt;domain&gt;/</c>
    /// （默认 <c>https://obsharp/</c>）。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// 自定义 scheme handler 用的 scheme，默认 <c>https</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>https</c> 是 CEF / Chromium 的内置 scheme，可以给它注册 scheme handler
    /// factory。原生库的 factory 总是返回 handler，请求被直接转给该 host 的命名管道 /
    /// UDS，<b>不做 TLS</b>、不进网络栈，所以没有加解密开销。
    /// </para>
    /// <para>
    /// 用 <c>https</c> 是为了让页面落在<b>安全上下文</b>里：<c>crypto.subtle</c>、
    /// <c>serviceWorker</c>、<c>navigator.clipboard</c> 这些只在安全上下文开放的 API
    /// 才能用。想退回明文来源（不安全上下文）可以显式传 <c>http</c>。
    /// </para>
    /// </remarks>
    public string? Scheme { get; init; }

    /// <summary>
    /// 自定义 scheme handler 用的 host，默认 <c>obsharp</c>。给了 <see cref="Host"/> 时
    /// 被忽略（原生侧实际注册的是 <c>&lt;host&gt;.obsharp</c>）。
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// Kestrel 的监听地址：Windows 是命名管道名（<c>obsharp-xxx</c> 或
    /// <c>\\.\pipe\obsharp-xxx</c>），Unix 是 UDS 路径。
    /// 为 <see langword="null"/> 且没给 <see cref="Host"/> 时不注册 scheme handler，
    /// 浏览器按普通网络访问。
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// <see cref="Endpoint"/> 的解释方式，默认按平台推断。
    /// </summary>
    public OBSharpEndpointKind EndpointKind { get; init; } = OBSharpEndpointKind.Auto;

    /// <summary>
    /// 每个页面文档创建前注入的脚本，可为 <see langword="null"/>。
    /// </summary>
    public string? StartupScript { get; init; }

    /// <summary>
    /// 是否让 Widget 背景透明。
    /// </summary>
    public bool Transparent { get; init; }

    /// <summary>
    /// <c>0xAARRGGBB</c> 背景色，0 表示不设。尽力而为：页面自己画的背景会盖住它。
    /// </summary>
    public uint BackgroundColor { get; init; }

    /// <summary>
    /// 父窗口。Windows 上按 <c>HWND</c> 解释（内部走 <c>QWidget::find</c>，
    /// 找不到再当成 <c>QWidget*</c>）；其它平台直接当成 <c>QWidget*</c>。
    /// </summary>
    public nint ParentWidget { get; init; }

    /// <summary>
    /// OB# host 名，可为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 给了 host 就表示页面走 <c>obsharp://&lt;host&gt;/</c>：<see cref="Endpoint"/> 为空时
    /// 按平台推导（Windows <c>\\.\pipe\obsharp\&lt;host&gt;</c>，Unix
    /// <c>/tmp/obsharp/&lt;host&gt;.socket</c>），<see cref="Url"/> 为空时用
    /// <c>obsharp://&lt;host&gt;/</c>，且注入的引导脚本里多出
    /// <c>window.obsharp.host</c> / <c>window.obsharp.url</c>。
    /// <para>
    /// URL 以 <c>obsharp://</c> 开头时，其中的 host 必须与这个字段一致，否则原生侧返回
    /// <see cref="OBSharpResult.InvalidArgument"/>。为 <see langword="null"/> 时行为与 ABI v1
    /// 完全一致（<see cref="Url"/> 原样加载）。
    /// </para>
    /// </remarks>
    public string? Host { get; init; }

    /// <summary>
    /// 浏览器就绪后立刻自动弹 DevTools（调试用，与右键菜单「检查」同一条 CEF 通路）。
    /// 是否打开由调用方决定（通常是上层从配置读了再传进来，本层不碰配置 / 环境变量）。
    /// </summary>
    public bool DevTools { get; init; }
}
