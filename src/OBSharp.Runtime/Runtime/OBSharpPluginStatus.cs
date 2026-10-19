namespace OBSharp.Runtime;

/// <summary>
/// 插件状态。
/// </summary>
internal enum OBSharpPluginStatus
{
    /// <summary>
    /// 没有启用：插件列表里是 <c>false</c>，或者被运行时停用了。
    /// </summary>
    Disabled,
    /// <summary>
    /// 已启用：程序集已经加载，插件已经构造并附加了上下文。
    /// </summary>
    Enabled,
    /// <summary>
    /// 启用了但加载失败，原因见 <see cref="OBSharpPluginEntry.Error"/>。
    /// </summary>
    Failed
}
