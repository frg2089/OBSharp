using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 把托管选项里的字符串编成原生结构体需要的 UTF-8 非托管缓冲区：
/// <see cref="OBSharpBrowserOptions"/>、<see cref="OBSharpWindowOptions"/>。
/// </summary>
/// <remarks>
/// <para>
/// 原生 <c>obsharp_browser_create</c> / <c>obsharp_window_create</c> 返回前就会把
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
    /// <c>obsharp_window_options</c> 缓冲区；<see cref="CreateWindow"/> 用。
    /// </summary>
    public OBSharpWindowOptions WindowOptions;

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
    /// 建一个填好普通窗口参数的缓冲区。
    /// </summary>
    /// <remarks>
    /// 窗口是纯容器，参数里<b>没有</b>浏览器字段：页面另外用
    /// <see cref="CefBrowser"/> 建，再 set <see cref="CefWindow.Content"/>。
    /// </remarks>
    public static NativeOptionsBuffer CreateWindow(WindowOptions options, OBSharpWindowCallbacks windowCallbacks)
    {
        var buffer = new NativeOptionsBuffer
        {
            WindowOptions =
            {
                Size = (uint)sizeof(OBSharpWindowOptions),
                Width = options.Width,
                Height = options.Height,
                Visible = options.Visible ? 1 : 0,
                Topmost = options.Topmost ? 1 : 0,
                Resizable = options.Resizable ? 1 : 0,
                Callbacks = windowCallbacks,
            },
        };

        buffer.WindowOptions.Title = buffer.Alloc(options.Title);
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
