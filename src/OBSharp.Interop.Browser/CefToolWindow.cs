using System.Diagnostics;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 一个工具窗口：原生侧是独立的顶层窗口（<c>Qt::Window | Qt::Tool</c>），内含浏览器
/// Widget，归这个对象所有。
/// </summary>
/// <remarks>
/// <para>
/// 用户点关闭时窗口只会隐藏，不会销毁；<see cref="CloseRequested"/> 在 Qt 主线程上触发。
/// 要真正销毁请调 <see cref="Dispose"/>。
/// </para>
/// <para>
/// 所有会碰 Qt 的调用都经 <see cref="OBSharpNative.InvokeUi"/> 切主线程（已经在主线程时是
/// 直调），调用方不用自己判线程；创建可能等 CEF 就绪而阻塞，建议在后台线程调
/// <see cref="Create"/>。
/// </para>
/// </remarks>
public sealed class CefToolWindow : IDisposable
{
    private nint _handle = 0;
    private WindowCallbackSink? _sink;
    private CefBrowser? _browser;
    private string _title;
    private int _width;
    private int _height;

    private CefToolWindow(nint handle, WindowCallbackSink sink, ToolWindowOptions options)
    {
        _handle = handle;
        _sink = sink;
        _title = options.Title ?? string.Empty;
        _width = options.Width > 0 ? options.Width : 720;
        _height = options.Height > 0 ? options.Height : 480;
    }

    /// <summary>
    /// 用户点了窗口的关闭按钮：原生侧已经把窗口隐藏了。
    /// </summary>
    /// <remarks>
    /// 在 Qt 主线程上触发；同时也会调用 <see cref="ToolWindowOptions.Callbacks"/> 里挂的
    /// <see cref="WindowCallbacks.CloseRequested"/>。
    /// </remarks>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 创建工具窗口。失败时抛 <see cref="OBSharpNativeException"/>。
    /// </summary>
    /// <param name="options">创建参数。</param>
    /// <remarks>
    /// 原生侧要在 Qt 主线程上建窗口，并且可能等 CEF 就绪（冷启动几秒），所以可能阻塞；
    /// 建议在后台线程调用。
    /// </remarks>
    public static CefToolWindow Create(ToolWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        OBSharpNative.EnsureCompatible();

        WindowCallbackSink sink = WindowCallbackSink.Create(options.Callbacks, options.Browser.Callbacks);
        OBSharpWindowCallbacks windowCallbacks = sink.DescribeWindow();
        OBSharpBrowserCallbacks browserCallbacks = sink.DescribeBrowser();

        nint handle = 0;
        try
        {
            OBSharpNative.InvokeUi(() =>
            {
                using var buffer = NativeOptionsBuffer.CreateTool(options, browserCallbacks, windowCallbacks);
                handle = NativeMethods.ToolWindowCreate(in buffer.ToolOptions);
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
                $"obsharp_tool_window_create() 返回了 NULL：{OBSharpNative.LastError}");
        }

        var window = new CefToolWindow(handle, sink, options);
        sink.CloseRequested += window.OnNativeCloseRequested;
        return window;
    }

    /// <summary>
    /// 底层 <c>obsharp_tool_window*</c>。已释放时是 0。
    /// </summary>
    public nint Handle => _handle;

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
            OBSharpNative.InvokeUi(() => browser = NativeMethods.ToolWindowBrowser(handle));
            if (browser == 0)
                return null;

            return _browser = CefBrowser.Wrap(browser);
        }
    }

    /// <summary>
    /// 窗口标题。原生 ABI 没有 getter，读出来的是托管侧记下的值（创建时的
    /// <see cref="ToolWindowOptions.Title"/> 或上一次设置的值）。
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            nint handle = RequireHandle("Title");
            OBSharpNative.InvokeUi(
                () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.ToolWindowSetTitle(handle, value)));
            _title = value;
        }
    }

    /// <summary>
    /// 窗口大小。原生 ABI 没有 getter，读出来的是托管侧记下的值（创建时的尺寸或上一次
    /// 设置的值）。
    /// </summary>
    public (int Width, int Height) Size
    {
        get => (_width, _height);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Height);
            SetSize(value.Width, value.Height);
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
                () => visible = NativeMethods.ToolWindowIsVisible(handle) != 0);
            return visible;
        }
    }

    /// <summary>
    /// 调整窗口大小。
    /// </summary>
    public void SetSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        nint handle = RequireHandle(nameof(SetSize));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed(
                (OBSharpResult)NativeMethods.ToolWindowSetSize(handle, width, height)));
        _width = width;
        _height = height;
    }

    /// <summary>
    /// 显示。第一次显示才真正创建 CEF 浏览器并访问 URL。
    /// </summary>
    public void Show()
    {
        nint handle = RequireHandle(nameof(Show));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.ToolWindowShow(handle)));
    }

    /// <summary>
    /// 隐藏（不销毁）。
    /// </summary>
    public void Hide()
    {
        nint handle = RequireHandle(nameof(Hide));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.ToolWindowHide(handle)));
    }

    /// <summary>
    /// 销毁窗口和里面的浏览器。多次调用是安全的。
    /// </summary>
    /// <remarks>
    /// 故意没有终结器：原生窗口活着的时候它会一直握着托管回调（
    /// <see cref="WindowCallbackSink"/> 的 <see cref="GCHandle"/> 是强句柄），所以托管对象
    /// 不会在窗口还开着的时候被回收；反过来，忘记 <see cref="Dispose"/> 只会泄漏原生窗口，
    /// 不会从 GC 线程去删一个可能还有人在用的顶层窗口。窗口的销毁一律由调用方显式驱动。
    /// </remarks>
    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle == 0)
            return;

        bool destroyed = false;
        try
        {
            OBSharpNative.InvokeUi(() => NativeMethods.ToolWindowDestroy(handle));
            destroyed = true;
        }
        catch (Exception exception)
        {
            // 进程正在退出时切不到主线程；这里没有补救手段，记一笔就算了。
            Debug.WriteLine($"[OBSharp.Interop.Browser.Native] 销毁工具窗口失败：{exception}");
        }

        // 浏览器句柄是借用的：只清掉托管包装，原生对象已经跟着窗口一起销毁了。
        _browser?.Dispose();
        _browser = null;

        if (!destroyed)
        {
            // 没销毁成功就别拆回调：原生窗口还在，它拿着的 user 指针得继续有效。
            return;
        }

        if (_sink is not null)
        {
            _sink.CloseRequested -= OnNativeCloseRequested;
            _sink.Dispose();
            _sink = null;
        }
    }

    private void OnNativeCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private nint RequireHandle(string member)
    {
        nint handle = _handle;
        if (handle == 0)
            throw new ObjectDisposedException(nameof(CefToolWindow), $"窗口已经释放，不能再调用 {member}。");

        return handle;
    }
}
