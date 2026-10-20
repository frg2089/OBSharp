namespace OBSharp;

/// <summary>
/// 宿主（OBSharp.Runtime）为每个插件准备好的环境信息，插件通过
/// <see cref="ObsPlugin.Context"/> 拿到它。插件不需要自己构造。
/// </summary>
internal sealed class ObsPluginContext(string identifier, string dataFolder)
{
    /// <summary>
    /// 插件标识符
    /// </summary>
    public string Identifier { get; } = identifier;

    /// <summary>
    /// 插件的数据目录 <c>&lt;OB# 数据目录&gt;/&lt;插件名&gt;</c>，附加到插件之前已经创建好。
    /// </summary>
    public string DataFolder { get; } = dataFolder;
}
