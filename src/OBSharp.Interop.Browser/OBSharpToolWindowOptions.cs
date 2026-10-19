using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// <c>obsharp_tool_window_options</c> 的内存布局。字段顺序、大小必须跟
/// <c>include/OBSharp.Interop.Browser.Native.h</c> 一字不差。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OBSharpToolWindowOptions
{
    public uint Size;

    /// <summary>窗口标题。</summary>
    public byte* Title;

    public int Width;
    public int Height;
    public int Visible;
    public int Topmost;
    public int Resizable;

    public OBSharpWindowCallbacks Callbacks;

    public OBSharpBrowserOptions Browser;
}
