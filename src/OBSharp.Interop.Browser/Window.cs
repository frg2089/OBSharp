
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

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
public partial class Window : IDisposable
{
    private readonly nint _native;
    private readonly GCHandle _handle;
    private bool _disposedValue;
    protected ILogger Logger { get; }

    /// <summary>
    /// 当前内容的 <c>QWidget*</c>；没设内容时是窗口自己。
    /// </summary>
    public nint Widget => obsharp_window_widget(_native);

    /// <summary>
    /// 是否可见。
    /// </summary>
    public bool IsVisible => obsharp_window_is_visible(_native) is not 0;

    /// <summary>
    /// 用户点了窗口的关闭按钮：原生侧已经把窗口隐藏了。
    /// </summary>
    /// <remarks>
    /// 在 Qt 主线程上触发
    /// </remarks>
    public event EventHandler? CloseRequested;

    public unsafe Window(WindowOptions options, ILogger logger)
    {
        Logger = logger;
        _handle = GCHandle.Alloc(this);
        NativeWindowOptions nativeOptions = new()
        {
            Size = (uint)sizeof(NativeWindowOptions),
            Title = (byte*)Marshal.StringToCoTaskMemUTF8(options.Title),
            Width = options.Width,
            Height = options.Height,
            Visible = options.Visible ? 1 : 0,
            Topmost = options.Topmost ? 1 : 0,
            Resizable = options.Resizable ? 1 : 0,
            Callbacks = new()
            {
                User = GCHandle.ToIntPtr(_handle),
                OnCloseRequested = &OnCloseRequested,
            },
        };
        _native = obsharp_window_create(nativeOptions);
    }

    /// <summary>
    /// 把内容放进窗口（通常是 <see cref="WebView.Widget"/>）。传 0 清空。
    /// </summary>
    public int SetContent(nint content) => obsharp_window_set_content(_native, content);

    /// <summary>
    /// 显示窗口。
    /// </summary>
    public virtual int Show() => obsharp_window_show(_native);

    /// <summary>
    /// 隐藏窗口。
    /// </summary>
    public virtual int Hide() => obsharp_window_hide(_native);

    /// <summary>
    /// 设置窗口标题。
    /// </summary>
    public int SetTitle(string? title) => obsharp_window_set_title(_native, title);

    /// <summary>
    /// 设置窗口大小。
    /// </summary>
    public int SetSize(int width, int height) => obsharp_window_set_size(_native, width, height);

    #region Native Methods    
    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial nint obsharp_window_create(in NativeWindowOptions options);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_destroy")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void obsharp_window_destroy(nint window);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_widget")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial nint obsharp_window_widget(nint window);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_set_content")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_window_set_content(nint window, nint content);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_show")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_window_show(nint window);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_hide")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_window_hide(nint window);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_is_visible")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_window_is_visible(nint window);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_set_title",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_window_set_title(nint window, string? title);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_window_set_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_window_set_size(nint window, int width, int height);

    #endregion


    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnCloseRequested(nint handle)
    {
        if (GCHandle.FromIntPtr(handle).Target is not Window window)
            return;

        try
        {
            window.CloseRequested?.Invoke(window, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogException(window.Logger, ex);
        }
    }

    [LoggerMessage(LogLevel.Error, "An exception occurred in the window callback.")]
    private static partial void LogException(ILogger logger, Exception ex);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
            return;

        if (disposing)
        {
            _handle.Free();
        }

        if (_native is not 0)
            obsharp_window_destroy(_native);
        _disposedValue = true;
    }

    ~Window()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <c>obsharp_window_options</c> 的内存布局。字段顺序、大小必须跟
    /// <c>include/OBSharp.Interop.Browser.Native.h</c> 一字不差。
    /// </summary>
    /// <remarks>
    /// 这是<b>纯 Qt 容器</b>的参数：它不描述页面、也不认识 host / URL。浏览器是另外建的
    /// （<see cref="CefBrowser"/>），建好之后用
    /// <see cref="CefWindow.Content"/> 把 <see cref="CefBrowser.Widget"/> 放进来。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeWindowOptions
    {
        public uint Size;

        /// <summary>
        /// 窗口标题。
        /// </summary>
        public byte* Title;

        public int Width;
        public int Height;
        public int Visible;
        public int Topmost;
        public int Resizable;

        public NativeWindowCallbacks Callbacks;
    }

    /// <summary>
    /// <c>obsharp_window_callbacks</c> 的内存布局。函数指针用
    /// <see cref="UnmanagedCallersOnlyAttribute"/> 静态方法取，<see cref="User"/> 放
    /// <see cref="GCHandle"/> 的整数形式（原生侧只原样透传，不管生命周期）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeWindowCallbacks
    {
        public nint User;

        /// <summary>
        /// 用户点了关闭（只有工具窗口会触发）。可以为 <see cref="nint.Zero"/>。
        /// </summary>
        public delegate* unmanaged[Cdecl]<nint, void> OnCloseRequested;
    }
}
