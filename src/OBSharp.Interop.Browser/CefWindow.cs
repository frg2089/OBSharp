using System.Diagnostics;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 一个普通顶层窗口容器：原生侧是 <c>Qt::Window</c>（普通标题栏、进任务栏、可单独
/// Alt+Tab），里面可以放任何 <c>QWidget</c>，通常是 <see cref="CefBrowser.Widget"/>。
/// </summary>
/// <remarks>
/// <para>
/// 它<b>不含浏览器</b>：浏览器是另外用 <see cref="CefBrowser.Create"/> 建的，通过
/// <see cref="Content"/> 放进来。所以同一个浏览器 Widget 也能改交给 OBS 的
/// <c>obs_frontend_add_dock_by_id()</c> 当停靠面板。
/// </para>
/// <para>
/// 用户点关闭时窗口只会隐藏，不会销毁；<see cref="CloseRequested"/> 在 Qt 主线程上触发。
/// 要真正销毁请调 <see cref="Dispose"/>（它不会销毁里面的浏览器）。
/// </para>
/// <para>
/// 所有会碰 Qt 的调用都经 <see cref="OBSharpNative.InvokeUi"/> 切主线程（已经在主线程时是
/// 直调），调用方不用自己判线程。
/// </para>
/// </remarks>
public sealed class CefWindow : IDisposable
{
    private nint _handle = 0;
    private WindowCallbackSink? _sink;
    private nint _content;
    private string _title;
    private int _width;
    private int _height;

    private CefWindow(nint handle, WindowCallbackSink sink, WindowOptions options)
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
    /// 在 Qt 主线程上触发；同时也会调用 <see cref="WindowOptions.Callbacks"/> 里挂的
    /// <see cref="WindowCallbacks.CloseRequested"/>。
    /// </remarks>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 创建窗口（不含内容）。失败时抛 <see cref="OBSharpNativeException"/>。
    /// </summary>
    public static CefWindow Create(WindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        OBSharpNative.EnsureCompatible();

        WindowCallbackSink sink = WindowCallbackSink.Create(options.Callbacks);
        OBSharpWindowCallbacks windowCallbacks = sink.DescribeWindow();

        nint handle = 0;
        try
        {
            OBSharpNative.InvokeUi(() =>
            {
                using var buffer = NativeOptionsBuffer.CreateWindow(options, windowCallbacks);
                handle = NativeMethods.WindowCreate(in buffer.WindowOptions);
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
                $"obsharp_window_create() 返回了 NULL：{OBSharpNative.LastError}");
        }

        var window = new CefWindow(handle, sink, options);
        sink.CloseRequested += window.OnNativeCloseRequested;
        return window;
    }

    /// <summary>
    /// 底层 <c>obsharp_window*</c>。已释放时是 0。
    /// </summary>
    public nint Handle => _handle;

    /// <summary>
    /// 把内容放进窗口（通常是 <see cref="CefBrowser.Widget"/>）。传 0 清空。
    /// </summary>
    /// <remarks>
    /// 窗口<b>不接管</b>内容的所有权：销毁窗口不会销毁它，得自己管（比如
    /// <see cref="CefBrowser.Dispose"/>）。
    /// </remarks>
    public nint Content
    {
        get => _content;
        set
        {
            nint handle = RequireHandle(nameof(Content));
            OBSharpNative.InvokeUi(() =>
            {
                OBSharpNativeException.ThrowIfFailed(
                    (OBSharpResult)NativeMethods.WindowSetContent(handle, value));
            });

            _content = value;
        }
    }

    /// <summary>
    /// 当前内容的 <c>QWidget*</c>；没设内容时是窗口自己。
    /// </summary>
    public nint Widget
    {
        get
        {
            nint handle = _handle;
            if (handle == 0)
                return 0;

            nint widget = 0;
            OBSharpNative.InvokeUi(() => widget = NativeMethods.WindowWidget(handle));
            return widget;
        }
    }

    /// <summary>
    /// 窗口标题。写入时同步到原生窗口；读取返回托管侧缓存（原生 ABI 没有 getter）。
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            nint handle = RequireHandle(nameof(Title));
            OBSharpNative.InvokeUi(
                () => OBSharpNativeException.ThrowIfFailed(
                    (OBSharpResult)NativeMethods.WindowSetTitle(handle, value)));
            _title = value;
        }
    }

    /// <summary>
    /// 窗口尺寸。写入时同步到原生窗口；读取返回托管侧缓存。
    /// </summary>
    public (int Width, int Height) Size
    {
        get => (_width, _height);
        set => SetSize(value.Width, value.Height);
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

            int visible = 0;
            OBSharpNative.InvokeUi(() => visible = NativeMethods.WindowIsVisible(handle));
            return visible != 0;
        }
    }

    /// <summary>
    /// 显示窗口。
    /// </summary>
    public void Show()
    {
        nint handle = RequireHandle(nameof(Show));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.WindowShow(handle)));
    }

    /// <summary>
    /// 隐藏窗口（不销毁，内容还在）。
    /// </summary>
    public void Hide()
    {
        nint handle = RequireHandle(nameof(Hide));
        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)NativeMethods.WindowHide(handle)));
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
                (OBSharpResult)NativeMethods.WindowSetSize(handle, width, height)));

        _width = width;
        _height = height;
    }

    /// <summary>
    /// 销毁窗口（<b>不</b>销毁里面的浏览器）。多次调用是安全的。
    /// </summary>
    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            try
            {
                OBSharpNative.InvokeUi(() => NativeMethods.WindowDestroy(handle));
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[OBSharp.Interop.Browser.Native] 销毁窗口失败：{exception}");
            }
        }

        if (_sink is not null)
        {
            _sink.CloseRequested -= OnNativeCloseRequested;
            _sink.Dispose();
            _sink = null;
        }

        GC.SuppressFinalize(this);
    }

    ~CefWindow()
    {
        // 进程正在退出时不碰原生侧：那时 Qt 事件循环可能已经停了。
        if (Environment.HasShutdownStarted)
            return;

        try
        {
            Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[OBSharp.Interop.Browser.Native] 终结器里销毁窗口失败：{exception}");
        }
    }

    private void OnNativeCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private nint RequireHandle(string member)
    {
        nint handle = _handle;
        if (handle == 0)
            throw new ObjectDisposedException(nameof(CefWindow), $"窗口已经释放，不能再调用 {member}。");

        return handle;
    }
}
