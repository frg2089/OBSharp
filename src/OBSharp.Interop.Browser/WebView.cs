
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using Microsoft.Extensions.Logging;

namespace OBSharp.Interop.Browser;

public partial class WebView : IDisposable
{
    private readonly GCHandle _handle;
    private bool _disposedValue;

    protected ILogger<WebView> Logger { get; }

    /// <summary>
    /// 底层 <c>obsharp_browser*</c>。已释放时是 0。
    /// </summary>
    public nint Handle { get; }

    /// <summary>
    /// 底层 <c>QWidget*</c>。可以直接 <c>setParent</c> / 放进 dock，但不要自己销毁。
    /// </summary>
    public nint Widget => obsharp_browser_widget(Handle);

    /// <summary>
    /// 当前文档 URL；还没加载时是空串。
    /// </summary>
    public string CurrentUrl => obsharp_browser_current_url(Handle);

    /// <summary>
    /// 页面标题变化。
    /// </summary>
    public event EventHandler<TitleChangedEventArgs>? TitleChanged;

    /// <summary>
    /// 页面 URL 变化。
    /// </summary>
    public event EventHandler<UrlChangedEventArgs>? UrlChanged;

    /// <summary>
    /// 一次主文档加载结束。<see cref="LoadEndEventArgs.ErrorCode"/> 为 0 表示成功。
    /// </summary>
    /// <remarks>
    /// obs-browser 的面板接口没有暴露 CefLoadHandler，这个事件是页面里的引导脚本
    /// 报告 <c>load</c> 事件得到的，因此 <see cref="LoadEndEventArgs.HttpStatusCode"/> 恒为 200、
    /// <see cref="LoadEndEventArgs.ErrorCode"/> 恒为 0，只在注册了 <c>endpoint</c> 时才有。
    /// </remarks>
    public event EventHandler<LoadEndEventArgs>? LoadEnd;

    /// <summary>
    /// 页面的 <c>console.*</c> 消息。同样来自引导脚本，<c>source</c> 是当前文档
    /// URL、<c>line</c> 恒为 0。
    /// </summary>
    public event EventHandler<ConsoleMessageEventArgs>? ConsoleMessage;

    /// <summary>
    /// 页面通过 <c>window.obsharp.post(json)</c> 发来的消息，<c>json</c> 是原文。
    /// </summary>
    public event EventHandler<MessageEventArgs>? Message;

    /// <summary>
    /// 创建浏览器。失败时抛 <see cref="OBSharpNativeException"/>。
    /// </summary>
    /// <param name="options">创建参数。</param>
    /// <param name="logger">日志记录器。</param>
    public unsafe WebView(WebViewOptions options, ILogger<WebView> logger)
    {
        Logger = logger;
        _handle = GCHandle.Alloc(this);
        NativeBrowserOptions nativeOptions = new()
        {
            Size = (uint)sizeof(NativeBrowserOptions),

            Url = (byte*)Marshal.StringToCoTaskMemUTF8(options.Url),
            Scheme = (byte*)Marshal.StringToCoTaskMemUTF8(options.Scheme),
            Domain = (byte*)Marshal.StringToCoTaskMemUTF8(options.Domain),
            Endpoint = (byte*)Marshal.StringToCoTaskMemUTF8(options.Endpoint),
            EndpointKind = (int)options.EndpointKind,
            StartupScript = (byte*)Marshal.StringToCoTaskMemUTF8(options.StartupScript),
            Transparent = options.Transparent ? 1 : 0,
            BackgroundColor = options.BackgroundColor,
            ParentWidget = options.ParentWidget,
            Callbacks = new()
            {
                User = GCHandle.ToIntPtr(_handle),
                OnTitleChanged = &OnTitleChanged,
                OnUrlChanged = &OnUrlChanged,
                OnLoadEnd = &OnLoadEnd,
                OnConsoleMessage = &OnConsoleMessage,
                OnMessage = &OnMessage,
            },
            Host = (byte*)Marshal.StringToCoTaskMemUTF8(options.Host),
        };
        Handle = obsharp_browser_create(nativeOptions);
    }

    /// <summary>
    /// 导航到 <paramref name="url"/>。
    /// </summary>
    public int Load(string? url) => obsharp_browser_load(Handle, url);

    /// <summary>
    /// 忽略缓存重新加载。
    /// </summary>
    public int Reload() => obsharp_browser_reload(Handle);

    /// <summary>
    /// 缩放：小于 0 缩小，大于 0 放大，等于 0 复位。
    /// </summary>
    public int Zoom(int direction) => obsharp_browser_zoom(Handle, direction);

    /// <summary>
    /// 在当前文档里执行一段 JavaScript。
    /// </summary>
    public int ExecuteScript(string? script) => obsharp_browser_execute_script(Handle, script);

    /// <summary>
    /// 设置每个页面文档创建前注入的脚本。注意这会整体替换原生侧拼好的引导脚本
    /// （<c>window.obsharp</c>），所以一般用 <see cref="WebViewOptions.StartupScript"/>。
    /// </summary>
    public int SetStartupScript(string? script) => obsharp_browser_set_startup_script(Handle, script);

    /// <summary>
    /// 是否允许弹出窗口。
    /// </summary>
    public int AllowPopups(bool allow) => obsharp_browser_allow_popups(Handle, allow ? 1 : 0);

    /// <summary>
    /// 调整 Widget 大小。
    /// </summary>
    public int Resize(int width, int height) => obsharp_browser_resize(Handle, width, height);

    /// <summary>
    /// 移动 Widget。
    /// </summary>
    public int Move(int x, int y) => obsharp_browser_move(Handle, x, y);

    /// <summary>
    /// 显示 / 隐藏。第一次显示会真正创建 CEF 浏览器。
    /// </summary>
    public int SetVisible(bool visible) => obsharp_browser_set_visible(Handle, visible ? 1 : 0);

    /// <summary>
    /// 给页面发一条消息：页面里会派发 <c>CustomEvent("obsharp-message")</c>，
    /// <c>detail</c> 是解析后的 JSON。
    /// </summary>
    public int PostMessage(string? json) => obsharp_browser_post_message(Handle, json);

    #region Native Methods
    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial nint obsharp_browser_create(in NativeBrowserOptions options);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_destroy")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void obsharp_browser_destroy(nint browser);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_widget")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial nint obsharp_browser_widget(nint browser);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_load",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_load(nint browser, string? url);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_reload")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_reload(nint browser);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_zoom")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_zoom(nint browser, int direction);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_execute_script",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_execute_script(nint browser, string? script);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_set_startup_script",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_set_startup_script(nint browser, string? script);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_allow_popups")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_allow_popups(nint browser, int allow);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_resize")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_resize(nint browser, int width, int height);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_move")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_move(nint browser, int x, int y);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_set_visible")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_set_visible(nint browser, int visible);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_post_message",
                   StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_browser_post_message(nint browser, string? json);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_current_url")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalUsing(typeof(OBSStringMarshaller))]
    private static partial string obsharp_browser_current_url(nint browser);
    #endregion

    #region Event Forwarder    
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnTitleChanged(nint handle, byte* title)
    {
        if (GCHandle.FromIntPtr(handle).Target is not WebView browser)
            return;

        try
        {
            browser.TitleChanged?.Invoke(browser, new(Marshal.PtrToStringUTF8((nint)title)));
        }
        catch (Exception ex)
        {
            LogException(browser.Logger, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnUrlChanged(nint handle, byte* url)
    {
        if (GCHandle.FromIntPtr(handle).Target is not WebView browser)
            return;

        try
        {
            browser.UrlChanged?.Invoke(browser, new(Marshal.PtrToStringUTF8((nint)url)));
        }
        catch (Exception ex)
        {
            LogException(browser.Logger, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnLoadEnd(nint handle, int errorCode, int httpStatusCode, byte* url)
    {
        if (GCHandle.FromIntPtr(handle).Target is not WebView browser)
            return;

        try
        {
            browser.LoadEnd?.Invoke(browser, new(errorCode, httpStatusCode, Marshal.PtrToStringUTF8((nint)url)));
        }
        catch (Exception ex)
        {
            LogException(browser.Logger, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnConsoleMessage(nint handle, int level, byte* message, byte* source, int line)
    {
        if (GCHandle.FromIntPtr(handle).Target is not WebView browser)
            return;

        try
        {
            var lvl = (OBSharpConsoleLevel)level;
            var msg = Marshal.PtrToStringUTF8((nint)message);
            var src = Marshal.PtrToStringUTF8((nint)source);
            var logLevel = lvl switch
            {
                OBSharpConsoleLevel.Verbose => LogLevel.Debug,
                OBSharpConsoleLevel.Info => LogLevel.Information,
                OBSharpConsoleLevel.Warning => LogLevel.Warning,
                OBSharpConsoleLevel.Error => LogLevel.Error,
                OBSharpConsoleLevel.Fatal => LogLevel.Critical,
                _ => LogLevel.None,
            };
            if (browser.Logger.IsEnabled(logLevel))
                browser.Logger.Log(logLevel, "{Level}: {Message} (Source: {Source}, Line: {Line})", lvl, msg, src, line);

            browser.ConsoleMessage?.Invoke(browser, new(lvl, msg, src, line));

        }
        catch (Exception ex)
        {
            LogException(browser.Logger, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnMessage(nint handle, byte* json)
    {
        if (GCHandle.FromIntPtr(handle).Target is not WebView browser)
            return;

        try
        {
            browser.Message?.Invoke(browser, new(Marshal.PtrToStringUTF8((nint)json)));
        }
        catch (Exception ex)
        {
            LogException(browser.Logger, ex);
        }
    }
    #endregion

    [LoggerMessage(LogLevel.Error, "An exception occurred in the browser callback.")]
    private static partial void LogException(ILogger logger, Exception ex);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
            return;

        if (disposing)
        {
            _handle.Free();
        }

        if (Handle is not 0)
            obsharp_browser_destroy(Handle);
        _disposedValue = true;
    }

    ~WebView()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <c>obsharp_browser_options</c> 的内存布局。字段顺序、大小必须跟
    /// <c>include/OBSharp.Interop.Browser.Native.h</c> 一字不差。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeBrowserOptions
    {
        public uint Size;

        public byte* Url;
        public byte* Scheme;
        public byte* Domain;
        public byte* Endpoint;
        public int EndpointKind;
        public byte* StartupScript;
        public int Transparent;
        public uint BackgroundColor;
        public nint ParentWidget;

        public NativeBrowserCallbacks Callbacks;

        /// <summary>
        /// OB# host 名（v2 新增，可 <see langword="null"/>）。非空时按 host 推导端点、
        /// 映射 <c>obsharp://&lt;host&gt;/...</c> 并注册该 host 的 scheme 工厂。
        /// </summary>
        public byte* Host;
    }
    /// <summary>
    /// <c>obsharp_browser_callbacks</c> 的内存布局。函数指针用
    /// <see cref="UnmanagedCallersOnlyAttribute"/> 静态方法取，<see cref="User"/> 放
    /// <see cref="GCHandle"/> 的整数形式（原生侧只原样透传，不管生命周期）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeBrowserCallbacks
    {
        public nint User;

        public delegate* unmanaged[Cdecl]<nint, byte*, void> OnTitleChanged;
        public delegate* unmanaged[Cdecl]<nint, byte*, void> OnUrlChanged;
        public delegate* unmanaged[Cdecl]<nint, int, int, byte*, void> OnLoadEnd;
        public delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, int, void> OnConsoleMessage;
        public delegate* unmanaged[Cdecl]<nint, byte*, void> OnMessage;
    }
}
