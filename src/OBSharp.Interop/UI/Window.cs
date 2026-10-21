using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using OBSharp.Interop.Native;

namespace OBSharp.Interop.UI;

/// <summary>
/// 一个普通顶层窗口容器：原生侧是 <c>Qt::Window</c>（普通标题栏、进任务栏、可单独
/// Alt+Tab），里面可以放任何 <c>QWidget</c>，通常是 <see cref="WebView.Widget"/>。
/// </summary>
/// <remarks>
/// <para>
/// 它<b>不含浏览器</b>：浏览器是另外用 <see cref="WebView"/> 建的，通过
/// <see cref="SetContent"/> 放进来。所以同一个浏览器 Widget 也能改交给 OBS 的
/// <c>obs_frontend_add_dock_by_id()</c> 当停靠面板。
/// </para>
/// <para>
/// 用户点关闭时窗口只会隐藏，不会销毁；<see cref="CloseRequested"/> 在 Qt 主线程上触发。
/// 要真正销毁请调 <see cref="Dispose"/>（它不会销毁里面的浏览器）。
/// </para>
/// <para>
/// 所有会碰 Qt 的调用都经 <see cref="NativeApi.InvokeUI"/> 切主线程（已经在主线程时是
/// 直调），调用方不用自己判线程。
/// </para>
/// <para>
/// 实现说明（跟旧版的差别）：旧版的原生库直接吃一整份
/// <c>obsharp_window_options</c>，标题 / 尺寸 / 置顶 / 是否可缩放都在 C++ 里摆；
/// 新 ABI 只给一个「空的 Qt::Window 容器」（<c>obsharp_window_create</c> 只收关闭回调），
/// 所以这些参数现在由这里逐条用 <c>obsharp_widget_*</c> 摆上去 —— 逻辑搬到 C#，
/// 对外行为不变。
/// </para>
/// </remarks>
public partial class Window : IDisposable
{
    /// <summary>
    /// 默认宽度 / 高度。对应旧 <c>PlainWindow::Create()</c> 里的
    /// <c>kDefaultWidth</c> / <c>kDefaultHeight</c>：options 里给的是非正数时用它们。
    /// </summary>
    private const int DefaultWidth = 720;
    private const int DefaultHeight = 480;

    private readonly nint _native;

    /// <summary>
    /// 原生窗口对象的 id。关掉回调用的上下文就是它（见 <see cref="OnCloseRequested"/>）。
    /// </summary>
    private readonly long _callbackId;

    /// <summary>
    /// 当前内容的 <c>QWidget*</c>；0 表示没设内容。
    /// </summary>
    /// <remarks>
    /// 新 ABI 的 <c>obsharp_window_create()</c> 只返回容器自己的 <c>QWidget*</c>，
    /// 没有「窗口句柄」这一层了，所以「当前内容是谁」只能由这边记着 ——
    /// 旧版的 <c>obsharp_window_widget()</c> 是回到 C++ 里问 <c>PlainWindow::Widget()</c>。
    /// </remarks>
    private nint _content;

    private bool _disposedValue;

    /// <summary>
    /// 活着的窗口：回调 id → 弱引用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不像 <see cref="WebView"/> 那样直接
    /// <c>GCHandle.Alloc(this)</c> 然后把句柄当 <c>user</c> 传给原生侧：
    /// </para>
    /// <para>
    /// <see cref="WebView"/> 可以在销毁时用 <c>obsharp_cef_widget_set_callbacks(widget, NULL)</c>
    /// 明确断开回调，断完就能安全地释放句柄。窗口没有这个原语 ——
    /// <c>obsharp_window_create()</c> 收下的回调指针在窗口的整个生命周期里都不会被替换，
    /// 而销毁用的是 <c>deleteLater()</c>（推迟到事件循环里执行）。也就是说
    /// 「句柄已经 Free 了」和「窗口真的没了」之间有一段空窗，这期间如果来一个
    /// closeEvent，回调就会拿着释放过的句柄去 <see cref="GCHandle.FromIntPtr"/> ——
    /// 那是未定义行为，实测就是进程直接崩。
    /// </para>
    /// <para>
    /// 用「id → 弱引用」的注册表绕开这件事：句柄问题根本不存在，
    /// <see cref="Dispose"/> 只要把表项摘掉，晚到的回调就查不到对象、安静丢弃；
    /// 弱引用还顺带保证这里不会把整个 <see cref="Window"/>（连带它的 logger）永久 root 住。
    /// 回调极稀疏（只有用户点关闭），一次字典查找的开销无所谓。
    /// </para>
    /// </remarks>
    private static readonly ConcurrentDictionary<long, WeakReference<Window>> s_callbacks = new();

    private static long s_nextCallbackId;

    protected ILogger Logger { get; }

    /// <summary>
    /// 当前内容的 <c>QWidget*</c>；没设内容时是窗口自己。
    /// </summary>
    public nint Widget => _content is not 0 ? _content : _native;

    /// <summary>
    /// 是否可见。
    /// </summary>
    /// <remarks>
    /// 这是唯一一个「读」操作：原生侧 <c>obsharp_widget_is_visible()</c> 要求 Qt 主线程，
    /// 所以仍然要过一次 <see cref="NativeApi.InvokeUI"/>，把结果带回调用线程。
    /// 读不到时返回 <see langword="false"/> 而不是抛异常 —— 这是个属性，
    /// 旧版（直接 P/Invoke 拿 <c>int32_t</c>）也从不抛，保持这个约定。
    /// </remarks>
    public bool IsVisible
    {
        get
        {
            if (_native is 0)
                return false;

            bool visible = false;
            try
            {
                NativeErrors.RunUI(() => visible = NativeMethods.obsharp_widget_is_visible(_native));
            }
            catch (OBSharpNativeException exception)
            {
                Logger.LogWarning(exception, "Failed to read the window visibility: {Message}", exception.Message);
                return false;
            }

            return visible;
        }
    }

    /// <summary>
    /// 用户点了窗口的关闭按钮：原生侧已经把窗口隐藏了。
    /// </summary>
    /// <remarks>
    /// 在 Qt 主线程上触发
    /// </remarks>
    public event EventHandler? CloseRequested;

    public unsafe Window(WindowOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        Logger = logger;

        // 原生库的日志走托管侧注入的回调（它不自己找 libobs 的 blog()）。这里是最早
        // 能拿到宿主 ILogger 的地方，所以在这一刻把它接上。
        NativeLogSink.Install(logger);

        // 尺寸：options 里给的是非正数时退回默认值（旧 PlainWindow::Create() 的判断）。
        int width = options.Width > 0 ? options.Width : DefaultWidth;
        int height = options.Height > 0 ? options.Height : DefaultHeight;

        _callbackId = Interlocked.Increment(ref s_nextCallbackId);
        s_callbacks[_callbackId] = new WeakReference<Window>(this);

        // 记下「创建时走的是哪条路」，SetSize 要照同一条路走（见 SetSize 的注释）。
        _fixedSize = !options.Resizable;

        nint created = 0;
        try
        {
            NativeErrors.RunUI(() =>
            {
                NativeTypes.WindowCallbacks callbacks = new()
                {
                    User = (nint)_callbackId,
                    OnCloseRequested = &OnCloseRequested,
                };

                created = NativeMethods.obsharp_window_create(in callbacks);

                // 建窗口是本类的全部意义，拿不到就没有可用的对象，直接失败。
                // （旧版这里返回 0 之后所有方法都退化成 no-op，问题是调用方完全看不出
                // 窗口根本没建起来 —— 抛异常反而更好查。）
                if (created is 0)
                {
                    throw NativeErrors.Create(
                        OBSharpResult.Internal,
                        "obsharp_window_create() returned a null handle: could not create the Qt window container.");
                }

                // 标题：空 / null 时不设，保持 Qt 默认（旧实现同样只在非空时 setWindowTitle）。
                if (!string.IsNullOrEmpty(options.Title))
                    NativeMethods.obsharp_widget_set_window_title(created, options.Title);

                // 可缩放 → resize()；固定尺寸 → setFixedSize()。
                // 旧实现的语义就是这两条二选一，这里照搬（不是 resize 之后再锁）。
                if (options.Resizable)
                    NativeMethods.obsharp_widget_resize(created, width, height);
                else
                    NativeMethods.obsharp_widget_set_fixed_size(created, width, height);

                // 置顶：Qt::WindowStaysOnTopHint。用 Qt::Window 而不是 Qt::Tool
                // 是为了在 Windows 上进任务栏、进 Alt+Tab（见旧 Window.cpp 的注释）。
                if (options.Topmost)
                    NativeMethods.obsharp_widget_set_topmost(created, true);

                // 原生侧的 obsharp_widget_show 已经是 show() + raise()，一次就够
                // （旧 PlainWindow::Show() 也是这两句）。
                if (options.Visible)
                    NativeMethods.obsharp_widget_show(created);

                // 标题里的 (none) 约定沿用旧日志：排查「窗口标题不对」时一眼能看出是没给还是给错。
                NativeLogSink.Write(
                    NativeLogLevel.Info,
                    "plain window created: title=" + (options.Title ?? "") +
                    ", topmost=" + (options.Topmost ? "1" : "0") +
                    ", resizable=" + (options.Resizable ? "1" : "0"));
            });
        }
        catch
        {
            s_callbacks.TryRemove(_callbackId, out _);
            throw;
        }

        _native = created;
    }

    /// <summary>
    /// 把内容放进窗口（通常是 <see cref="WebView.Widget"/>）。传 0 清空。
    /// </summary>
    /// <remarks>
    /// 原生侧 <c>obsharp_widget_set_content()</c> 负责建布局、摘掉旧内容、加进新内容，
    /// 而且<b>不拥有</b>内容 —— 窗口销毁时不会删它。旧 <c>PlainWindow::SetContent()</c>
    /// 也是这个语义（只做 layout 的 add/remove，外加 hide + setParent(nullptr)）。
    /// </remarks>
    public int SetContent(nint content)
    {
        if (_native is 0)
            return (int)OBSharpResult.InvalidArgument;

        return Forward(() =>
        {
            NativeMethods.obsharp_widget_set_content(_native, content);
            _content = content;
        });
    }

    /// <summary>
    /// 显示窗口。
    /// </summary>
    public virtual int Show() => Forward(() => NativeMethods.obsharp_widget_show(_native));

    /// <summary>
    /// 隐藏窗口。
    /// </summary>
    public virtual int Hide() => Forward(() => NativeMethods.obsharp_widget_hide(_native));

    /// <summary>
    /// 设置窗口标题。
    /// </summary>
    public int SetTitle(string? title)
        => Forward(() => NativeMethods.obsharp_widget_set_window_title(_native, title));

    /// <summary>
    /// 设置窗口大小。
    /// </summary>
    /// <remarks>
    /// 跟创建时一样区分「可缩放 / 固定尺寸」：固定尺寸的窗口只有 setFixedSize 才动得了，
    /// 对它调 resize 会被 Qt 立刻改回原尺寸（看起来像「这个方法没生效」）。
    /// 旧 <c>PlainWindow::SetSize()</c> 就是用 <c>fixedSize_</c> 这个成员分的，这里由
    /// 调用方持有的 options 决定不了，所以记在 <see cref="_fixedSize"/> 里。
    /// </remarks>
    public int SetSize(int width, int height) => Forward(() =>
    {
        if (width <= 0 || height <= 0)
            throw NativeErrors.Create(OBSharpResult.InvalidArgument, "The window width and height must be positive.");

        if (_fixedSize)
            NativeMethods.obsharp_widget_set_fixed_size(_native, width, height);
        else
            NativeMethods.obsharp_widget_resize(_native, width, height);
    });

    /// <summary>
    /// 创建时是否走了「固定尺寸」那条路。创建之后不再变，所以 <see cref="SetSize"/>
    /// 能照着同一条路走。
    /// </summary>
    private readonly bool _fixedSize;

    /// <summary>
    /// 把一次 Qt 操作切到主线程执行，并把异常折成返回码。
    /// </summary>
    /// <remarks>
    /// 为什么这些方法返回 <see langword="int"/> 而不是抛异常：这是既有的公开 API 形状
    /// （旧版直接把原生的 <c>int32_t</c> 返回码传出去，0 = 成功、负 = 错误码），
    /// 下游已经按这个写。要改签名就是破坏性变更，所以保留；异常信息落到日志里。
    /// </remarks>
    private int Forward(Action action)
    {
        if (_native is 0)
            return (int)OBSharpResult.InvalidArgument;

        try
        {
            NativeErrors.RunUI(action);
            return (int)OBSharpResult.Ok;
        }
        catch (OBSharpNativeException exception)
        {
            Logger.LogError(exception, "Window operation failed: {Message}", exception.Message);
            return (int)exception.Result;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnCloseRequested(nint id)
    {
        if (!s_callbacks.TryGetValue((long)id, out WeakReference<Window>? reference) ||
            !reference.TryGetTarget(out Window? window))
        {
            // 窗口已经 Dispose 掉了，但回调还可能晚到一帧（见 s_callbacks 的说明）。
            return;
        }

        try
        {
            window.CloseRequested?.Invoke(window, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogException(window.Logger, ex);
        }
    }

    [LoggerMessage(LogLevel.Error, "An exception occurred in the window callback.")]
    private static partial void LogException(ILogger logger, Exception ex);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
            return;

        _disposedValue = true;

        // 先把回调摘掉：之后即使还有 closeEvent 晚到，也查不到这个窗口了。
        s_callbacks.TryRemove(_callbackId, out _);

        if (_native is 0)
            return;

        try
        {
            NativeApi.InvokeUI(() =>
            {
                // 先把内容从布局里摘掉，但**不**销毁它（内容归 WebView 所有，
                // 谁先销毁由调用方决定 —— PluginManagerWindow 的 OnClosing 就是
                // 先 SetContent(0) 再 Dispose 浏览器的）。
                //
                // 注意 obsharp_widget_set_content(container, 0) 只做 QVBoxLayout::removeWidget，
                // **不**会把子 widget 的 parent 置空。所以调用方即使漏了这一步，
                // WebView.Dispose() 里的 obsharp_widget_parent() 判据照样成立（有父级 → 只 hide），
                // 不会二次释放 —— 宁可漏一个 widget，也不能把 OBS 手里的指针删掉。
                NativeMethods.obsharp_widget_set_content(_native, 0);
                NativeMethods.obsharp_widget_hide(_native);

                // deleteLater()：推迟到事件循环里销毁。绝不能在 Qt 正在派发事件的栈上
                // 直接 delete（比如在 closeEvent 回调里销毁窗口），那是崩溃。
                NativeMethods.obsharp_widget_delete_later(_native);
            });
        }
        catch (Exception exception) when (exception is OBSharpNativeException or NativeException)
        {
            // 析构路径不能抛（finalizer 线程抛异常会直接终止进程），也不该因为
            // 「Qt 已经没了」让调用方的 Dispose 失败。记一笔就够了。
            Logger.LogWarning(exception, "Failed to destroy the window (Qt may already have shut down): {Message}", exception.Message);
        }

        _content = 0;
    }

    ~Window()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
