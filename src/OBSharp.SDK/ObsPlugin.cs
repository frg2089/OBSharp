using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace OBSharp;

/// <summary>
/// 插件基类。一个插件程序集里恰好实现一个子类，宿主（OBSharp.Runtime）加载程序集之后
/// 用无参构造函数构造它，再附加 <see cref="Context"/>，按 <see cref="Load"/>、
/// <see cref="PostLoad"/>、<see cref="Unload"/> 的顺序调用。
/// </summary>
public abstract class ObsPlugin
{
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

    protected virtual WebApplicationBuilder CreateBuilder(string? name = null)
    {
        name ??= Name;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = name,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        // 跟 ObsPluginContext.CreateBuilder() 一样：日志落进 OBS 日志，每行带插件标识符。
        builder.Logging.AddProvider(new ObsLoggerProvider(name));

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (OperatingSystem.IsWindows())
            {
                // Kestrel 的 ListenNamedPipe 会把名字接到 \\.\pipe\ 后面，
                // 所以这里写 "obsharp\<name>"，实际端点就是 \\.\pipe\obsharp\<name>。
                options.ListenNamedPipe("obsharp\\" + name);
            }
            else
            {
                var path = Path.Combine(Path.GetTempPath(), "obsharp", $"{name}.socket");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path))
                    File.Delete(path);
                options.ListenUnixSocket(path);
            }
        });

        return builder;
    }
}
