using Microsoft.Extensions.Logging;

namespace OBSharp;

/// <summary>
/// 插件基类。一个插件程序集里恰好实现一个子类，宿主（OBSharp.Runtime）加载程序集之后
/// 用无参构造函数构造它，再附加 <see cref="Context"/>，按 <see cref="Load"/>、
/// <see cref="PostLoad"/>、<see cref="Unload"/> 的顺序调用。
/// </summary>
public abstract class ObsPlugin
{
    private ObsPluginContext? _context;

    /// <summary>
    /// 插件名。默认取插件程序集名（不含扩展名），同时用作数据目录名。
    /// </summary>
    public virtual string Name
    {
        get
        {
            var type = GetType();
            return type.Assembly.GetName().Name ?? type.Namespace ?? type.Name;
        }
    }

    /// <summary>
    /// 插件描述，默认 null。
    /// </summary>
    public virtual string? Description => null;

    /// <summary>
    /// 宿主附加的环境信息。只有加载开始之后（<see cref="Load"/> 及以后）才可用。
    /// </summary>
    public ObsPluginContext Context => _context
        ?? throw new InvalidOperationException($"Plugin \"{GetType()}\" has not been attached by a host yet.");

    /// <summary>
    /// 插件的数据目录，等于 <see cref="ObsPluginContext.DataFolder"/>。
    /// </summary>
    public string DataFolder => Context.DataFolder;

    /// <summary>
    /// 插件自己的日志，等于 <see cref="ObsPluginContext.Logger"/>。
    /// </summary>
    public ILogger Logger => Context.Logger;

    /// <summary>
    /// 读取插件自己的本地化字符串，见 <see cref="ObsPluginContext.GetText"/>。
    /// </summary>
    public string GetText(string key) => Context.GetText(key);

    /// <summary>
    /// 加载插件。返回 false 表示加载失败：宿主会记一条警告，然后回滚这个插件
    /// （调 <see cref="Unload"/>、释放加载上下文），所以这里没成功就别留下半截状态。
    /// </summary>
    public abstract bool Load();

    /// <summary>
    /// 所有插件都 Load 完之后调用，用来注册依赖其他插件的资源。运行时才启用的插件
    /// 紧跟着它自己的 <see cref="Load"/> 调用。
    /// </summary>
    public virtual void PostLoad() { }

    /// <summary>
    /// 卸载插件，释放插件自己持有的非托管资源。
    /// </summary>
    public virtual void Unload() { }

    // 只有宿主（OBSharp.Runtime）会调用它。
    internal void Attach(ObsPluginContext context)
    {
        if (_context is not null)
            throw new InvalidOperationException($"Plugin \"{GetType()}\" was already attached.");

        _context = context;
    }
}
