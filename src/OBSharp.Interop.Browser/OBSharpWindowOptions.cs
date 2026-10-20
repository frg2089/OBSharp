using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

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
internal unsafe struct OBSharpWindowOptions
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
}
