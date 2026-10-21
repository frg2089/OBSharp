using OBSharp.Interop.Native;

namespace OBSharp.Interop.UI;

/// <summary>
/// 把调用方给的「父窗口」句柄解析成真正的 <c>QWidget*</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这段判断从旧原生库的 <c>ResolveParent()</c>（BrowserWidget.cpp）/ <c>ResolveFilePickerParent()</c>
/// （FilePicker.cpp）搬过来：以前它在 C++ 里，现在 ABI 只提供
/// <c>obsharp_widget_find()</c> 这种「一对一」的原语，「先按 OS 窗口 id 找、找不到再当成
/// <c>QWidget*</c> 用」这个判断归托管侧。
/// </para>
/// <para>
/// 为什么要兼容两种解释：OBS 的 <c>obs_frontend_get_main_window_handle()</c> 给的是 <c>HWND</c>，
/// 而插件自己的代码手里通常已经是某个 <c>QWidget*</c>（比如 <see cref="WebView.Widget"/>）。
/// 调用方不必知道自己拿的是哪一种。
/// </para>
/// </remarks>
internal static class ParentResolver
{
    /// <summary>
    /// 解析父窗口；<paramref name="parent"/> 为 0 时返回 0（调用方自己决定退回什么）。
    /// </summary>
    /// <param name="parent">HWND（Windows）或 <c>QWidget*</c>。</param>
    /// <returns>对应的 <c>QWidget*</c>；解析不出来时是 0。</returns>
    public static nint Resolve(nint parent)
    {
        if (parent is 0)
            return 0;

        // Windows 上允许直接传 HWND：QWidget::find 能找到它对应的顶层窗口。
        //
        // 只在 Windows 上试：其它平台 QWidget::find 的参数是 WId（X11 的 Window / Wayland 的
        // surface 指针），语义跟「用户随手传进来的指针」不一样，误用比不试更糟 —— 旧实现
        // 也是 #ifdef Q_OS_WIN 包起来的。
        if (OperatingSystem.IsWindows())
        {
            nint found = NativeMethods.obsharp_widget_find((nuint)parent);
            if (found is not 0)
                return found;
        }

        // 找不到就原样当 QWidget* 用。传错了会在原生的 vtable 调用上炸，这是旧实现就有的
        // 前提（调用方约定：要么 HWND，要么真 QWidget*）。
        return parent;
    }
}
