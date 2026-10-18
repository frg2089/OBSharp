using OBSharp.Interop;

namespace OBSharp;

/// <summary>
/// 插件基类。一个插件程序集里恰好实现一个子类，宿主（OBSharp.Runtime）用依赖注入
/// 构造它（构造函数可以注入 <c>ILogger&lt;T&gt;</c>、<c>IConfiguration</c> 等），
/// 构造后附加 <see cref="Context"/>，再按 <see cref="Load"/>、
/// <see cref="PostLoad"/>、<see cref="Unload"/> 的顺序调用。
/// </summary>
public abstract class ObsPlugin
{
    private ObsPluginContext? _context;

    /// <summary>插件名。默认取插件程序集名（不含扩展名），同时用作数据目录名。</summary>
    public virtual string Name
    {
        get
        {
            var type = GetType();
            return type.Assembly.GetName().Name ?? type.Namespace ?? type.Name;
        }
    }

    /// <summary>插件描述，默认 null。</summary>
    public virtual string? Description => null;

    /// <summary>宿主附加的环境信息。只有加载开始之后（<see cref="Load"/> 及以后）才可用。</summary>
    public ObsPluginContext Context => _context
        ?? throw new InvalidOperationException($"Plugin \"{GetType()}\" has not been attached by a host yet.");

    /// <summary>插件的数据目录，等于 <see cref="ObsPluginContext.DataFolder"/>。</summary>
    public string DataFolder => Context.DataFolder;

    /// <summary>OB# 这个 OBS 模块的 <c>obs_module_t*</c>，等于 <see cref="ObsPluginContext.Module"/>。</summary>
    public unsafe ObsModule* Module => Context.Module;

    /// <summary>宿主注册的服务，等于 <see cref="ObsPluginContext.Services"/>。</summary>
    public IServiceProvider Services => Context.Services;

    /// <summary>读取插件自己的本地化字符串，见 <see cref="ObsPluginContext.GetText"/>。</summary>
    public string GetText(string key) => Context.GetText(key);

    /// <summary>加载插件。返回 false 表示加载失败：宿主会记一条警告，但不会因此卸载整个模块。</summary>
    public abstract bool Load();

    /// <summary>所有插件都 Load 完之后调用，用来注册依赖其他插件的资源。</summary>
    public virtual void PostLoad() { }

    /// <summary>卸载插件，释放插件自己持有的非托管资源。</summary>
    public virtual void Unload() { }

    // 只有宿主（OBSharp.Runtime）会调用它。
    internal void Attach(ObsPluginContext context)
    {
        if (_context is not null)
            throw new InvalidOperationException($"Plugin \"{GetType()}\" was already attached.");

        _context = context;
    }
}
