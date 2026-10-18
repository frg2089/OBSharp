namespace OBSharp;

/// <summary>
/// 插件数据目录的提供者。由宿主实现，插件也可以直接注入它。
/// </summary>
public interface IDataFolderProvider
{
    /// <summary>
    /// 拿到 <c>&lt;OB# 数据目录&gt;/&lt;name&gt;</c>；<paramref name="name"/> 不是合法目录名时抛 <see cref="ArgumentException"/>。
    /// </summary>
    string GetDataFolder(string name);
}
