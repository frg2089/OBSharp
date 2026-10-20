using Microsoft.AspNetCore.Builder;

namespace OBSharp;

/// <summary>
/// 插件基类。一个插件程序集里恰好实现一个子类，宿主（OBSharp.Runtime）加载程序集之后
/// 用无参构造函数构造它，再附加 <see cref="Context"/>，按 <see cref="Load"/>、
/// <see cref="PostLoad"/>、<see cref="Unload"/> 的顺序调用。
/// </summary>
public abstract class ObsPlugin
{
    private ObsPluginContext? _context;
    private WebApplicationBuilder? _builder;

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
    /// <remarks>
    /// 插件自己通过 <see cref="ObsPluginContext.RegisterHost"/> 注册的 host、通过
    /// <see cref="ObsBrowser"/> / <see cref="ObsWindow"/> 之类的对象，由宿主在 <see cref="Unload"/>
    /// 之后统一收尾（见 <see cref="CloseContext"/>），插件不用手动停它们；这里只管插件
    /// 自己另外持有的东西。
    /// </remarks>
    public virtual void Unload() { }

    /// <summary>
    /// 创建一个 <see cref="WebApplicationBuilder"/>，用于配置插件自己的 Web 应用。每个插件
    /// 只能创建一个 Builder。
    /// </summary>
    /// <remarks>
    /// 这个 Web 应用会被挂成 OBS 的一个停靠面板：应用跑起来（<c>Run</c> / <c>RunAsync</c>，
    /// Kestrel 开始监听）之后，OB# 用浏览器连上它并调用 <c>obs_frontend_add_dock_by_id()</c>；
    /// 应用停下时面板撤掉。进程里没有 obs-browser 时只是没有面板，应用本身照常。
    /// </remarks>
    protected WebApplicationBuilder CreateBuilder() => _builder ??= Context.CreateBuilder();

    // 只有宿主（OBSharp.Runtime）会调用它。
    internal void Attach(ObsPluginContext context)
    {
        if (_context is not null)
            throw new InvalidOperationException($"Plugin \"{GetType()}\" was already attached.");

        _context = context;
    }

    /// <summary>
    /// 收尾插件注册的 host / 窗口。由宿主在 <see cref="Unload"/> 之后调用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 插件卸载（停用、回滚、宿主退出）时，宿主调完 <see cref="Unload"/> 就调这里：
    /// 先释放插件开的窗口（停靠窗口先从 OBS 摘掉），再把 host 丢到后台停止，整个收尾
    /// 不阻塞 OBS 的 Qt 主线程。
    /// </para>
    /// <para>
    /// 幂等：多次调用无害。插件不用自己调它，自己先收拾也没关系 —— 收尾只处理还活着的对象。
    /// </para>
    /// </remarks>
    internal void CloseContext()
    {
        try
        {
            _context?.Close();
        }
        catch (Exception ex)
        {
            // 卸载流程不能被收尾拖垮：收尾失败只记一笔，宿主继续卸加载上下文。
            System.Diagnostics.Debug.WriteLine($"Closing the OB# context of plugin \"{GetType()}\" failed: {ex}");
        }
    }
}
