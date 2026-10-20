using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// <c>OBSharp.Interop.Browser.Native</c> 的托管入口：初始化、探测、以及 Qt 主线程调度。
/// </summary>
/// <remarks>
/// 原生库不能单独用：它复用 obs-browser 已经注册好的 CEF 和 OBS 自己带的 Qt6，
/// 所以只能跑在 OBS 进程（也就是 OB# 宿主所在的进程）里。
/// </remarks>
public static unsafe class OBSharpNative
{
    /// <summary>
    /// 编译托管绑定时使用的 ABI 版本。
    /// </summary>
    /// <remarks>
    /// 2 对应 host / 停靠窗口 / 工具窗口；原生库版本串是 <c>0.2.0</c>。
    /// </remarks>
    public const uint AbiVersion = 3;

    /// <summary>
    /// 解析 obs-browser、拿到面板接口，并开始等 CEF 就绪。幂等。
    /// </summary>
    /// <param name="logTag">
    /// 日志前缀，比如插件类型名；<see langword="null"/> 表示用默认的
    /// <c>OBSharp.Interop.Browser.Native</c>。
    /// </param>
    /// <returns>成功返回 <see cref="OBSharpResult.Ok"/>；进程里没有 obs-browser 时返回
    /// <see cref="OBSharpResult.BrowserUnavailable"/>。</returns>
    /// <exception cref="OBSharpNativeException">原生库的 ABI 版本对不上。</exception>
    public static OBSharpResult Initialize(string? logTag = null)
    {
        EnsureCompatible();
        return (OBSharpResult)NativeMethods.Initialize(logTag);
    }

    /// <summary>
    /// CEF 面板是否已经可以创建 Widget。
    /// </summary>
    public static bool IsAvailable => NativeMethods.IsAvailable() != 0;

    /// <summary>
    /// 封进原生库的版本串，形如 <c>0.2.0</c>。
    /// </summary>
    public static string Version
        => Marshal.PtrToStringUTF8((nint)NativeMethods.Version()) ?? string.Empty;

    /// <summary>
    /// 最近一次失败的描述；没有失败时是空串。
    /// </summary>
    public static string LastError
        => Marshal.PtrToStringUTF8((nint)NativeMethods.LastError()) ?? string.Empty;

    /// <summary>
    /// 当前线程是不是 Qt 主线程。
    /// </summary>
    public static bool IsUiThread() => NativeMethods.IsUiThread() != 0;

    /// <summary>
    /// 在 Qt 主线程上同步执行 <paramref name="action"/>。已经在主线程时直接执行。
    /// </summary>
    /// <exception cref="OBSharpNativeException">切不过去（主线程不可用）。</exception>
    public static void InvokeUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (NativeMethods.IsUiThread() != 0)
        {
            action();
            return;
        }

        var invocation = new UiInvocation { Action = action };
        GCHandle handle = GCHandle.Alloc(invocation);
        try
        {
            OBSharpNativeException.ThrowIfFailed(
                (OBSharpResult)NativeMethods.InvokeUi(&InvokeUiThunk, GCHandle.ToIntPtr(handle)));
        }
        finally
        {
            handle.Free();
        }

        // 在 Qt 主线程上抛的异常不能穿回原生栈，于是先存下来，回到这里再抛。
        if (invocation.Error is not null)
            ExceptionDispatchInfo.Capture(invocation.Error).Throw();
    }

    /// <summary>
    /// 探测 endpoint 是否可连接。
    /// </summary>
    /// <param name="endpoint">命名管道名 / UDS 路径。</param>
    /// <param name="endpointKind">解释方式，默认按平台推断。</param>
    /// <returns><see cref="OBSharpResult.Ok"/>、<see cref="OBSharpResult.Endpoint"/> 或
    /// <see cref="OBSharpResult.InvalidArgument"/>。</returns>
    public static OBSharpResult ProbeEndpoint(
        string endpoint, OBSharpEndpointKind endpointKind = OBSharpEndpointKind.Auto)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        return (OBSharpResult)NativeMethods.ProbeEndpoint(endpoint, (int)endpointKind);
    }

    internal static void EnsureCompatible()
    {
        uint version = NativeMethods.AbiVersion();
        if (version != AbiVersion)
        {
            throw new InvalidOperationException(
                $"OBSharp.Interop.Browser.Native 的 ABI 版本是 {version}，托管绑定期望 {AbiVersion}。" +
                "请把它们一起重新编译。");
        }
    }

    private sealed class UiInvocation
    {
        public Action? Action;
        public Exception? Error;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void InvokeUiThunk(nint user)
    {
        if (user == 0 || GCHandle.FromIntPtr(user).Target is not UiInvocation invocation)
            return;

        try
        {
            invocation.Action?.Invoke();
        }
        catch (Exception exception)
        {
            // 绝不能让异常穿回 C++ 栈。
            invocation.Error = exception;
        }
    }
}
