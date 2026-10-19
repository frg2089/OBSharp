using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// <c>obsharp_browser_options</c> 的内存布局。字段顺序、大小必须跟
/// <c>include/OBSharp.Interop.Browser.Native.h</c> 一字不差。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OBSharpBrowserOptions
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
    public void* ParentWidget;

    public OBSharpBrowserCallbacks Callbacks;

    /// <summary>
    /// OB# host 名（v2 新增，可 <see langword="null"/>）。非空时按 host 推导端点、
    /// 映射 <c>obsharp://&lt;host&gt;/...</c> 并注册该 host 的 scheme 工厂。
    /// </summary>
    public byte* Host;
}
