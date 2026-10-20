using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 普通窗口的原生回调落点：固定 <see cref="WindowCallbacks"/>，随窗口一起释放。
/// </summary>
/// <remarks>
/// <para>
/// 原生侧把 <c>user</c> 原样传回来，里面是 <see cref="GCHandle"/> 的整数形式。用强句柄
/// （不是 <see cref="GCHandleType.Weak"/>）分配，所以只要句柄没释放，原生回调就一定能
/// 找到活对象；反过来说，释放句柄必须在原生窗口销毁之后（见 <see cref="CefWindow.Dispose"/>）。
/// </para>
/// <para>
/// 抛出去的托管异常会直接穿回 C++ 栈（未定义行为），所以每个回调都自己吞掉异常，
/// 只写调试输出。
/// </para>
/// </remarks>
internal sealed unsafe class WindowCallbackSink : IDisposable
{
    private readonly WindowCallbacks _window;
    private GCHandle _handle;

    private WindowCallbackSink(WindowCallbacks window)
    {
        _window = window;
        _handle = GCHandle.Alloc(this);
    }

    /// <summary>
    /// 用户点了窗口的关闭按钮（原生侧已经先把窗口隐藏了）。
    /// </summary>
    internal event Action? CloseRequested;

    /// <summary>
    /// 固定回调，返回由调用方负责 <see cref="Dispose"/> 的落点。
    /// </summary>
    internal static WindowCallbackSink Create(WindowCallbacks window) => new(window);

    /// <summary>
    /// 窗口回调的函数指针组；<c>User</c> 已经填好。
    /// </summary>
    internal OBSharpWindowCallbacks DescribeWindow() => new()
    {
        User = GCHandle.ToIntPtr(_handle),
        OnCloseRequested = &OnCloseRequested,
    };

    public void Dispose()
    {
        CloseRequested = null;

        if (_handle.IsAllocated)
        {
            _handle.Free();
            _handle = default;
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
