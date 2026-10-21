using OBSharp.Interop.UI.Internal;
using OBSharp.Interop.Native;

namespace OBSharp.Interop.UI;

/// <summary>
/// 本程序集的内部基础设施：初始化、线程切换、端点探测。
/// </summary>
/// <remarks>
/// <para>
/// 旧版这里是一堆 <c>obsharp_browser_native_*</c> 的 P/Invoke（<c>version</c> /
/// <c>initialize</c> / <c>is_available</c> / <c>is_ui_thread</c> / <c>invoke_ui</c> /
/// <c>probe_endpoint</c>），原生库里各有对应实现。
/// </para>
/// <para>
/// 新 ABI 把这些拆开了：版本号 / 线程判断 / 主线程封送是<b>通用原语</b>（<see cref="NativeApi"/>），
/// 而「obs-browser 面板在不在」「这个端点连不连得上」属于<b>业务判断</b>，所以搬到了
/// <see cref="OBSharp.Interop.UI.Internal"/> 命名空间下的
/// <see cref="QcefPanel"/> / <see cref="EndpointResolver"/>。
/// 这个类只负责把它们拼回原来那几个入口 —— 对调用方（本程序集里的 <see cref="WebView"/> /
/// <see cref="FilePicker"/>）来说用法一点没变。
/// </para>
/// </remarks>
internal static partial class NativeBridge
{
    /// <summary>
    /// CEF 面板是否已经可以创建 Widget。
    /// </summary>
    /// <remarks>
    /// 注意这<b>不是</b>「<see cref="Initialize"/> 调用过没有」：它的含义一直是「现在能不能
    /// 建 widget」（旧实现在这里问的是 <c>QCef::initialized()</c>）。所以它查的是面板接口
    /// 能不能解析出来 —— 解析不出来时建 widget 一定失败，「不可用」就是正确答案。
    /// </remarks>
    public static bool IsAvailable => QcefPanel.IsAvailable;

    /// <summary>
    /// 原生库的版本串
    /// </summary>
    public static string Version => NativeApi.Version;


    /// <summary>
    /// 当前线程是不是 Qt 主线程。
    /// </summary>
    public static bool IsUIThread => NativeApi.IsUIThread;

    /// <summary>
    /// 初始化：解析 obs-browser 面板接口，并让 obs-browser 自己把 CEF 拉起来。
    /// </summary>
    /// <param name="logTag">日志标签（旧版的参数，保留签名以兼容调用方）。</param>
    /// <exception cref="OBSharpNativeException">宿主里没有可用的 obs-browser / CEF。</exception>
    /// <remarks>
    /// <para>
    /// 跟旧版的差别：以前这一步整段在 C++ 里（解析面板 + <c>init_browser()</c> + 记 UI 线程），
    /// 现在拆成两步 —— <see cref="QcefPanel"/> 负责「在宿主进程里找 obs-browser」，
    /// <c>obsharp_qcef_init_browser()</c> 负责「走 obs-browser 自己的入口把 CEF 拉起来」
    /// （等价于用户打开一次浏览器 dock）。
    /// </para>
    /// <para>
    /// <b>我们不创建 CefApp、不调用 CefInitialize</b>：那个 CefApp 是 obs-browser 的，
    /// 自己再初始化一份就是两个 CEF 实例抢同一个进程，必然崩。obs-browser 内部有原子保护，
    /// 重复调用 <c>init_browser()</c> 无害。
    /// </para>
    /// <para>
    /// 必须在 Qt 主线程上调用（<c>init_browser()</c> 会碰 Qt），所以整段经
    /// <see cref="InvokeUI"/>。初始化本身有副作用（建 QCef 单例），但没有做幂等缓存 ——
    /// <see cref="QcefPanel.Load"/> 已经缓存了解析结果（成功才缓存，失败不缓存，
    /// 因为 obs-browser 可能是宿主启动之后才异步加载进来的），这里每次只是再调一次
    /// <c>init_browser()</c>，而它本来就是幂等的。
    /// </para>
    /// </remarks>
    public static void Initialize(string? logTag = null)
    {
        if (!string.IsNullOrEmpty(logTag))
        {
            // 旧版的 logTag 用来给原生日志加前缀。新原生库没有这个概念（日志直接进 OBS 日志），
            // 所以把标签本身打一行当作标记 —— 比静默忽略这个参数好，调用方还能靠它定位。
            NativeLogSink.WriteInformation("log tag: " + logTag);
        }

        InvokeUI(() =>
        {
            // 解析面板接口。必须在 Qt 主线程上做：obs_browser_create_qcef() 会建一个
            // QCef（C++ 对象，内部有 QObject / 线程亲和性），旧原生库也是在主线程上
            // 解析的（obsharp_browser_create 先要求 UI 线程，再调 initialize）。
            NativeLogSink.WriteDebug("Resolving the obs-browser panel interface");

            QcefPanel? panel = QcefPanel.Load(out string? loadError);
            if (panel is null)
            {
                // 这里用 BrowserUnavailable 而不是 Internal：解析失败意味着「宿主里没有
                // 可用的 obs-browser / CEF」，那是环境问题，不是我们代码的错。
                throw NativeErrors.Create(
                    OBSharpResult.BrowserUnavailable,
                    "Cannot initialize the browser: no usable obs-browser panel interface in this host. " +
                    (loadError is null ? "" : " " + loadError));
            }

            // 一行版本号，跟旧原生库的 obsharp_browser_native_initialize() 打的那行等价。
            // 排查「改了原生库却没生效」时这是第一条要看的：部署目录里可能同时存在两份
            // OBSharp.Interop.Native，加载的不是刚编出来那份时，代码怎么改都不会有效果。
            NativeLogSink.WriteInformation("native library v" + Version);

            NativeLogSink.WriteDebug("Calling QCef::init_browser()");

            if (!NativeMethods.obsharp_qcef_init_browser(panel.Cef))
            {
                throw NativeErrors.Create(
                    OBSharpResult.BrowserUnavailable,
                    "obsharp_qcef_init_browser() failed: obs-browser could not initialize CEF.");
            }

            NativeLogSink.WriteDebug("CEF initialization requested");
        });
    }

    /// <summary>
    /// 在 Qt 主线程上同步执行 <paramref name="action"/>（已经在主线程时直调）。
    /// </summary>
    /// <remarks>
    /// 底下就是 <see cref="NativeApi.InvokeUI"/>：它自己判线程、自己管
    /// <c>GCHandle</c>、把回调里抛的异常搬回调用线程重抛。这里保留一个同名入口，
    /// 是因为本程序集里所有「碰 Qt」的地方都走它，将来换实现只改一处。
    /// </remarks>
    public static void InvokeUI(Action action) => NativeErrors.Run(() => NativeApi.InvokeUI(action));

    /// <summary>
    /// 探测 endpoint 是否可连接。
    /// </summary>
    /// <param name="endpoint">命名管道名 / UDS 路径。</param>
    /// <param name="endpointKind">解释方式，默认按平台推断。</param>
    /// <exception cref="OBSharpNativeException">
    /// 端点串不合法（<see cref="OBSharpResult.InvalidArgument"/>）或连不上
    /// （<see cref="OBSharpResult.Endpoint"/>）。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 语义跟旧原生库的 <c>obsharp_browser_native_probe_endpoint()</c> 一致：连上就立刻关掉，
    /// 不发任何请求 —— 目的只是回答「这个端点现在有 Kestrel 在听吗」。
    /// </para>
    /// <para>
    /// 走 <see cref="EndpointResolver"/> 而不是把地址原样丢给 socket：调用方给的
    /// <c>endpoint</c> 可能是裸管道名（<c>obsharp-foo</c>），而真正能连的是
    /// <c>\\.\pipe\obsharp-foo</c>。跟 WebView 注册 scheme handler 时用的是同一套规范化，
    /// 所以「探测成功」就意味着「浏览器也能连上」。
    /// </para>
    /// <para>
    /// 「连上即关」这一步在 <see cref="KestrelProbe.TryProbe"/> 里（它对应旧原生库
    /// <c>probe_endpoint</c> 的 <c>KestrelConnection::Connect</c> + <c>Close</c> 两步）。
    /// </para>
    /// </remarks>
    public static void ProbeEndpoint(string endpoint, OBSharpEndpointKind endpointKind = OBSharpEndpointKind.Auto)
    {
        if (!EndpointResolver.TryNormalize(endpoint, endpointKind, out Endpoint normalized, out string? error))
        {
            throw NativeErrors.Create(
                OBSharpResult.InvalidArgument,
                "Invalid endpoint \"" + endpoint + "\": " + (error ?? "unknown error"));
        }

        if (!KestrelProbe.TryProbe(normalized, out string? probeError))
        {
            throw NativeErrors.Create(
                OBSharpResult.Endpoint,
                "Could not connect to endpoint \"" + normalized.Address + "\": " + (probeError ?? "unknown error"));
        }
    }
}
