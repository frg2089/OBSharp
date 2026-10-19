namespace OBSharp.Runtime;

/// <summary>
/// 插件列表（<c>plugins.ini</c>）里的一项：程序集路径、状态和已经加载的实例。
/// 由 <see cref="OBSharpPluginManager"/> 维护，外面只读。
/// </summary>
public sealed class OBSharpPluginEntry
{
    public string Identifier { get; }

    /// <summary>
    /// 插件程序集的绝对路径。
    /// </summary>
    public string AssemblyPath { get; }

    /// <summary>
    /// 插件名。加载成功时从插件身上抓一次存下来，之后不再问插件；没加载（停用、加载失败）时是 null。
    /// </summary>
    /// <remarks>
    /// 只有在插件加载成功时才会抓取 Name 和 Description，停用或加载失败时会清空。
    /// 插件名和描述只在加载成功时从插件身上抓一次存下来：它们是插件自己的属性，每次都去取的话，
    /// 恶意插件可以随取随改。没加载（停用、加载失败）时都清空，界面只显示路径。
    /// </remarks>
    public string? Name { get; private set; }

    /// <summary>
    /// 插件描述。和 <see cref="Name"/> 一样只在加载成功时抓一次，没加载时是 null。
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// 运行时状态。
    /// </summary>
    public OBSharpPluginStatus Status { get; private set; } = OBSharpPluginStatus.Disabled;

    /// <summary>
    /// 加载失败的原因，<see cref="Status"/> 是 <see cref="OBSharpPluginStatus.Failed"/> 时有值。
    /// </summary>
    public string? Error { get; private set; }


    /// <summary>
    /// 已经加载的插件实例，没启用时为 null。
    /// </summary>
    public ObsPlugin? Plugin { get; private set; }

    /// <summary>
    /// 插件列表里这一项是不是 <c>true</c>，启动加载时用一次就不再管了。
    /// </summary>
    internal bool StartEnabled { get; set; }

    /// <summary>
    /// 插件的加载上下文，停用时要卸掉它才能卸载程序集。
    /// </summary>
    internal OBSharpPluginLoadContext? LoadContext { get; private set; }


    internal OBSharpPluginEntry(string path, bool enabled)
    {
        Identifier = Path.GetFileNameWithoutExtension(path);
        AssemblyPath = path;
        StartEnabled = enabled;
    }

    // 下面这些由 OBSharpPluginManager 调用：加载到哪一步都要能被回滚，所以每一步都先挂进来。

    internal void SetLoadContext(OBSharpPluginLoadContext loadContext) => LoadContext = loadContext;

    internal void SetLoaded(ObsPlugin plugin, string name, string? description)
    {
        Plugin = plugin;
        Name = name;
        Description = description;
        Status = OBSharpPluginStatus.Enabled;
        Error = null;
    }

    internal void SetDisabled()
    {
        Plugin = null;
        LoadContext = null;
        Name = null;
        Description = null;
        Status = OBSharpPluginStatus.Disabled;
        Error = null;
    }

    internal void SetFailed(string error)
    {
        Plugin = null;
        LoadContext = null;
        Name = null;
        Description = null;
        Status = OBSharpPluginStatus.Failed;
        Error = error;
    }
}
