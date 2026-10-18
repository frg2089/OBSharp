using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 原生回调的落点。原生侧把 <c>user</c> 原样传回来，里面是
/// <see cref="GCHandle"/> 的整数形式；这份 <see cref="GCHandle"/> 由
/// <see cref="CefBrowser"/> 持有并负责释放。
/// </summary>
/// <remarks>
/// 抛出去的托管异常会直接穿回 C++ 栈（未定义行为），所以每个回调都自己吞掉异常，
/// 只写调试输出。
/// </remarks>
internal sealed unsafe class CallbackSink
{
    private readonly BrowserCallbacks _callbacks;

    internal CallbackSink(BrowserCallbacks callbacks) => _callbacks = callbacks;

    /// <summary>
    /// 一组函数指针；<c>User</c> 由调用方填。
    /// </summary>
    internal static OBSharpBrowserCallbacks Describe() => new()
    {
        OnTitleChanged = &OnTitleChanged,
        OnUrlChanged = &OnUrlChanged,
        OnLoadEnd = &OnLoadEnd,
        OnConsoleMessage = &OnConsoleMessage,
        OnMessage = &OnMessage,
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnTitleChanged(nint user, byte* title)
    {
        try
        {
            if (Resolve(user) is { } sink)
                sink._callbacks.TitleChanged?.Invoke(Utf8(title));
        }
        catch (Exception exception)
        {
            Swallow(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnUrlChanged(nint user, byte* url)
    {
        try
        {
            if (Resolve(user) is { } sink)
                sink._callbacks.UrlChanged?.Invoke(Utf8(url));
        }
        catch (Exception exception)
        {
            Swallow(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnLoadEnd(nint user, int httpStatusCode, int errorCode, byte* errorText)
    {
        try
        {
            if (Resolve(user) is { } sink)
                sink._callbacks.LoadEnd?.Invoke(httpStatusCode, errorCode, Utf8(errorText));
        }
        catch (Exception exception)
        {
            Swallow(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnConsoleMessage(nint user, int level, byte* message, byte* source, int line)
    {
        try
        {
            if (Resolve(user) is { } sink)
            {
                sink._callbacks.ConsoleMessage?.Invoke(
                    (OBSharpConsoleLevel)level, Utf8(message), Utf8(source), line);
            }
        }
        catch (Exception exception)
        {
            Swallow(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnMessage(nint user, byte* json)
    {
        try
        {
            if (Resolve(user) is { } sink)
                sink._callbacks.Message?.Invoke(Utf8(json));
        }
        catch (Exception exception)
        {
            Swallow(exception);
        }
    }

    private static CallbackSink? Resolve(nint user)
        => user == 0 ? null : GCHandle.FromIntPtr(user).Target as CallbackSink;

    private static string Utf8(byte* value)
        => value is null ? string.Empty : Marshal.PtrToStringUTF8((nint)value) ?? string.Empty;

    private static void Swallow(Exception exception)
    {
        Debug.WriteLine($"[OBSharp.Interop.Browser.Native] 回调抛出了异常，已忽略：{exception}");
    }
}
