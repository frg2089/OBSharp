using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// <c>obsharp_window_callbacks</c> 的内存布局。函数指针用
/// <see cref="UnmanagedCallersOnlyAttribute"/> 静态方法取，<see cref="User"/> 放
/// <see cref="GCHandle"/> 的整数形式（原生侧只原样透传，不管生命周期）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OBSharpWindowCallbacks
{
    public nint User;

    /// <summary>
    /// 用户点了关闭（只有工具窗口会触发）。可以为 <see cref="nint.Zero"/>。
    /// </summary>
    public delegate* unmanaged[Cdecl]<nint, void> OnCloseRequested;
}
