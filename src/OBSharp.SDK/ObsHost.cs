using System.Diagnostics;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OBSharp;

/// <summary>
/// 插件注册的一个 host：一个名字 + 一台只监听 IPC 端点的 Kestrel。
/// </summary>
/// <remarks>
/// <para>
/// host 由 <see cref="ObsPluginContext.RegisterHost"/> 创建，注册本身不会启动它；插件
/// 自己决定什么时候 <see cref="Start"/>。插件和页面看到的规范地址是
/// <see cref="Url"/>（<c>obsharp://&lt;name&gt;/</c>），但 Kestrel 只监听下面两类端点，
/// 不占任何 TCP 端口：
/// </para>
/// <list type="bullet">
/// <item>Windows：命名管道 <c>\\.\pipe\obsharp\&lt;name&gt;</c>（Kestrel 侧
/// <c>ListenNamedPipe("obsharp\\&lt;name&gt;")</c>）；</item>
/// <item>Unix：UDS <c>/tmp/obsharp/&lt;name&gt;.socket</c>，启动后把权限设成 0600。</item>
/// </list>
/// <para>
/// 应用的内容根是插件的数据目录（<c>DataFolder</c>），<c>wwwroot</c> 在
/// <c>&lt;DataFolder&gt;/wwwroot</c>；日志接 <see cref="ObsPluginLoggerProvider"/>，
/// 跟 <see cref="ObsPlugin.CreateBuilder"/> 建出来的 Web 应用一样落进 OBS 日志。
/// </para>
/// <para>
/// 一个 host 同时是 CEF 里 scheme handler 的端点：浏览器访问
/// <c>obsharp://&lt;name&gt;/...</c> 时，原生库把它映射成
/// <c>http://&lt;name&gt;.obsharp/...</c> 再转给这里的命名管道 / UDS。
/// </para>
/// </remarks>
public sealed partial class ObsHost : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _startStop = new(1, 1);
    private readonly string _pluginName;
    private WebApplicationBuilder? _builder;
    private WebApplication? _app;
    private bool _builderTaken;
    private bool _disposed;
    private volatile bool _running;

    internal ObsHost(string pluginName, string dataFolder, string name)
    {
        _pluginName = pluginName;
        Name = name;
        DataFolder = dataFolder;
        Url = "obsharp://" + name + "/";
        Endpoint = OperatingSystem.IsWindows()
            ? @"\\.\pipe\obsharp\" + name
            : GetSocketPath(name);

        _builder = CreateBuilder();
    }

    /// <summary>
    /// host 名。同时也是管道名的一段、UDS 文件名和 CEF 里 domain 的前缀。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 插件和页面用的规范地址：<c>obsharp://&lt;name&gt;/</c>。这个协议永远是 InSecure 的。
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Kestrel 实际监听的端点：Windows 是 <c>\\.\pipe\obsharp\&lt;name&gt;</c>，
    /// Unix 是 <c>/tmp/obsharp/&lt;name&gt;.socket</c>。
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    /// 插件的数据目录，同时也是这台 Kestrel 的内容根。
    /// </summary>
    internal string DataFolder { get; }

    /// <summary>
    /// 已经启动、并且还没有 <see cref="StopAsync"/> 时是 <see langword="true"/>。
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 配置这台 Kestrel 用的 <see cref="WebApplicationBuilder"/>。
    /// </summary>
    /// <remarks>
    /// 只能取一次，<see cref="Build"/> 之后再取会抛：Builder 是给插件配服务和中间件的，
    /// 应用建好之后就不能再改了。默认配置（监听端点、内容根、日志）已经做好，插件只往上加。
    /// </remarks>
    /// <exception cref="InvalidOperationException">已经取过一次，或者已经 <see cref="Build"/>。</exception>
    /// <exception cref="ObjectDisposedException">host 已经释放。</exception>
    public WebApplicationBuilder Builder
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_app is not null || _builder is null)
                    throw new InvalidOperationException(
                        $"host \"{Name}\" 已经 Build 过，不能再取 Builder。");

                if (_builderTaken)
                    throw new InvalidOperationException(
                        $"host \"{Name}\" 的 Builder 只能取一次；要改配置请在第一次取到它之后一次改完。");

                _builderTaken = true;
                return _builder;
            }
        }
    }

    /// <summary>
    /// 建出 <see cref="WebApplication"/>。幂等：第二次调用返回同一个应用。
    /// </summary>
    /// <exception cref="ObjectDisposedException">host 已经释放。</exception>
    public WebApplication Build()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_app is not null)
                return _app;

            WebApplicationBuilder builder = _builder
                ?? throw new InvalidOperationException($"host \"{Name}\" 已经 Build 过。");

            _app = builder.Build();
            _builder = null;
            return _app;
        }
    }

    /// <summary>
    /// <see cref="Build"/> 并启动 Kestrel，同步等待端点开始监听。
    /// </summary>
    /// <exception cref="ObjectDisposedException">host 已经释放。</exception>
    public void Start()
    {
        WebApplication app = Build();

        _startStop.Wait();
        try
        {
            if (_running)
                return;

            ObjectDisposedException.ThrowIf(_disposed, this);

            app.Start();
            _running = true;
            ApplySocketPermissions();
        }
        finally
        {
            _startStop.Release();
        }
    }

    /// <summary>
    /// <see cref="Build"/> 并异步启动 Kestrel。
    /// </summary>
    /// <param name="cancellationToken">取消启动。</param>
    /// <exception cref="ObjectDisposedException">host 已经释放。</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        WebApplication app = Build();

        await _startStop.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running)
                return;

            ObjectDisposedException.ThrowIf(_disposed, this);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _running = true;
            ApplySocketPermissions();
        }
        finally
        {
            _startStop.Release();
        }
    }

    /// <summary>
    /// 停止 Kestrel。没启动过时是空操作。
    /// </summary>
    /// <param name="cancellationToken">取消停止。</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _startStop.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebApplication? app = _app;
            if (app is null || !_running)
                return;

            _running = false;
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startStop.Release();
        }
    }

    /// <summary>
    /// 停止并释放 Kestrel。多次调用是安全的。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        WebApplication? app;

        await _startStop.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _running = false;
                app = _app;
                _app = null;
                _builder = null;
            }
        }
        finally
        {
            _startStop.Release();
        }

        if (app is null)
            return;

        try
        {
            await app.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 停不下来也只能算了：插件已经卸载，进程自己会回收。
            Debug.WriteLine($"Stopping OB# host \"{Name}\" failed: {ex}");
        }

        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Disposing OB# host \"{Name}\" failed: {ex}");
        }
    }

    // 校验 host 名：小写字母 / 数字 / 连字符，1~48 个字符，首尾不能是连字符。
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$")]
    private static partial Regex HostNamePattern();

    /// <summary>
    /// 校验 host 名。不合法的名字会直接当管道名 / 文件名用，必须先挡住。
    /// </summary>
    /// <exception cref="ArgumentException">名字为空或不合法。</exception>
    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!HostNamePattern().IsMatch(name))
            throw new ArgumentException(
                $"host 名 \"{name}\" 不合法：只能用小写字母、数字和连字符，1~48 个字符，首尾不能是连字符。",
                nameof(name));
    }

    private static string GetSocketPath(string name)
        => Path.Combine(Path.GetTempPath(), "obsharp", name + ".socket");

    // ApplicationName 用插件名（不是 host 名）：插件的 appsettings.json 按插件程序集名找，
    // 跟现有 CreateBuilder() 建出来的应用保持一致。
    private WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = _pluginName,
            ContentRootPath = DataFolder,
            WebRootPath = Path.Combine(DataFolder, "wwwroot"),
        });

        // 跟 ObsPluginContext.CreateBuilder() 一样：日志落进 OBS 日志，每行带插件标识符。
        builder.Logging.AddProvider(new ObsPluginLoggerProvider(_pluginName));

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (OperatingSystem.IsWindows())
            {
                // Kestrel 的 ListenNamedPipe 会把名字接到 \\.\pipe\ 后面，
                // 所以这里写 "obsharp\<name>"，实际端点就是 \\.\pipe\obsharp\<name>。
                options.ListenNamedPipe("obsharp\\" + Name);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Endpoint)!);
                options.ListenUnixSocket(Endpoint);
            }
        });

        return builder;
    }

    // UDS 是普通文件，启动之后收紧成 0600：只有当前用户能连。
    private void ApplySocketPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(Endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            // 权限没设上也还能用（比如文件系统不支持），记一笔就好。
            Debug.WriteLine($"Could not set the permissions of OB# host socket \"{Endpoint}\": {ex}");
        }
    }
}
