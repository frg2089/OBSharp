using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// <c>OBSharp.Interop.Browser.Native</c> 的裸绑定，一一对应
/// <c>src/OBSharp.Interop.Browser.Native/include/OBSharp.Interop.Browser.Native.h</c>。
/// 不要直接用它——用 <see cref="OBSharpNative"/> / <see cref="CefBrowser"/> /
/// <see cref="CefWindow"/>。
/// </summary>
internal static unsafe partial class NativeMethods
{
    static NativeMethods() => NativeLibraryLoader.EnsureRegistered();

    // ---- 版本与初始化 -----------------------------------------------------

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_abi_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial uint AbiVersion();

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial byte* Version();

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_initialize",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int Initialize(string? logTag);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_is_available")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int IsAvailable();

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_last_error")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial byte* LastError();

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_is_ui_thread")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int IsUiThread();

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_invoke_ui")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int InvokeUi(delegate* unmanaged[Cdecl]<nint, void> fn, nint user);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_native_probe_endpoint",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int ProbeEndpoint(string? endpoint, int endpointKind);

    // ---- 浏览器生命周期 ---------------------------------------------------

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint BrowserCreate(in OBSharpBrowserOptions options);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_destroy")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void BrowserDestroy(nint browser);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_widget")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint BrowserWidget(nint browser);

    // ---- 浏览器操作 -------------------------------------------------------

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_load",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserLoad(nint browser, string? url);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_reload")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserReload(nint browser);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_zoom")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserZoom(nint browser, int direction);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_execute_script",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserExecuteScript(nint browser, string? script);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_set_startup_script",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserSetStartupScript(nint browser, string? script);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_allow_popups")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserAllowPopups(nint browser, int allow);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_resize")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserResize(nint browser, int width, int height);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_move")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserMove(nint browser, int x, int y);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_set_visible")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserSetVisible(nint browser, int visible);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_post_message",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int BrowserPostMessage(nint browser, string? json);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_browser_current_url")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial byte* BrowserCurrentUrl(nint browser);

    // ---- 普通顶层窗口（纯容器） -------------------------------------------

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint WindowCreate(in OBSharpWindowOptions options);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_destroy")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void WindowDestroy(nint window);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_widget")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint WindowWidget(nint window);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_set_content")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WindowSetContent(nint window, nint content);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_show")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WindowShow(nint window);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_hide")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WindowHide(nint window);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_is_visible")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WindowIsVisible(nint window);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_set_title",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WindowSetTitle(nint window, string? title);

    [LibraryImport(NativeLibraryLoader.LibraryName, EntryPoint = "obsharp_window_set_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WindowSetSize(nint window, int width, int height);
}
