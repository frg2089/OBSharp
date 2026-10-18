using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 一个 CEF 浏览器 Widget。底层是 obs-browser 的 <c>QCefWidget</c>，归这个对象所有。
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
/// </remarks>
public sealed unsafe class CefBrowser : IDisposable
{
    private nint _handle;
    private GCHandle _sink;

    private CefBrowser(nint handle, GCHandle sink)
    {
        _handle = handle;
        _sink = sink;
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
                handle = NativeMethods.BrowserCreate(in buffer.Options);
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

        return new CefBrowser(handle, sink);
    }

    /// <summary>
    /// 底层 <c>obsharp_browser*</c>。已释放时是 0。
    /// </summary>
    public nint Handle => _handle;

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
    /// 导航到 <paramref name="url"/>。
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
    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
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

        Dispose();
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
