using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 把 <see cref="BrowserOptions"/> 里的托管字符串编成
/// <see cref="OBSharpBrowserOptions"/> 需要的 UTF-8 非托管缓冲区。
/// </summary>
/// <remarks>
/// 原生 <c>obsharp_browser_create</c> 返回前就会把字符串拷进自己的
/// <c>std::string</c>，所以 <see cref="Dispose"/> 可以在调用返回后立刻执行。
/// </remarks>
internal sealed unsafe class NativeOptionsBuffer : IDisposable
{
    private readonly List<nint> _allocations = [];

    public OBSharpBrowserOptions Options;

    public static NativeOptionsBuffer Create(BrowserOptions options, OBSharpBrowserCallbacks callbacks)
    {
        var buffer = new NativeOptionsBuffer
        {
            Options =
            {
                Size = (uint)sizeof(OBSharpBrowserOptions),
                Url = null,
                Scheme = null,
                Domain = null,
                Endpoint = null,
                EndpointKind = (int)options.EndpointKind,
                StartupScript = null,
                Transparent = options.Transparent ? 1 : 0,
                BackgroundColor = options.BackgroundColor,
                ParentWidget = (void*)options.ParentWidget,
                Callbacks = callbacks,
            },
        };

        buffer.Options.Url = buffer.Alloc(options.Url);
        buffer.Options.Scheme = buffer.Alloc(options.Scheme);
        buffer.Options.Domain = buffer.Alloc(options.Domain);
        buffer.Options.Endpoint = buffer.Alloc(options.Endpoint);
        buffer.Options.StartupScript = buffer.Alloc(options.StartupScript);
        return buffer;
    }

    public void Dispose()
    {
        foreach (nint pointer in _allocations)
            Marshal.FreeCoTaskMem(pointer);

        _allocations.Clear();
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
