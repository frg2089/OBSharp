namespace OBSharp.Interop.Browser;

/// <summary>
/// 一次文件选择的结果。
/// </summary>
/// <remarks>
/// 用户取消时 <see cref="Canceled"/> 为 <see langword="true"/>，<see cref="Paths"/>
/// 是空数组；取消不是错误，不会抛异常。真正的失败由调用
/// <see cref="FilePicker"/> 的异步方法抛出 <see cref="OBSharpNativeException"/>。
/// </remarks>
public sealed class FilePickerResult
{
    /// <summary>
    /// 用户是否取消了对话框。对话框被外部销毁时也按取消上报。
    /// </summary>
    public bool Canceled { get; init; }

    /// <summary>
    /// 选中的路径，全部是绝对路径。取消时是空数组。
    /// </summary>
    public string[] Paths { get; init; } = [];

    /// <summary>
    /// 选中的第一个路径；没有选中时是 <see langword="null"/>。单选对话框直接用这个。
    /// </summary>
    public string? Path => Paths.Length > 0 ? Paths[0] : null;
}
