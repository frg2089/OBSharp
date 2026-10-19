using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// <c>obsharp_dock_window_options</c> 的内存布局。字段顺序、大小必须跟
/// <c>include/OBSharp.Interop.Browser.Native.h</c> 一字不差。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OBSharpDockWindowOptions
{
    public uint Size;

    /// <summary>OBS 停靠面板 id（也是 QDockWidget 的 objectName）。</summary>
    public byte* Id;

    /// <summary>面板标题。</summary>
    public byte* Title;

    public int Visible;
    public int Width;
    public int Height;

    public OBSharpWindowCallbacks Callbacks;

    public OBSharpBrowserOptions Browser;
}
