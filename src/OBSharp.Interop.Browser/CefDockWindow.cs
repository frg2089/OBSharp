using System.Diagnostics;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 一个 OBS 停靠窗口：原生侧是一个 <c>QDockWidget</c>（内含浏览器 Widget），归这个对象所有。
/// </summary>
/// <remarks>
/// <para>
/// 创建出来还没挂进 OBS：先 <see cref="OBSharpNative.InvokeUi"/>，再把 <see cref="Widget"/>
/// 交给 <c>obs_frontend_add_custom_qdock(id, widget)</c>，之后 <see cref="Show"/> /
/// <see cref="Hide"/> 才会正常。
/// </para>
/// <para>
/// <see cref="Dispose"/> 会销毁浏览器和 <c>QDockWidget</c>，所以调用方必须先从 OBS 摘掉
/// （<c>obs_frontend_remove_dock</c>），否则 OBS 还引用着它。
/// </para>
/// <para>
/// 所有会碰 Qt 的调用都经 <see cref="OBSharpNative.InvokeUi"/> 切主线程（已经在主线程时是
/// 直调），调用方不用自己判线程；创建可能等 CEF 就绪而阻塞，建议在后台线程调
/// <see cref="Create"/>。
/// </para>
/// </remarks>
public sealed class CefDockWindow : IDisposable
{
    private nint _handle = 0;
    private WindowCallbackSink? _sink;
    private CefBrowser? _browser;
    private string _title;

    private CefDockWindow(nint handle, WindowCallbackSink sink, DockWindowOptions options)
    {
        _handle = handle;
        _sink = sink;
        _title = options.Title ?? string.Empty;
        Id = options.Id;
    }

    /// <summary>
    /// 创建停靠窗口。失败时抛 <see cref="OBSharpNativeException"/>。
    /// </summary>
    /// <param name="options">创建参数。</param>
    /// <remarks>
    /// 原生侧要在 Qt 主线程上建 <c>QDockWidget</c>，并且可能等 CEF 就绪（冷启动几秒），
    /// 所以可能阻塞；建议在后台线程调用。
    /// </remarks>
    public static CefDockWindow Create(DockWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        OBSharpNative.EnsureCompatible();

        WindowCallbackSink sink = WindowCallbackSink.Create(options.Callbacks, options.Browser.Callbacks);

        // 停靠窗口没有「关闭」这个概念（开关由 OBS 的 dock 决定），所以不挂关闭回调：
        // 原生侧看到非 NULL 的 on_close_requested 会记一条「停靠窗口忽略它」的警告。
        OBSharpWindowCallbacks windowCallbacks = sink.DescribeWindow(withCloseRequested: false);
        OBSharpBrowserCallbacks browserCallbacks = sink.DescribeBrowser();

        nint handle = 0;
        try
        {
            OBSharpNative.InvokeUi(() =>
            {
                using var buffer = NativeOptionsBuffer.CreateDock(options, browserCallbacks, windowCallbacks);
                handle = NativeMethods.DockWindowCreate(in buffer.DockOptions);
            });
        }
        catch
        {
            sink.Dispose();
            throw;
        }

        if (handle == 0)
        {
            sink.Dispose();
            throw new OBSharpNativeException(
                OBSharpResult.Internal,
                $"obsharp_dock_window_create() 返回了 NULL：{OBSharpNative.LastError}");
        }

        return new CefDockWindow(handle, sink, options);
    }

    /// <summary>
    /// 这个面板在 OBS 里的 id。就是创建时给的 <see cref="DockWindowOptions.Id"/>；
    /// 为 <see langword="null"/> 时原生侧用 host 名推导。
    /// </summary>
    public string? Id { get; }

    /// <summary>
    /// 底层 <c>obsharp_dock_window*</c>。已释放时是 0。
    /// </summary>
    public nint Handle => _handle;

    /// <summary>
    /// 底层 <c>QDockWidget*</c>（借用）。交给 <c>obs_frontend_add_custom_qdock()</c> 用；
    /// 不要自己销毁。已释放时是 0。
    /// </summary>
    public nint Widget
    {
        get
        {
            nint handle = _handle;
            if (handle == 0)
                return 0;

            nint widget = 0;
            OBSharpNative.InvokeUi(() => widget = NativeMethods.DockWindowWidget(handle));
            return widget;
        }
    }

    /// <summary>
    /// 里面的浏览器（借用包装：<see cref="CefBrowser.Dispose"/> 不会销毁原生对象）。
    /// 原生侧还没建出浏览器时是 <see langword="null"/>。
    /// </summary>
    public CefBrowser? Browser
    {
        get
        {
            // 借用包装随时可能被调用方 Dispose：那就丢掉缓存重新包一个。
            if (_browser is { IsDisposed: false })
                return _browser;

            _browser = null;

            nint handle = _handle;
            if (handle == 0)
                return null;

            nint browser = 0;
            OBSharpNative.InvokeUi(() => browser = NativeMethods.DockWindowBrowser(handle));
            if (browser == 0)
                return null;

            return _browser = CefBrowser.Wrap(browser);
        }
    }

    /// <summary>
    /// 面板标题。原生 ABI 没有 getter，读出来的是托管侧记下的值（创建时的
    /// <see cref="DockWindowOptions.Title"/> 或上一次设置的值）。
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            nint handle = RequireHandle("Title");
            OBSharpNative.InvokeUi(
                () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.DockWindowSetTitle(handle, value)));
            _title = value;
        }
    }

    /// <summary>
    /// 是否可见。
    /// </summary>
    public bool IsVisible
    {
        get
        {
            nint handle = _handle;
            if (handle == 0)
                return false;

            bool visible = false;
            OBSharpNative.InvokeUi(
                () => visible = NativeMethods.DockWindowIsVisible(handle) != 0);
            return visible;
        }
    }

    /// <summary>
    /// 显示。第一次显示才真正创建 CEF 浏览器并访问 URL。
    /// </summary>
    public void Show()
    {
        nint handle = RequireHandle(nameof(Show));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.DockWindowShow(handle)));
    }

    /// <summary>
    /// 隐藏（不销毁）。
    /// </summary>
    public void Hide()
    {
        nint handle = RequireHandle(nameof(Hide));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.DockWindowHide(handle)));
    }

    /// <summary>
    /// 调整面板尺寸。
    /// </summary>
    public void SetSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        nint handle = RequireHandle(nameof(SetSize));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed(
                (OBSharpResult)NativeMethods.DockWindowSetSize(handle, width, height)));
    }

    /// <summary>
    /// 销毁浏览器和 <c>QDockWidget</c>。多次调用是安全的。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须先从 OBS 摘掉（<c>obs_frontend_remove_dock</c>），否则 OBS 还引用着这个
    /// <c>QDockWidget</c>。
    /// </para>
    /// <para>
    /// 故意没有终结器：被遗忘的停靠窗口宁可泄漏原生对象，也不能在 OBS 还引用着
    /// <c>QDockWidget</c> 的时候从 GC 线程把它删掉（那会让 OBS 拿到悬空指针）。
    /// 原生对象要回收，调用方必须显式 <see cref="Dispose"/>。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle == 0)
        {
            // 已经释放过（或者正在释放）：Sink 跟着原生对象一起收，不要重复释放。
            _browser?.Dispose();
            _browser = null;
            return;
        }

        bool destroyed = false;
        try
        {
            OBSharpNative.InvokeUi(() => NativeMethods.DockWindowDestroy(handle));
            destroyed = true;
        }
        catch (Exception exception)
        {
            // 进程正在退出时切不到主线程；这里没有补救手段，记一笔就算了。
            Debug.WriteLine($"[OBSharp.Interop.Browser.Native] 销毁停靠窗口失败：{exception}");
        }

        // 浏览器句柄是借用的：只清掉托管包装，原生对象已经跟着窗口一起销毁了。
        _browser?.Dispose();
        _browser = null;

        if (destroyed)
        {
            _sink?.Dispose();
            _sink = null;
        }

        // 没销毁成功就故意留着 Sink：原生窗口还在，它拿着的 user 指针得继续有效，
        // 否则原生回调会打到已经释放的 GCHandle 上。
    }

    private nint RequireHandle(string member)
    {
        nint handle = _handle;
        if (handle == 0)
            throw new ObjectDisposedException(nameof(CefDockWindow), $"窗口已经释放，不能再调用 {member}。");

        return handle;
    }
}
