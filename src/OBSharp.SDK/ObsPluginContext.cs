using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OBSharp;

/// <summary>
/// 宿主（OBSharp.Runtime）为每个插件准备好的环境信息，插件通过
/// <see cref="ObsPlugin.Context"/> 拿到它。插件不需要自己构造。
/// </summary>
/// <remarks>
/// 宿主固定跑在 ASP.NET Core 上（<c>Microsoft.AspNetCore.App</c> 随宿主一起加载），
/// 插件要用 ASP.NET Core 里的类型，自己加
/// <c>&lt;FrameworkReference Include="Microsoft.AspNetCore.App" /&gt;</c> 就能编译。
/// </remarks>
public sealed class ObsPluginContext
{
    internal ObsPluginContext(string name, string dataFolder)
    {
        Name = name;
        DataFolder = dataFolder;
    }

    /// <summary>
    /// 插件标识符
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 插件的数据目录 <c>&lt;OB# 数据目录&gt;/&lt;插件名&gt;</c>，附加到插件之前已经创建好。
    /// </summary>
    public string DataFolder { get; }


    internal WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions()
        {
            ApplicationName = Name,
            ContentRootPath = DataFolder,
            WebRootPath = Path.Combine(DataFolder, "wwwroot"),
        });

        // 插件的 .NET 日志写进 OBS 日志，每行以插件标识符开头（见 ObsPluginLoggerProvider）。
        // 默认的 console provider 留着：从终端起 OBS（或者以后单独跑插件）时还能看到。
        builder.Logging.AddProvider(new ObsPluginLoggerProvider(Name));

        string endpoint = GetEndpoint();

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (OperatingSystem.IsWindows())
            {
                options.ListenNamedPipe(Name);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
                options.ListenUnixSocket(endpoint);
                File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });

        // Web 应用起来之后，把这个插件挂成 OBS 的一个停靠面板（见 ObsPluginPanel）。
        builder.Services.AddHostedService(services => new ObsPluginPanel(
            Name,
            endpoint,
            services.GetRequiredService<ILoggerFactory>().CreateLogger<ObsPluginPanel>()));

        return builder;
    }

    // 插件 Web 应用的监听地址：Windows 是命名管道名，Unix 是 UDS 路径。
    private string GetEndpoint() => OperatingSystem.IsWindows()
        ? Name
        : Path.Combine(Path.GetTempPath(), "obsharp", Name + ".socket");
}
