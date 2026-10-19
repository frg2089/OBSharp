using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 窗口用的原生回调落点：同时固定窗口事件（<see cref="WindowCallbacks"/>）和里面浏览器的
/// 事件（<see cref="BrowserCallbacks"/>），随窗口一起释放。
/// </summary>
/// <remarks>
/// <para>
/// 原生侧把 <c>user</c> 原样传回来，里面是 <see cref="GCHandle"/> 的整数形式：窗口回调指向
/// 本对象，浏览器回调指向本对象持有的 <see cref="CallbackSink"/>。用强句柄（不是
/// <see cref="GCHandleType.Weak"/>）分配，所以只要句柄没释放，原生回调就一定能找到活对象；
/// 反过来说，释放句柄必须在原生窗口销毁之后（见 <see cref="CefDockWindow.Dispose"/>）。
/// </para>
/// <para>
/// 抛出去的托管异常会直接穿回 C++ 栈（未定义行为），所以每个回调都自己吞掉异常，
/// 只写调试输出。
/// </para>
/// </remarks>
internal sealed unsafe class WindowCallbackSink : IDisposable
{
    private readonly WindowCallbacks _window;
    private readonly CallbackSink _browser;
    private GCHandle _windowHandle;
    private GCHandle _browserHandle;

    private WindowCallbackSink(WindowCallbacks window, BrowserCallbacks browser)
    {
        _window = window;
        _browser = new CallbackSink(browser);
        _windowHandle = GCHandle.Alloc(this);
        _browserHandle = GCHandle.Alloc(_browser);
    }

    /// <summary>
    /// 用户点了工具窗口的关闭按钮（原生侧已经先把窗口隐藏了）。
    /// </summary>
    internal event Action? CloseRequested;

    /// <summary>
    /// 固定两套回调，返回由调用方负责 <see cref="Dispose"/> 的落点。
    /// </summary>
    internal static WindowCallbackSink Create(WindowCallbacks window, BrowserCallbacks browser)
        => new(window, browser);

    /// <summary>
    /// 窗口回调的函数指针组；<c>User</c> 已经填好。
    /// </summary>
    /// <param name="withCloseRequested">
    /// 是否带上关闭回调。停靠窗口的开关由 OBS 的 dock 决定，传 <see langword="false"/>
    /// 让原生侧收到全 0（否则它会为此记一条警告）。
    /// </param>
    internal OBSharpWindowCallbacks DescribeWindow(bool withCloseRequested = true) => new()
    {
        User = GCHandle.ToIntPtr(_windowHandle),
        OnCloseRequested = withCloseRequested ? &OnCloseRequested : null,
    };

    /// <summary>
    /// 浏览器回调的函数指针组；<c>User</c> 已经填好。
    /// </summary>
    internal OBSharpBrowserCallbacks DescribeBrowser()
    {
        OBSharpBrowserCallbacks callbacks = CallbackSink.Describe();
        callbacks.User = GCHandle.ToIntPtr(_browserHandle);
        return callbacks;
    }

    public void Dispose()
    {
        CloseRequested = null;

        if (_browserHandle.IsAllocated)
        {
            _browserHandle.Free();
            _browserHandle = default;
        }

        if (_windowHandle.IsAllocated)
        {
            _windowHandle.Free();
            _windowHandle = default;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnCloseRequested(nint user)
    {
        try
        {
            if (user == 0 || GCHandle.FromIntPtr(user).Target is not WindowCallbackSink sink)
                return;

            // 事件给托管侧订阅者，选项里的回调也给一遍：两处都能收到。
            sink.CloseRequested?.Invoke();
            sink._window.CloseRequested?.Invoke();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[OBSharp.Interop.Browser.Native] 窗口回调抛出了异常，已忽略：{exception}");
        }
    }
}
