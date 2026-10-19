using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 一个 CEF 浏览器 Widget。底层是 obs-browser 的 <c>QCefWidget</c>：<see cref="Create"/>
/// 出来的对象归这个对象所有，<see cref="Wrap"/> 出来的只是借用。
/// </summary>
/// <remarks>
/// <para>
/// 原生侧要求所有操作都在 Qt 主线程上；这里会自己用
/// <see cref="OBSharpNative.InvokeUi"/> 切过去（已经在主线程时是直调），调用方不用管。
/// </para>
/// <para>
/// <see cref="Widget"/> 返回的指针可以直接 <c>setParent</c> / 塞进 dock，但不要自己
/// <c>delete</c>：<see cref="Dispose"/> 会一并销毁。
/// </para>
/// <para>
/// 窗口（<see cref="CefDockWindow"/> / <see cref="CefToolWindow"/>）里的浏览器是
/// <see cref="Wrap"/> 包装的借用句柄：<see cref="Dispose"/> 只释放托管侧，不会销毁
/// 原生对象（它归窗口所有）。
/// </para>
/// </remarks>
public sealed unsafe class CefBrowser : IDisposable
{
    private nint _handle;
    private GCHandle _sink;
    private readonly bool _owned;

    private CefBrowser(nint handle, GCHandle sink, bool owned)
    {
        _handle = handle;
        _sink = sink;
        _owned = owned;
    }

    /// <summary>
    /// 创建浏览器。失败时抛 <see cref="OBSharpNativeException"/>。
    /// </summary>
    /// <param name="options">创建参数。</param>
    public static CefBrowser Create(BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        OBSharpNative.EnsureCompatible();

        GCHandle sink = GCHandle.Alloc(new CallbackSink(options.Callbacks));
        OBSharpBrowserCallbacks callbacks = CallbackSink.Describe();
        callbacks.User = GCHandle.ToIntPtr(sink);

        nint handle = 0;
        try
        {
            OBSharpNative.InvokeUi(() =>
            {
                using var buffer = NativeOptionsBuffer.Create(options, callbacks);
                handle = NativeMethods.BrowserCreate(in buffer.Browser);
            });
        }
        catch
        {
            sink.Free();
            throw;
        }

        if (handle == 0)
        {
            sink.Free();
            throw new OBSharpNativeException(
                OBSharpResult.Internal,
                $"obsharp_browser_create() 返回了 NULL：{OBSharpNative.LastError}");
        }

        return new CefBrowser(handle, sink, owned: true);
    }

    /// <summary>
    /// 包装一个已经存在的原生浏览器句柄（借用，不接管所有权）。
    /// </summary>
    /// <param name="handle"><c>obsharp_browser*</c>，必须非 0。</param>
    /// <remarks>
    /// 用于 <see cref="CefDockWindow.Browser"/> / <see cref="CefToolWindow.Browser"/>：
    /// 原生对象归窗口所有，<see cref="Dispose"/> 只清掉托管包装，不会销毁它。
    /// </remarks>
    internal static CefBrowser Wrap(nint handle)
    {
        if (handle == 0)
            throw new ArgumentOutOfRangeException(nameof(handle), "句柄是 0，没有可包装的浏览器。");

        return new CefBrowser(handle, default, owned: false);
    }

    /// <summary>
    /// 底层 <c>obsharp_browser*</c>。已释放时是 0。
    /// </summary>
    public nint Handle => _handle;

    /// <summary>
    /// 是否已经 <see cref="Dispose"/> 过。
    /// </summary>
    internal bool IsDisposed => _handle == 0;

    /// <summary>
    /// 底层 <c>QWidget*</c>。可以直接 <c>setParent</c> / 放进 dock，但不要自己销毁。
    /// </summary>
    public nint Widget => _handle == 0 ? 0 : NativeMethods.BrowserWidget(_handle);

    /// <summary>
    /// 当前文档 URL；还没加载时是空串。
    /// </summary>
    public string CurrentUrl
    {
        get
        {
            nint handle = _handle;
            if (handle == 0)
                return string.Empty;

            return Marshal.PtrToStringUTF8((nint)NativeMethods.BrowserCurrentUrl(handle)) ?? string.Empty;
        }
    }

    /// <summary>
    /// 导航到 <paramref name="url"/>。传 <c>obsharp://&lt;host&gt;/...</c> 时会按该浏览器的
    /// host 映射成 <c>https://&lt;host&gt;.obsharp/...</c>（仍然走 IPC、不做 TLS）。
    /// </summary>
    public void Load(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        Run(handle => NativeMethods.BrowserLoad(handle, url));
    }

    /// <summary>
    /// 忽略缓存重新加载。
    /// </summary>
    public void Reload() => Run(NativeMethods.BrowserReload);

    /// <summary>
    /// 缩放：小于 0 缩小，大于 0 放大，等于 0 复位。
    /// </summary>
    public void Zoom(int direction) => Run(handle => NativeMethods.BrowserZoom(handle, direction));

    /// <summary>
    /// 在当前文档里执行一段 JavaScript。
    /// </summary>
    public void ExecuteScript(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        Run(handle => NativeMethods.BrowserExecuteScript(handle, script));
    }

    /// <summary>
    /// 设置每个页面文档创建前注入的脚本。注意这会整体替换原生侧拼好的引导脚本
    /// （<c>window.obsharp</c>），所以一般用 <see cref="BrowserOptions.StartupScript"/>。
    /// </summary>
    public void SetStartupScript(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        Run(handle => NativeMethods.BrowserSetStartupScript(handle, script));
    }

    /// <summary>
    /// 是否允许弹出窗口。
    /// </summary>
    public void AllowPopups(bool allow)
        => Run(handle => NativeMethods.BrowserAllowPopups(handle, allow ? 1 : 0));

    /// <summary>
    /// 调整 Widget 大小。
    /// </summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Run(handle => NativeMethods.BrowserResize(handle, width, height));
    }

    /// <summary>
    /// 移动 Widget。
    /// </summary>
    public void Move(int x, int y) => Run(handle => NativeMethods.BrowserMove(handle, x, y));

    /// <summary>
    /// 显示 / 隐藏。第一次显示会真正创建 CEF 浏览器。
    /// </summary>
    public void SetVisible(bool visible)
        => Run(handle => NativeMethods.BrowserSetVisible(handle, visible ? 1 : 0));

    /// <summary>
    /// 给页面发一条消息：页面里会派发 <c>CustomEvent("obsharp-message")</c>，
    /// <c>detail</c> 是解析后的 JSON。
    /// </summary>
    public void PostMessage(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Run(handle => NativeMethods.BrowserPostMessage(handle, json));
    }

    /// <summary>
    /// 销毁浏览器，连带销毁底层 Widget。多次调用是安全的。
    /// </summary>
    /// <remarks>
    /// 借用包装（<see cref="Wrap"/>）只释放托管侧的 <see cref="GCHandle"/>，不会销毁原生
    /// 对象：窗口还在用着它。
    /// </remarks>
    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);

        if (_owned && handle != 0)
            NativeMethods.BrowserDestroy(handle);

        if (_sink.IsAllocated)
        {
            _sink.Free();
            _sink = default;
        }

        GC.SuppressFinalize(this);
    }

    ~CefBrowser()
    {
        // 进程正在退出时不碰原生侧：那时 OBS 的 Qt 事件循环可能已经停了，而原生
        // 销毁要从非主线程同步切到主线程，会把终结器线程卡死。
        if (Environment.HasShutdownStarted)
            return;

        try
        {
            Dispose();
        }
        catch (Exception exception)
        {
            // 终结器里不能再抛。
            Debug.WriteLine($"[OBSharp.Interop.Browser.Native] 销毁浏览器失败：{exception}");
        }
    }

    private void Run(Func<nint, int> action)
    {
        nint handle = _handle;
        if (handle == 0)
            throw new ObjectDisposedException(nameof(CefBrowser));

        OBSharpNative.InvokeUi(
            () => OBSharpNativeException.ThrowIfFailed((OBSharpResult)action(handle)));
    }
}
