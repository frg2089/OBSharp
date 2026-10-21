using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using OBSharp.Interop.UI.Internal;
using OBSharp.Interop.Native;

namespace OBSharp.Interop.UI;

/// <summary>
/// 一个内嵌的浏览器：Cef 面板 + 它自己的 Qt Widget + 页面事件。
/// </summary>
/// <remarks>
/// <para>
/// 这一版是<b>纯托管编排</b>：旧版的 <c>BrowserWidget</c>（C++）把 host 解析、URL 映射、
/// 引导脚本拼装、scheme 注册、关闭事件守卫全做了，现在那些逻辑分别在
/// <see cref="BrowserHostConfigResolver"/> / <see cref="UrlMapping"/> /
/// <see cref="BootstrapScript"/> / <see cref="SchemeHandlerBridge"/> 里，
/// 这个类只负责「按正确的顺序调用它们」，以及把原生回调翻译成 .NET 事件。
/// </para>
/// <para>
/// 线程：所有碰 Qt 的调用都经 <see cref="NativeApi.InvokeUI"/>（已经在 Qt 主线程时是直调），
/// 调用方不用自己判线程。
/// </para>
/// <para>
/// 所有权：widget 由 <c>obsharp_qcef_create_widget()</c> 创建、归本类释放；但如果它已经被
/// OBS 收养（<c>obs_frontend_add_dock_by_id</c> 会把它塞进 <c>QDockWidget</c> 并 reparent），
/// 就<b>不能</b>删 —— 详见 <see cref="Dispose(bool)"/> 里的注释。
/// </para>
/// </remarks>
public partial class WebView : IDisposable
{
    /// <summary>
    /// QEvent::Type 的数值，来自 Qt6 的 <c>qcoreevent.h</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 数值必须写死而不是从原生侧取：新 ABI 的 <c>obsharp_filter_event_fn</c> 直接把
    /// <c>QEvent::Type</c> 的整数值转发过来，「怎么解释它」按设计属于 C# 的判断。
    /// 写死的同时把出处记在这里，将来 Qt 升大版本时第一个该看的就是这里。
    /// </para>
    /// <para>
    /// <see cref="Show"/> = 17、<see cref="Close"/> = 19：Qt6 <c>qcoreevent.h</c> 里
    /// <c>QEvent::Show = 17</c>、<c>QEvent::Close = 19</c>（中间夹着 Create=15 / Destroy=16）。
    /// </para>
    /// </remarks>
    private static class QtEventType
    {
        /// <summary><c>QEvent::Show</c>（widget 被显示）。</summary>
        public const int Show = 17;

        /// <summary><c>QEvent::Close</c>（请求关闭 widget）。</summary>
        public const int Close = 19;

        /// <summary>
        /// OBS 用来通知停靠面板「被关了」的自定义事件类型。
        /// </summary>
        /// <remarks>
        /// 它是 <c>QEvent::User + QEvent::Close</c> = 1000 + 19 = 1019，见
        /// OBS 的 <c>OBSDock::closeEvent</c>：
        /// <code>
        /// QEvent widgetEvent(QEvent::Type(QEvent::User + QEvent::Close));   // 1019，不是 19
        /// qApp->sendEvent(widget(), &amp;widgetEvent);
        /// </code>
        /// </remarks>
        public const int ObsDockClose = 1019;
    }

    private readonly ILogger _logger;

    /// <summary>
    /// 传给原生回调的上下文：<see cref="GCHandle"/> 的整数形式。
    /// </summary>
    /// <remarks>
    /// 原生侧只原样透传、不管生命周期；本类在 <see cref="Dispose(bool)"/> 里先断开所有回调
    /// 再 <c>Free()</c>，所以回调不会拿着已释放的句柄跑。
    /// </remarks>
    private GCHandle _handle;

    /// <summary>
    /// 底层 <c>QCefWidget*</c>；建失败或已释放时是 0。
    /// </summary>
    /// <remarks>
    /// 注意这<b>不再是</b>旧版那个不透明的 <c>obsharp_browser*</c>：新 ABI 里没有浏览器对象了，
    /// 就是 CEF 的 widget 本身（<c>obsharp_qcef_create_widget</c> 的返回值）。
    /// </remarks>
    private nint _widget;

    /// <summary>
    /// 关闭事件过滤器的 token（<c>obsharp_widget_install_event_filter</c> 的返回值）。
    /// </summary>
    private nint _closeGuard;

    /// <summary>
    /// 本浏览器绑定的 host / scheme / domain 与端点（解析结果）。
    /// </summary>
    /// <remarks>
    /// 各方法都要用（Load 映射 URL、SetStartupScript 重新拼引导脚本、日志），所以留一份。
    /// 旧版这些字段在 C++ 的 <c>BrowserWidget</c> 里。
    /// </remarks>
    private readonly BrowserHostConfig _config;

    /// <summary>
    /// 当前文档 URL（本地跟踪）。
    /// </summary>
    /// <remarks>
    /// 旧版是问原生 <c>obsharp_browser_current_url()</c>；新 ABI 的转发层刻意不缓存状态
    /// （它只做一行式转发），所以「最后一次交给 CEF 的 URL」只能由这里记。
    /// 语义跟旧版一致：还没有任何导航时是空串，另外 <see cref="OnUrlChanged"/> 会持续同步它。
    /// </remarks>
    private string _currentUrl = "";

    private bool _disposedValue;

    protected ILogger Logger => _logger;

    /// <summary>
    /// 底层 <c>QCefWidget*</c>。已释放时是 0。
    /// </summary>
    /// <remarks>
    /// 保留了这个属性以兼容旧用法，但含义变了：以前它是 <c>obsharp_browser*</c> 句柄。
    /// 下游（PluginManager / SDK / Runtime）都没有用它，所以改含义不影响任何人；
    /// 真正的用途是排查时打印指针。要操作 widget 请用 <see cref="Widget"/>。
    /// </remarks>
    public nint Handle => _widget;

    /// <summary>
    /// 底层 <c>QWidget*</c>。可以直接 <c>setParent</c> / 放进 dock，但不要自己销毁。
    /// </summary>
    public nint Widget => _widget;

    /// <summary>
    /// 当前文档 URL；还没加载时是空串。
    /// </summary>
    public string CurrentUrl => _currentUrl;

    /// <summary>
    /// 页面标题变化。
    /// </summary>
    public event EventHandler<TitleChangedEventArgs>? TitleChanged;

    /// <summary>
    /// 页面 URL 变化。
    /// </summary>
    public event EventHandler<UrlChangedEventArgs>? UrlChanged;

    /// <summary>
    /// 一次主文档加载结束。<see cref="LoadEndEventArgs.ErrorCode"/> 为 0 表示成功。
    /// </summary>
    /// <remarks>
    /// obs-browser 的面板接口没有暴露 CefLoadHandler，这个事件是页面里的引导脚本
    /// 报告 <c>load</c> 事件得到的，因此 <see cref="LoadEndEventArgs.HttpStatusCode"/> 恒为 200、
    /// <see cref="LoadEndEventArgs.ErrorCode"/> 恒为 0，只在注册了 <c>endpoint</c> 时才有。
    /// </remarks>
    public event EventHandler<LoadEndEventArgs>? LoadEnd;

    /// <summary>
    /// 页面的 <c>console.*</c> 消息。同样来自引导脚本，<c>source</c> 是当前文档
    /// URL、<c>line</c> 恒为 0。
    /// </summary>
    public event EventHandler<ConsoleMessageEventArgs>? ConsoleMessage;

    /// <summary>
    /// 页面通过 <c>window.obsharp.post(json)</c> 发来的消息，<c>json</c> 是原文。
    /// </summary>
    public event EventHandler<MessageEventArgs>? Message;

    /// <summary>
    /// 创建浏览器。失败时抛 <see cref="OBSharpNativeException"/>。
    /// </summary>
    /// <param name="options">创建参数。</param>
    /// <param name="logger">日志记录器。</param>
    public unsafe WebView(WebViewOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        // 原生库的日志走托管侧注入的回调（它不自己找 libobs 的 blog()）。这里是最早
        // 能拿到宿主 ILogger 的地方，所以在这一刻把它接上。
        NativeLogSink.Install(logger);

        // 1) 把调用方参数解析成「host / scheme / domain / 端点 / 初始 URL」。
        //
        // 这一步在旧版是 C++ 的 ResolveBrowserHostConfig()。所有校验（host 名合法性、
        // url host 与 options.Host 是否一致、端点怎么推）都在那里，失败就是参数错误。
        if (!BrowserHostConfigResolver.TryResolve(
                options.Url, options.Scheme, options.Domain, options.Endpoint,
                options.EndpointKind, options.Host,
                out BrowserHostConfig config, out string? resolveError))
        {
            throw NativeErrors.Create(
                OBSharpResult.InvalidArgument,
                "Invalid browser options: " + (resolveError ?? "unknown error"));
        }

        _config = config;
        _currentUrl = config.Url;

        // 2) 初始化 CEF（解析 obs-browser 面板 + init_browser）。
        //
        // 旧版是 obsharp_browser_create() 内部顺带做的；现在显式一步。失败抛
        // BrowserUnavailable —— 环境问题，跟参数错要分开，调用方的处理方式不一样。
        NativeBridge.Initialize();

        QcefPanel? panel = QcefPanel.Load(out string? panelError);
        if (panel is null)
        {
            throw NativeErrors.Create(
                OBSharpResult.BrowserUnavailable,
                "No usable obs-browser panel interface: " + (panelError ?? "unknown error"));
        }

        // 3) 有端点就注册 scheme handler，并把「页面消息往哪送」挂到本实例上。
        //
        // 为什么必须先注册再建 widget：create_widget 之后 CEF 会立刻发起主文档请求，
        // 那时 factory 必须已经在注册表里，否则首个请求走网络栈 —— 现象是空白页，
        // 而且没有任何错误日志（请求根本没到我们这里）。
        if (config.HasEndpoint && config.Endpoint is not null)
        {
            if (!SchemeHandlerBridge.EnsureInstalled(
                    config.Scheme, config.Domain, config.Endpoint, out string? schemeError))
            {
                throw NativeErrors.Create(
                    OBSharpResult.Internal,
                    "Failed to register the scheme handler (" + config.Scheme + "://" + config.Domain + "): " +
                    (schemeError ?? "unknown error"));
            }

            // sink 收到的是一条**未经解析**的原文 JSON（在 CEF 的 IO 线程上被调用）。
            // 解析 / 分流交给 BridgeDispatcher，见 OnBridgeMessage。
            SchemeHandlerBridge.SetMessageSink(config.Domain, OnBridgeMessage);
        }

        // 4) 在 Qt 主线程上把 widget 建起来并摆好。
        //
        // 引导脚本的注入条件：只有注册了 scheme handler 才注入 —— 脚本里的
        // fetch('/__obsharp/message') 得有 handler 接，没有端点时注入它等于让页面
        // 往一个不存在的地址发请求（控制台一片 404，且 console 补丁本身也在脚本里，
        // 连错误都看不到）。
        string userScript = options.StartupScript ?? "";
        string startupScript = config.HasEndpoint
            ? BootstrapScript.Build(userScript, config.Host)
            : userScript;

        nint widget = 0;
        nint guard = 0;

        // 先分配回调上下文：widget 一建好就会有 titleChanged 信号可能触发，
        // 句柄必须在那之前就绪。
        _handle = GCHandle.Alloc(this);

        try
        {
            NativeErrors.RunUI(() =>
            {
                nint parent = ParentResolver.Resolve(options.ParentWidget);

                // 等 CEF 就绪再建 widget。旧版是在 obsharp_browser_create() 里等
                // （WaitForCefReady，15 秒上限）；这一步不能省：init_browser() 只是
                // 「开始初始化」，CEF 冷启动通常要 1~3 秒，没就绪就 create_widget
                // 拿到的是空指针。
                bool ready = NativeMethods.obsharp_qcef_wait_for_init(panel.Cef);
                if (!ready)
                {
                    throw NativeErrors.Create(
                        OBSharpResult.BrowserUnavailable,
                        "Timed out waiting for obs-browser to finish initializing CEF.");
                }

                // title / url 回调在创建这一刻一次性传入：新 ABI 不再提供事后再改的
                // setter，所以结构体必须先准备好。
                NativeTypes.CefWidgetCallbacks callbacks = new()
                {
                    User = GCHandle.ToIntPtr(_handle),
                    OnTitleChanged = &OnTitleChanged,
                    OnUrlChanged = &OnUrlChanged,
                };

                widget = NativeMethods.obsharp_qcef_create_widget(panel.Cef, parent, config.Url, 0, in callbacks);
                if (widget is 0)
                {
                    throw NativeErrors.Create(
                        OBSharpResult.Internal,
                        "obsharp_qcef_create_widget() returned a null handle: QCef::create_widget() failed.");
                }

                // 装关闭事件守卫，必须在做其它事之前：OBS 的 dock 可能在任何时候发
                // 1019 关闭事件（见 QtEventType.ObsDockClose 的说明），先装上才不会漏。
                guard = NativeMethods.obsharp_widget_install_event_filter(widget, &OnFilterEvent, GCHandle.ToIntPtr(_handle));

                // 透明 / 底色二选一：旧 BrowserWidget::Create() 里就是 if/else if，
                // 不是两个都设（setAutoFillBackground(false) 之后又设 palette 是自相矛盾的）。
                if (options.Transparent)
                {
                    NativeMethods.obsharp_widget_set_translucent_background(widget);
                }
                else if (options.BackgroundColor is not 0)
                {
                    // 尽力而为：给 Widget 铺个底色，页面自己画的内容会盖在上面。
                    // 0 表示「不设」，不是「设成透明黑」。
                    NativeMethods.obsharp_widget_set_background_color(widget, options.BackgroundColor);
                }

                // 空脚本不设：set_startup_script(null) 在原生侧是「清空」，
                // 调它等于把 QCefWidget 自己的默认脚本也清掉，没必要。
                if (!string.IsNullOrEmpty(startupScript))
                    NativeMethods.obsharp_cef_widget_set_startup_script(widget, startupScript);


                // 排查「页面停在 about:blank」时最先要看的一条：它直接给出交给 CEF 的 URL
                // 和 scheme / domain / host 的组合。
                NativeLogSink.Write(
                    NativeLogLevel.Info,
                    "browser widget created: url=" + config.Url +
                    " scheme=" + config.Scheme +
                    " domain=" + config.Domain +
                    " host=" + (config.Host.Length == 0 ? "(none)" : config.Host) +
                    " endpoint=" + (config.HasEndpoint && config.Endpoint is not null
                        ? config.Endpoint.Address
                        : "(none)"));
            });
        }
        catch
        {
            // 构造失败：把已经建出来的东西收掉，别漏 widget / 句柄。
            // 注意 widget 可能已经有父级（OBS 的 dock 抢先收养是可能的），所以复用
            // 析构那条判据（ReleaseWidget 内部按 parent 决定删不删），而不是无条件 deleteLater。
            // guard 为 0 也没关系：ReleaseWidget 只在非 0 时撤过滤器。
            if (widget is not 0)
            {
                try
                {
                    ReleaseWidget(widget, guard);
                }
                catch (Exception cleanupError) when (cleanupError is NativeException or OBSharpNativeException)
                {
                    // 收尾失败不该盖住真正的构造失败原因。
                    _logger.LogDebug(cleanupError, "Cleanup after a failed construction also failed.");
                }
            }

            if (_handle.IsAllocated)
                _handle.Free();

            _widget = 0;
            _closeGuard = 0;
            throw;
        }

        _widget = widget;
        _closeGuard = guard;
    }

    /// <summary>
    /// 导航到 <paramref name="url"/>。
    /// </summary>
    /// <remarks>
    /// <c>obsharp://&lt;host&gt;/...</c> 会先映射成 CEF 实际加载的
    /// <c>&lt;scheme&gt;://&lt;host&gt;.obsharp/...</c>；映射失败（host 对不上 / URL 本身坏了）
    /// 返回 <see cref="OBSharpResult.InvalidArgument"/> 对应的负值 —— 这跟旧版
    /// <c>obsharp_browser_load()</c> 返回 <c>OBSHARP_ERR_INVALID_ARGUMENT</c> 是同一件事，
    /// 只是失败原因现在写得清楚（也能从日志里看到）。
    /// </remarks>
    public int Load(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return Fail(OBSharpResult.InvalidArgument, "The URL must not be empty.");

        if (_widget is 0)
            return Fail(OBSharpResult.InvalidArgument, "The browser has already been disposed.");

        if (!UrlMapping.TryMapObsharpUrlForBrowser(_config.Host, url, _config.Scheme, out string mapped, out string? error))
            return Fail(OBSharpResult.InvalidArgument, error ?? ("Failed to map the URL: " + url));

        return Forward(() =>
        {
            NativeMethods.obsharp_cef_widget_set_url(_widget, mapped);
            _currentUrl = mapped;
        });
    }

    /// <summary>
    /// 忽略缓存重新加载。
    /// </summary>
    public int Reload() => Forward(() => NativeMethods.obsharp_cef_widget_reload(_widget));

    /// <summary>
    /// 缩放：小于 0 缩小，大于 0 放大，等于 0 复位。
    /// </summary>
    /// <remarks>
    /// 原生 <c>obsharp_cef_widget_zoom()</c> 返回 1 = 成功、0 = 失败（比如浏览器还没建好）。
    /// 旧版 <c>obsharp_browser_zoom()</c> 是「帧到成功就返回 OK」，所以这里把 1 折成 0（Ok）、
    /// 0 折成 <see cref="OBSharpResult.Internal"/>，保持调用方看到的值域不变。
    /// </remarks>
    public int Zoom(int direction)
    {
        if (_widget is 0)
            return Fail(OBSharpResult.InvalidArgument, "The browser has already been disposed.");

        bool ok = false;
        int result = Forward(() => ok = NativeMethods.obsharp_cef_widget_zoom(_widget, direction));
        if (result is not 0)
            return result;

        return ok ? 0 : (int)OBSharpResult.Internal;
    }

    /// <summary>
    /// 在当前文档里执行一段 JavaScript。
    /// </summary>
    public int ExecuteScript(string? script)
    {
        if (string.IsNullOrEmpty(script))
            return Fail(OBSharpResult.InvalidArgument, "The script must not be empty.");

        return Forward(() => NativeMethods.obsharp_cef_widget_execute_script(_widget, script));
    }

    /// <summary>
    /// 设置每个页面文档创建前注入的脚本。注意这会整体替换原生侧拼好的引导脚本
    /// （<c>window.obsharp</c>），所以一般用 <see cref="WebViewOptions.StartupScript"/>。
    /// </summary>
    /// <remarks>
    /// 跟旧版有一处行为差异（有意为之）：旧版 <c>obsharp_browser_set_startup_script()</c>
    /// 是裸转发，会把引导脚本一起冲掉；既然引导脚本现在归托管侧拼，这里就按
    /// 「用户脚本」的语义处理 —— 有端点时仍然把引导脚本拼在前面。
    /// 这样调用方写 <c>SetStartupScript("window.foo = 1")</c> 不会把
    /// <c>window.obsharp.post</c> 弄没（那是旧版最容易踩的坑）。
    /// </remarks>
    public int SetStartupScript(string? script)
    {
        string userScript = script ?? "";
        string startupScript = _config.HasEndpoint
            ? BootstrapScript.Build(userScript, _config.Host)
            : userScript;

        return Forward(() => NativeMethods.obsharp_cef_widget_set_startup_script(_widget, startupScript));
    }

    /// <summary>
    /// 是否允许弹出窗口。
    /// </summary>
    public int AllowPopups(bool allow)
        => Forward(() => NativeMethods.obsharp_cef_widget_allow_all_popups(_widget, allow));

    /// <summary>
    /// 调整 Widget 大小。
    /// </summary>
    public int Resize(int width, int height) => Forward(() => NativeMethods.obsharp_widget_resize(_widget, width, height));

    /// <summary>
    /// 移动 Widget。
    /// </summary>
    public int Move(int x, int y) => Forward(() => NativeMethods.obsharp_widget_move(_widget, x, y));

    /// <summary>
    /// 显示 / 隐藏。第一次显示会真正创建 CEF 浏览器。
    /// </summary>
    public int SetVisible(bool visible)
        => Forward(() => NativeMethods.obsharp_widget_set_visible(_widget, visible));

    /// <summary>
    /// 给页面发一条消息：页面里会派发 <c>CustomEvent("obsharp-message")</c>，
    /// <c>detail</c> 是解析后的 JSON。
    /// </summary>
    /// <remarks>
    /// 实现方式跟旧 <c>BrowserWidget::PostToPage()</c> 一字不差：拼一段 executeJavaScript
    /// 去 dispatchEvent。空 json 传成字面量 <c>null</c>（否则拼出来的 JS 是
    /// <c>detail: ()</c>，语法错误，整段脚本不执行）。
    /// </remarks>
    public int PostMessage(string? json)
    {
        if (_widget is 0)
            return Fail(OBSharpResult.InvalidArgument, "The browser has already been disposed.");

        // 页面可能还没加载完：这时 executeJavaScript 是空操作（CEF 自己会丢弃），
        // 不报错 —— 旧版行为一致。
        string script = "window.dispatchEvent(new CustomEvent('obsharp-message', { detail: (" +
                        (string.IsNullOrEmpty(json) ? "null" : json) +
                        ") }));";

        return Forward(() => NativeMethods.obsharp_cef_widget_execute_script(_widget, script));
    }

    #region 事件转发

    /// <summary>
    /// 待派发的页面消息队列（先进先出，保证 console 与 load-end 的先后顺序）。
    /// </summary>
    /// <remarks>
    /// 见 <see cref="OnBridgeMessage"/> 里「为什么不能直接 InvokeUI」的说明：
    /// 消息是从 CEF 的 IO 线程来的，要**异步**转到 Qt 主线程，而且不能乱序。
    /// </remarks>
    private readonly Queue<string> _pendingMessages = new();

    /// <summary>
    /// 是否已经有一个泵线程在跑（保护 <see cref="_pendingMessages"/> 和它自己的状态）。
    /// </summary>
    private bool _draining;

    /// <summary>
    /// 页面消息的统一入口：<see cref="SchemeHandlerBridge"/> 的 sink。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>线程</b>：这个方法在 CEF 的 <b>IO 线程</b>上被调用，不是 Qt 主线程。而事件订阅方
    /// （WPF / Blazor 组件等）是按「事件都在 UI 线程上触发」写的，所以要转到 Qt 主线程再派发。
    /// </para>
    /// <para>
    /// <b>为什么不用 <see cref="NativeApi.InvokeUI"/> 直接转</b>：它是
    /// <c>Qt::BlockingQueuedConnection</c>，会<b>阻塞调用线程</b>直到 Qt 主线程跑完那段代码。
    /// 在 CEF 的 IO 线程上阻塞是危险的 —— Qt 主线程此刻完全可能正卡在一个要等 CEF 的操作里
    /// （<c>create_widget</c> / <c>wait_for_init</c> 的等待循环），两边互等就是死锁。
    /// 旧 C++ 实现也是刻意的异步投递（<c>PostToQtThread</c> 用 <c>Qt::QueuedConnection</c>）。
    /// </para>
    /// <para>
    /// 新 ABI 只提供阻塞版的 <c>obsharp_invoke_ui</c>，没有「异步投递」原语，所以这里先
    /// 把消息塞进队列，再让<b>线程池</b>线程去调那个阻塞版：CEF 的 IO 线程立刻返回，
    /// Qt 主线程也不用等谁。队列 + 单个泵线程保证顺序不乱（console 和 load-end 的先后
    /// 对排查问题有意义，不能靠线程池的调度顺序）。
    /// </para>
    /// <para>
    /// <b>生命周期</b>：消息可能在 WebView 已经 Dispose 之后才到（页面发请求 → 我们销毁 →
    /// CEF 才把请求递上来）。所以泵里封送到主线程之后要再检查一次「我还活着没」。
    /// </para>
    /// <para>
    /// <b>分流</b>：带 <c>$obsharp</c> 键的是引导脚本自己的控制消息（console / load-end），
    /// 由 <see cref="BridgeDispatcher"/> 解析并派发，<b>不</b>外泄给 <see cref="Message"/>；
    /// 其余原样当业务消息。
    /// </para>
    /// </remarks>
    private void OnBridgeMessage(string json)
    {
        lock (_pendingMessages)
        {
            _pendingMessages.Enqueue(json);

            // 已经有一个泵在跑：它会把刚入队的那条取走。
            if (_draining)
                return;

            _draining = true;
        }

        // 先离开 CEF 的 IO 线程再说话。放到线程池而不是直接投 Qt：见上面的死锁说明。
        ThreadPool.QueueUserWorkItem(static state => ((WebView)state!).DrainMessages(), this);
    }

    /// <summary>
    /// 把队列里的消息按顺序转到 Qt 主线程派发，直到队列空。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="OnBridgeMessage"/> 启动，在线程池线程上跑。<b>不能</b>抛异常：
    /// 线程池上的未处理异常会终结整个进程。
    /// </remarks>
    private void DrainMessages()
    {
        while (true)
        {
            string json;
            lock (_pendingMessages)
            {
                if (_pendingMessages.Count is 0)
                {
                    // 队列空了才复位标志。顺序很重要：必须在锁里复位，
                    // 否则会漏掉「复位前一刻刚入队」的那条消息（永远没人派发）。
                    _draining = false;
                    return;
                }

                json = _pendingMessages.Dequeue();
            }

            try
            {
                NativeApi.InvokeUI(() =>
                {
                    if (_disposedValue)
                        return;

                    DispatchJson(json);
                });
            }
            catch (Exception exception) when (exception is NativeException or OBSharpNativeException)
            {
                // Qt 已经退出时投递会失败。消息丢了就丢了（进程正在收尾），
                // 但绝不能让异常穿过 CEF 的回调栈 —— 那是未定义行为。
                _logger.LogDebug(exception, "Could not marshal a page message to the Qt main thread; the message was dropped.");
            }
        }
    }

    /// <summary>
    /// 记一条失败日志并按 <see cref="OBSharpResult"/> 返回错误码。
    /// </summary>
    private int Fail(OBSharpResult code, string message)
    {
        _logger.LogError("{Message}", message);
        return (int)code;
    }

    /// <summary>
    /// 解析一条 bridge 消息并派发成 .NET 事件。必须在 Qt 主线程上调用。
    /// </summary>
    private void DispatchJson(string json)
    {
        bool control = BridgeDispatcher.TryDispatch(
            json,
            info => RaiseConsoleMessage(info),
            (statusCode, errorCode, errorText) =>
                RaiseSafely(() => LoadEnd?.Invoke(this, new LoadEndEventArgs(errorCode, statusCode, errorText))));

        // 控制消息已经被消费（包括我们不认识的 kind）：不外泄给 Message。
        if (!control)
            RaiseSafely(() => Message?.Invoke(this, new MessageEventArgs(json)));
    }

    /// <summary>
    /// 把一条 console 控制消息变成 <see cref="ConsoleMessage"/> 事件（外加一行日志）。
    /// </summary>
    /// <remarks>
    /// 日志映射沿用旧版：<c>0=Verbose → Debug</c>、<c>1=Info → Information</c>、
    /// <c>2=Warning → Warning</c>、<c>3=Error → Error</c>、<c>4=Fatal → Critical</c>。
    /// 先判 <c>IsEnabled</c> 再拼字符串：页面 console 可能非常密，不判会一直白拼。
    /// </remarks>
    private void RaiseConsoleMessage(ConsoleMessageInfo info)
    {
        var level = (OBSharpConsoleLevel)info.Level;
        LogLevel logLevel = level switch
        {
            OBSharpConsoleLevel.Verbose => LogLevel.Debug,
            OBSharpConsoleLevel.Info => LogLevel.Information,
            OBSharpConsoleLevel.Warning => LogLevel.Warning,
            OBSharpConsoleLevel.Error => LogLevel.Error,
            OBSharpConsoleLevel.Fatal => LogLevel.Critical,
            _ => LogLevel.None,
        };

        if (logLevel is not LogLevel.None && _logger.IsEnabled(logLevel))
            _logger.Log(logLevel, "{Level}: {Message} (Source: {Source}, Line: {Line})", level, info.Message, info.Source, info.Line);

        RaiseSafely(() => ConsoleMessage?.Invoke(this, new ConsoleMessageEventArgs(level, info.Message, info.Source, info.Line)));
    }

    /// <summary>
    /// 执行一次事件派发并吞掉订阅方的异常。
    /// </summary>
    /// <remarks>
    /// 必须吞：这些事件是在 CEF / Qt 的回调栈上触发的，异常穿过原生栈是未定义行为
    /// （通常是进程直接崩，而且崩点看起来跟订阅方的代码毫无关系）。记录之后继续 ——
    /// 一个订阅方出错不该让其它订阅方收不到事件。
    /// </remarks>
    private void RaiseSafely(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception exception)
        {
            LogException(_logger, exception);
        }
    }

    /// <summary>
    /// 标题变化回调（Qt 主线程，来自 <c>QCefWidget::titleChanged</c>）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnTitleChanged(nint handle, byte* title)
    {
        if (GCHandle.FromIntPtr(handle).Target is not WebView browser)
            return;

        string? value = Marshal.PtrToStringUTF8((nint)title);
        browser.RaiseSafely(() => browser.TitleChanged?.Invoke(browser, new TitleChangedEventArgs(value)));
    }

    /// <summary>
    /// URL 变化回调（Qt 主线程，来自 <c>QCefWidget::urlChanged</c>）。
    /// </summary>
    /// <remarks>
    /// 顺手同步 <see cref="CurrentUrl"/>：CEF 自己也会改 URL（页面内跳转、
    /// <c>location.href = ...</c>、重定向），只靠 <see cref="Load"/> 里记的那份
    /// 会跟实际文档脱节。
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnUrlChanged(nint handle, byte* url)
    {
        if (GCHandle.FromIntPtr(handle).Target is not WebView browser)
            return;

        string? value = Marshal.PtrToStringUTF8((nint)url);
        browser._currentUrl = value ?? "";
        browser.RaiseSafely(() => browser.UrlChanged?.Invoke(browser, new UrlChangedEventArgs(value)));
    }

    /// <summary>
    /// 事件过滤器：拦关闭事件、显示时踢一下重绘。
    /// </summary>
    /// <returns>非 0 = 吃掉这个事件，0 = 继续正常派发。</returns>
    /// <remarks>
    /// <para>
    /// 这段逻辑以前是 C++ 的 <c>CloseEventGuard</c>（<c>BrowserWidget.cpp</c>，
    /// 一个覆写 <c>eventFilter</c> 的 QObject 子类）。新 ABI 不再在原生侧做任何判断：
    /// <c>obsharp_filter_event_fn</c> 把 <c>QEvent::Type</c> 原样转发过来，
    /// 返回什么由这里决定。
    /// </para>
    /// <para>
    /// <b>为什么必须吃掉关闭事件</b>：OBS 关掉停靠面板时（<c>OBSDock::closeEvent</c>）会主动
    /// 给面板里的 widget 发一个关闭事件 —— 而且类型是自定义的 1019
    /// （<c>QEvent::User + QEvent::Close</c>），不是 <c>QEvent::Close</c>(19)。
    /// obs-browser 的 QCefWidget 收到它就会 <c>closeBrowser()</c>，那块 CEF 从此报废：
    /// 停靠面板重新显示时只剩空白、而且不会重新加载 —— 用户看到的现象就是
    /// 「dock 只在第一次有 CEF」。面板的显隐归 OBS 的 dock 管（Qt 自己会 hide），
    /// 不需要让 CEF 跟着「关」。
    /// </para>
    /// <para>
    /// 19 也要拦：父窗口被 close 时 Qt 自己会给子 widget 发 <c>QEvent::Close</c>，
    /// 那时同样不该毁掉 CEF。
    /// </para>
    /// <para>
    /// <b>为什么 Show 时要踢一下</b>：CEF 用无窗口渲染（OSR），widget 被隐藏后它就不再画了；
    /// 重新显示时 Qt 只是把旧的后备缓冲贴上去，而 CEF 那边没有收到 WasResized /
    /// Invalidate，于是看着就是一块空白。把尺寸改一下再改回来会触发
    /// <c>QCefWidget::resizeEvent</c> → <c>CefBrowserHost::WasResized()</c>，OSR 随之重新出帧。
    /// 改不动（hide 期间尺寸被锁定）时退回 <c>update()</c>，至少触发一次 paint。
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte OnFilterEvent(nint user, nint widget, int eventType)
    {
        if (GCHandle.FromIntPtr(user).Target is not WebView browser)
            return 0;

        // 两个都拦：1019 是 OBS 的 dock 关闭通知，19 是普通 Qt 关闭语义。
        // Debug 级别：这是排查用的细节，不该在正常运行时刷屏（OBS 默认配置下看不到，
        // 需要 --verbose）。
        if (eventType is QtEventType.ObsDockClose or QtEventType.Close)
        {
            _ = browser; // 只为确认句柄还有效；事件本身直接吃掉，不需要额外状态。
            NativeLogSink.Write(NativeLogLevel.Debug, "blocked a close event on the browser widget (type=" + eventType + ")");
            return 1; // 吞掉：C 侧 bool 是 1 字节，所以用 byte 而不是 bool
        }

        if (eventType is QtEventType.Show)
        {
            NativeLogSink.Write(NativeLogLevel.Debug, "browser widget shown; nudging CEF to repaint");
            NudgeRepaint(widget);
        }

        return 0; // 继续正常派发
    }

    /// <summary>
    /// 让 CEF 重新计算视口并重绘。
    /// </summary>
    /// <remarks>
    /// 只通过 QWidget 的公开接口做：把尺寸改一下再改回来。不直接调
    /// <c>CefBrowserHost::WasResized()</c> 是因为那要穿过 obs-browser 的 ABI，
    /// 而 resize 本来就会转成它。
    /// </remarks>
    private static unsafe void NudgeRepaint(nint widget)
    {
        if (widget is 0)
            return;

        int width = 0;
        int height = 0;
        NativeMethods.obsharp_widget_size(widget, &width, &height);

        if (width <= 0 || height <= 0)
        {
            // 尺寸还不可用（hide 期间 / 还没布局）：至少触发一次 paint。
            NativeMethods.obsharp_widget_update(widget);
            return;
        }

        NativeMethods.obsharp_widget_resize(widget, width + 1, height + 1);
        NativeMethods.obsharp_widget_resize(widget, width, height);
        NativeMethods.obsharp_widget_update(widget);
    }

    #endregion

    /// <summary>
    /// 把一次 Qt 操作切到主线程执行，并把异常折成返回码。
    /// </summary>
    /// <remarks>
    /// 保留「返回 <see langword="int"/> 错误码」这个形状是因为它是既有公开 API
    /// （下游 / README 里的用法都按 <c>0 = 成功</c> 写），改成抛异常就是破坏性变更。
    /// 异常细节进日志。
    /// </remarks>
    private int Forward(Action action)
    {
        if (_widget is 0)
            return (int)OBSharpResult.InvalidArgument;

        try
        {
            NativeErrors.RunUI(action);
            return (int)OBSharpResult.Ok;
        }
        catch (OBSharpNativeException exception)
        {
            _logger.LogError(exception, "Browser operation failed: {Message}", exception.Message);
            return (int)exception.Result;
        }
    }

    /// <summary>
    /// 释放 widget（带所有权判断）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这就是旧 <c>BrowserWidget</c> 析构函数里的判据</b>，注释照旧保留：
    /// </para>
    /// <para>
    /// widget 是我们用 <c>QCef::create_widget()</c> 创建的，正常情况下归我们释放
    /// （<c>deleteLater</c>，推迟到事件循环里，避免在信号处理过程中直接 delete）。
    /// </para>
    /// <para>
    /// <b>但如果它已经被 OBS 收养</b>（交给 <c>obs_frontend_add_dock_by_id</c> 之后，
    /// OBS 会 <c>setWidget</c>，所有权转到 <c>QDockWidget</c>），这里就<b>不能</b>再删：
    /// 那是二次释放。判据是它的 parent 不为空 —— OBS 接管时会 reparent 到 dock 里。
    /// </para>
    /// <para>
    /// 所以只有 <c>parent == 0</c> 时才 <c>deleteLater</c>；有父级就只 <c>hide()</c>，
    /// 把引用丢掉，剩下的交给它的父级。
    /// </para>
    /// </remarks>
    private static void ReleaseWidget(nint widget, nint guard)
    {
        NativeApi.InvokeUI(() =>
        {
            // 顺序不能换：先断信号、再撤过滤器、再关浏览器。
            //
            // 反过来的话，先 closeBrowser 可能触发 urlChanged 之类的信号，而那时
            // 回调还挂着、GCHandle 可能已经释放 —— 回调打到半销毁的对象上。
            // 旧析构也是这个顺序（disconnect → removeEventFilter → closeBrowser）。
            NativeMethods.obsharp_cef_widget_detach_callbacks(widget);

            if (guard is not 0)
                NativeMethods.obsharp_widget_remove_event_filter(widget, guard);

            NativeMethods.obsharp_cef_widget_close_browser(widget);

            NativeMethods.obsharp_widget_hide(widget);

            if (NativeMethods.obsharp_widget_parent(widget) is 0)
            {
                NativeMethods.obsharp_widget_delete_later(widget);
            }
            else
            {
                // 已经有父级：所有权在别人手里（OBS 的 QDockWidget），只把引用丢掉。
                // 这里删了就是二次释放 —— 旧 BrowserWidget 析构里同一个判据。
                NativeLogSink.Write(
                    NativeLogLevel.Debug,
                    "browser widget is owned elsewhere (adopted by OBS's dock); leaving it to its parent");
            }
        });
    }

    [LoggerMessage(LogLevel.Error, "An exception occurred in the browser callback.")]
    private static partial void LogException(ILogger logger, Exception ex);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
            return;

        _disposedValue = true;

        if (_widget is 0)
        {
            if (disposing && _handle.IsAllocated)
                _handle.Free();

            return;
        }

        nint widget = _widget;
        nint guard = _closeGuard;

        // 先置空：回调（标题 / URL / 消息）在自己的线程上会检查这个，别让它们
        // 在这之后继续把事件派发出去。
        _widget = 0;
        _closeGuard = 0;

        try
        {
            // 顺序：断回调 + 撤过滤器 + closeBrowser + hide + （按所有权）deleteLater。
            ReleaseWidget(widget, guard);
        }
        catch (Exception exception)
        {
            // 析构路径不能抛：finalizer 线程上抛异常会直接终止进程。
            // Qt 已经退出时（进程收尾阶段）这里很容易失败，记一笔就够了。
            _logger.LogWarning(exception, "Failed to destroy the browser widget (Qt may already have shut down): {Message}", exception.Message);
        }

        // 回调已经断开、过滤器已经撤掉，现在释放上下文句柄是安全的。
        if (_handle.IsAllocated)
            _handle.Free();
    }

    ~WebView()
    {
        // 注意：finalizer 里调 InvokeUI 是危险的（Qt 主线程可能正好在等 GC），
        // 但「不释放就是泄漏一个 CEF widget」更糟。ReleaseWidget 会吞掉异常，
        // 拿不到主线程时最多是漏一个 widget，不会把进程带崩。
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
