namespace OBSharp.Interop.Browser;

/// <summary>
/// 文件选择对话框（打开文件 / 保存文件 / 选择文件夹）的参数。对应原生头文件里
/// 的 <c>obsharp_file_picker_options</c>。
/// </summary>
/// <remarks>
/// 三种对话框共用一份参数，不适用的字段会被原生侧忽略：选择文件夹时不看
/// <see cref="FileName"/> / <see cref="Filters"/> / <see cref="DefaultSuffix"/>，
/// 打开文件时不看 <see cref="DefaultSuffix"/>。
/// </remarks>
public sealed class FilePickerOptions
{
    /// <summary>
    /// 对话框标题。为 <see langword="null"/> 时用 Qt 的默认标题。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 初始目录。为 <see langword="null"/> 时由 Qt 决定（通常是上次访问的目录）。
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// 初始文件名（打开 / 保存）。为 <see langword="null"/> 表示不预填。
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// 文件过滤器。每项的写法是 <c>"显示名|模式,模式"</c>，例如：
    /// <code>
    /// Filters = ["图片|*.png,*.jpg", "所有文件|*"]
    /// </code>
    /// 托管侧会拼成 Qt 的 <c>"图片 (*.png *.jpg);;所有文件 (*)"</c>。
    /// 为 <see langword="null"/> / 空数组时显示所有文件。
    /// </summary>
    public string[]? Filters { get; init; }

    /// <summary>
    /// 保存时自动补的后缀，<b>不带点</b>（<c>"txt"</c>、<c>"json"</c>）。
    /// 为 <see langword="null"/> / 空串时表示不补。
    /// </summary>
    public string? DefaultSuffix { get; init; }

    /// <summary>
    /// 是否显示隐藏文件 / 系统文件。默认不显示。
    /// </summary>
    public bool ShowHiddenFiles { get; init; }

    /// <summary>
    /// 对话框的模态父窗口，通常是 <see cref="WebView.Widget"/> 或
    /// <see cref="Window.Widget"/>。为 0 时用当前活动窗口。
    /// </summary>
    /// <remarks>
    /// 跟 <see cref="WebViewOptions.ParentWidget"/> 一样，Windows 上也可以传 HWND。
    /// 对话框显示期间要保持这个窗口有效（异步调用时尤其注意别提前 Dispose）。
    /// </remarks>
    public nint ParentWidget { get; init; }
}
