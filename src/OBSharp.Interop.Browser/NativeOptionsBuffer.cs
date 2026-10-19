using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 把托管选项里的字符串编成原生结构体需要的 UTF-8 非托管缓冲区：
/// <see cref="OBSharpBrowserOptions"/>、<see cref="OBSharpDockWindowOptions"/>、
/// <see cref="OBSharpToolWindowOptions"/>。
/// </summary>
/// <remarks>
/// <para>
/// 原生 <c>obsharp_browser_create</c> / <c>obsharp_*_window_create</c> 返回前就会把
/// 字符串拷进自己的 <c>std::string</c>，所以 <see cref="Dispose"/> 可以在调用返回后
/// 立刻执行。
/// </para>
/// <para>
/// 结构体的 <c>size</c> 字段一律填成本次编译时的大小：原生侧会拒绝比它小的
/// <c>size</c>，将来加字段时旧调用方也还能用。
/// </para>
/// </remarks>
internal sealed unsafe class NativeOptionsBuffer : IDisposable
{
    private readonly List<nint> _allocations = [];

    /// <summary>
    /// <c>obsharp_browser_options</c> 缓冲区；<see cref="Create(BrowserOptions, OBSharpBrowserCallbacks)"/> 用。
    /// </summary>
    public OBSharpBrowserOptions Browser;

    /// <summary>
    /// <c>obsharp_dock_window_options</c> 缓冲区；<see cref="CreateDock"/> 用。
    /// </summary>
    public OBSharpDockWindowOptions DockOptions;

    /// <summary>
    /// <c>obsharp_tool_window_options</c> 缓冲区；<see cref="CreateTool"/> 用。
    /// </summary>
    public OBSharpToolWindowOptions ToolOptions;

    /// <summary>
    /// 建一个只填浏览器参数的缓冲区。
    /// </summary>
    public static NativeOptionsBuffer Create(BrowserOptions options, OBSharpBrowserCallbacks callbacks)
    {
        var buffer = new NativeOptionsBuffer();
        buffer.FillBrowser(ref buffer.Browser, options, callbacks);
        return buffer;
    }

    /// <summary>
    /// 建一个填好停靠窗口参数的缓冲区（浏览器参数在 <see cref="DockOptions"/> 里）。
    /// </summary>
    public static NativeOptionsBuffer CreateDock(
        DockWindowOptions options, OBSharpBrowserCallbacks browserCallbacks, OBSharpWindowCallbacks windowCallbacks)
    {
        var buffer = new NativeOptionsBuffer
        {
            DockOptions =
            {
                Size = (uint)sizeof(OBSharpDockWindowOptions),
                Visible = options.Visible ? 1 : 0,
                Width = options.Width,
                Height = options.Height,
                Callbacks = windowCallbacks,
            },
        };

        buffer.DockOptions.Id = buffer.Alloc(options.Id);
        buffer.DockOptions.Title = buffer.Alloc(options.Title);
        buffer.FillBrowser(ref buffer.DockOptions.Browser, options.Browser, browserCallbacks);
        return buffer;
    }

    /// <summary>
    /// 建一个填好工具窗口参数的缓冲区（浏览器参数在 <see cref="ToolOptions"/> 里）。
    /// </summary>
    public static NativeOptionsBuffer CreateTool(
        ToolWindowOptions options, OBSharpBrowserCallbacks browserCallbacks, OBSharpWindowCallbacks windowCallbacks)
    {
        var buffer = new NativeOptionsBuffer
        {
            ToolOptions =
            {
                Size = (uint)sizeof(OBSharpToolWindowOptions),
                Width = options.Width,
                Height = options.Height,
                Visible = options.Visible ? 1 : 0,
                Topmost = options.Topmost ? 1 : 0,
                Resizable = options.Resizable ? 1 : 0,
                Callbacks = windowCallbacks,
            },
        };

        buffer.ToolOptions.Title = buffer.Alloc(options.Title);
        buffer.FillBrowser(ref buffer.ToolOptions.Browser, options.Browser, browserCallbacks);
        return buffer;
    }

    public void Dispose()
    {
        foreach (nint pointer in _allocations)
            Marshal.FreeCoTaskMem(pointer);

        _allocations.Clear();
    }

    private void FillBrowser(ref OBSharpBrowserOptions target, BrowserOptions options,
        OBSharpBrowserCallbacks callbacks)
    {
        target.Size = (uint)sizeof(OBSharpBrowserOptions);
        target.EndpointKind = (int)options.EndpointKind;
        target.Transparent = options.Transparent ? 1 : 0;
        target.BackgroundColor = options.BackgroundColor;
        target.ParentWidget = (void*)options.ParentWidget;
        target.Callbacks = callbacks;

        target.Url = Alloc(options.Url);
        target.Scheme = Alloc(options.Scheme);
        target.Domain = Alloc(options.Domain);
        target.Endpoint = Alloc(options.Endpoint);
        target.StartupScript = Alloc(options.StartupScript);
        target.Host = Alloc(options.Host);
    }

    private byte* Alloc(string? value)
    {
        if (value is null)
            return null;

        nint pointer = Marshal.StringToCoTaskMemUTF8(value);
        _allocations.Add(pointer);
        return (byte*)pointer;
    }
}
