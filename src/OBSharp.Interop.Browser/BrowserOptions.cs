namespace OBSharp.Interop.Browser;

/// <summary>
/// 建一个浏览器所需的参数。对应原生头文件里的 <c>obsharp_browser_options</c>，
/// 但这里的字符串直到创建返回前都由托管侧托管，不用调用方操心。
/// </summary>
public sealed class BrowserOptions
{
    /// <summary>
    /// 初始 URL。为 <see langword="null"/> 时用
    /// <c>&lt;scheme&gt;://&lt;domain&gt;/</c>（默认 <c>http://obsharp/</c>）。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// 自定义 scheme handler 用的 scheme，默认 <c>http</c>。obs-browser 已经把它
    /// 注册成 standard + CORS enabled，所以能直接复用，不用自己建 CefApp。
    /// </summary>
    public string? Scheme { get; init; }

    /// <summary>
    /// 自定义 scheme handler 用的 host，默认 <c>obsharp</c>。
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// Kestrel 的监听地址：Windows 是命名管道名（<c>obsharp-xxx</c> 或
    /// <c>\\.\pipe\obsharp-xxx</c>），Unix 是 UDS 路径。
    /// 为 <see langword="null"/> 时不注册 scheme handler，浏览器按普通网络访问。
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
    /// 事件回调，默认什么都不挂。
    /// </summary>
    public BrowserCallbacks Callbacks { get; init; } = new();
}
