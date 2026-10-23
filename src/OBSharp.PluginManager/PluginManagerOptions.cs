namespace OBSharp.PluginManager;

/// <summary>
/// 插件管理器的配置节（插件 <c>appsettings.json</c> 的 <c>PluginManager</c> 节），
/// 经 <c>IOptions</c> 注入，谁用谁读。
/// </summary>
public sealed class PluginManagerOptions
{
    /// <summary>
    /// 打开管理器窗口时浏览器就绪后立刻自动弹 DevTools（调试用，与 CEF 右键菜单
    /// 「检查」同一条通路）。抓「只在首次打开时出现」的问题时把它打开。
    /// </summary>
    public bool DevTools { get; set; }
}
