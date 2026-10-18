using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// <c>obsharp_browser_callbacks</c> 的内存布局。函数指针用
/// <see cref="UnmanagedCallersOnlyAttribute"/> 静态方法取，<see cref="User"/> 放
/// <see cref="GCHandle"/> 的整数形式（原生侧只原样透传，不管生命周期）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OBSharpBrowserCallbacks
{
    public nint User;

    public delegate* unmanaged[Cdecl]<nint, byte*, void> OnTitleChanged;
    public delegate* unmanaged[Cdecl]<nint, byte*, void> OnUrlChanged;
    public delegate* unmanaged[Cdecl]<nint, int, int, byte*, void> OnLoadEnd;
    public delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, int, void> OnConsoleMessage;
    public delegate* unmanaged[Cdecl]<nint, byte*, void> OnMessage;
}
