using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OBSharp.Interop.Browser;

internal static partial class Internal
{
    /// <summary>
    /// CEF 面板是否已经可以创建 Widget。
    /// </summary>
    public static bool IsAvailable => obsharp_browser_native_is_available() is not 0;

    /// <summary>
    /// 原生库的版本串
    /// </summary>
    public static string Version => obsharp_browser_native_version();

    /// <summary>
    /// 当前线程是不是 Qt 主线程。
    /// </summary>
    public static bool IsUIThread => obsharp_browser_native_is_ui_thread() is not 0;

    public static void Initialize(string? logTag = null)
    {
        var result = obsharp_browser_native_initialize(logTag);
        OBSharpNativeException.ThrowIfFailed(result);
    }

    public static unsafe void InvokeUI(Action action)
    {
        if (IsUIThread)
        {
            action();
            return;
        }

        UiInvocation invocation = new()
        {
            Action = action,
        };

        GCHandle handle = GCHandle.Alloc(invocation);
        try
        {
            var result = obsharp_browser_native_invoke_ui(&NativeInvokeUI, GCHandle.ToIntPtr(handle));
            OBSharpNativeException.ThrowIfFailed(result);
        }
        finally
        {
            handle.Free();
        }

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
    public static void ProbeEndpoint(string endpoint, OBSharpEndpointKind endpointKind = OBSharpEndpointKind.Auto)
    {
        var result = obsharp_browser_native_probe_endpoint(endpoint, (int)endpointKind);
        OBSharpNativeException.ThrowIfFailed(result);
    }

    #region Native Methods
    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_abi_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial uint obsharp_browser_native_abi_version();

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalUsing(typeof(OBSStringMarshaller))]
    private static partial string obsharp_browser_native_version();

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_initialize",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_native_initialize(string? logTag);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_is_available")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_native_is_available();

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_is_ui_thread")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_native_is_ui_thread();

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_invoke_ui")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int obsharp_browser_native_invoke_ui(delegate* unmanaged[Cdecl]<nint, void> fn, nint user);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_probe_endpoint",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_native_probe_endpoint(string? endpoint, int endpointKind);
    #endregion

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void NativeInvokeUI(nint handle)
    {
        if (GCHandle.FromIntPtr(handle).Target is not UiInvocation invocation)
            return;

        try
        {
            invocation.Action?.Invoke();
        }
        catch (Exception exception)
        {
            invocation.Error = exception;
        }
    }
}

file sealed class UiInvocation
{
    public required Action Action { get; init; }
    public Exception? Error { get; set; }
}
