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
/// <para>
/// 宿主固定跑在 ASP.NET Core 上（<c>Microsoft.AspNetCore.App</c> 随宿主一起加载），
/// 插件要用 ASP.NET Core 里的类型，自己加
/// <c>&lt;FrameworkReference Include="Microsoft.AspNetCore.App" /&gt;</c> 就能编译。
/// </para>
/// <para>
/// 除了 <see cref="CreateBuilder"/> 那条「一个插件一个 Web 应用、自动挂成停靠面板」的老路，
/// 插件还可以用 <see cref="RegisterHost"/> 显式注册任意多个 host（每个 host 一台只监听
/// IPC 端点的 Kestrel），再自己决定什么时候启动它们。
/// </para>
/// </remarks>
public sealed class ObsPluginContext
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ObsHost> _hosts = new(StringComparer.Ordinal);
    private readonly List<ObsWindow> _windows = [];
    private bool _closed;

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

    /// <summary>
    /// 插件注册过的全部 host，按注册顺序排列。
    /// </summary>
    /// <remarks>
    /// 只注册、没启动的 host 也在里面；<see cref="ObsHost.IsRunning"/> 才表示它是否在监听。
    /// </remarks>
    public IReadOnlyList<ObsHost> Hosts
    {
        get
        {
            lock (_gate)
                return [.. _hosts.Values];
        }
    }

    /// <summary>
    /// 注册一个 host（一个名字 + 一台只监听 IPC 端点的 Kestrel）。幂等：同名返回同一个对象。
    /// </summary>
    /// <param name="name">
    /// host 名：小写字母 / 数字 / 连字符，1~48 个字符，首尾不能是连字符。它同时是
    /// 命名管道名的一段、UDS 文件名和 CEF 里 domain 的前缀。
    /// </param>
    /// <remarks>
    /// 注册不会启动 Kestrel，也不会占任何端口；插件拿到对象后自己 <see cref="ObsHost.Start"/>。
    /// host 和窗口彼此独立：只注册 host 不开窗口是合法的。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> 不是合法的 host 名。</exception>
    /// <exception cref="ObjectDisposedException">插件的 context 已经关闭（插件正在卸载）。</exception>
    public ObsHost RegisterHost(string name)
    {
        ObsHost.ValidateName(name);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            if (_hosts.TryGetValue(name, out ObsHost? existing))
                return existing;

            var host = new ObsHost(Name, DataFolder, name);
            _hosts.Add(host.Name, host);
            return host;
        }
    }

    /// <summary>
    /// 找已注册的 host；没有时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="name">host 名。</param>
    public ObsHost? FindHost(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_gate)
            return _hosts.GetValueOrDefault(name);
    }

    /// <summary>
    /// 插件开过的全部窗口，按创建顺序排列。
    /// </summary>
    /// <remarks>
    /// 已经 <see cref="ObsWindow.Dispose"/> 的窗口会被移出这个列表。
    /// </remarks>
    public IReadOnlyList<ObsWindow> Windows
    {
        get
        {
            lock (_gate)
                return [.. _windows];
        }
    }

    /// <summary>
    /// 开一个停靠窗口（OBS 面板）。窗口第一次 <see cref="ObsWindow.Show"/> 时才创建 CEF 浏览器。
    /// </summary>
    /// <param name="options">窗口参数。</param>
    /// <remarks>
    /// 停靠窗口挂进 OBS 用的是 <c>obs_frontend_add_custom_qdock()</c>；窗口对象自己管生命周期，
    /// 插件卸载时由 <see cref="Close"/> 统一释放。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 是 null。</exception>
    /// <exception cref="ArgumentException"><see cref="ObsWindowOptions.Id"/> 或
    /// <see cref="ObsWindowOptions.Title"/> 是空的。</exception>
    /// <exception cref="ObjectDisposedException">插件的 context 已经关闭（插件正在卸载）。</exception>
    public ObsDockWindow AddDockWindow(ObsWindowOptions options)
    {
        ValidateWindowOptions(options);
        ThrowIfClosed();

        // 不在锁里构造：窗口构造可能立刻 Show（ShowOnCreate），那会同步切到 Qt 主线程。
        var window = new ObsDockWindow(this, options);
        if (TryCloseWindow(window))
            throw new ObjectDisposedException(nameof(ObsPluginContext));

        return window;
    }

    /// <summary>
    /// 开一个工具窗口（独立顶层窗口）。窗口第一次 <see cref="ObsWindow.Show"/> 时才创建 CEF 浏览器。
    /// </summary>
    /// <param name="options">窗口参数。</param>
    /// <remarks>
    /// 工具窗口是 <c>Qt::Window | Qt::Tool</c>；用户点关闭只是隐藏，要销毁请
    /// <see cref="ObsWindow.Dispose"/>。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 是 null。</exception>
    /// <exception cref="ArgumentException"><see cref="ObsWindowOptions.Id"/> 或
    /// <see cref="ObsWindowOptions.Title"/> 是空的。</exception>
    /// <exception cref="ObjectDisposedException">插件的 context 已经关闭（插件正在卸载）。</exception>
    public ObsToolWindow AddToolWindow(ObsWindowOptions options)
    {
        ValidateWindowOptions(options);
        ThrowIfClosed();

        var window = new ObsToolWindow(this, options);
        if (TryCloseWindow(window))
            throw new ObjectDisposedException(nameof(ObsPluginContext));

        return window;
    }

    // 窗口自己（基类构造函数 / Dispose）登记进 Windows 列表，插件不用管。
    internal void TrackWindow(ObsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            if (!_windows.Contains(window))
                _windows.Add(window);
        }
    }

    internal void UntrackWindow(ObsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
            _windows.Remove(window);
    }

    private void ThrowIfClosed()
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_closed, this);
    }

    // 构造窗口的同时插件正好在卸载：把刚建出来的窗口立刻收拾掉，让调用方知道。
    private bool TryCloseWindow(ObsWindow window)
    {
        lock (_gate)
        {
            if (!_closed)
                return false;
        }

        try
        {
            window.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Disposing OB# window \"{window.Id}\" failed: {ex}");
        }

        return true;
    }

    private static void ValidateWindowOptions(ObsWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
    }

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

    /// <summary>
    /// 插件卸载时由宿主调用（<see cref="ObsPlugin.CloseContext"/>）：先释放插件开的所有窗口，
    /// 再停掉这个插件注册的全部 host。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先窗口后 host：停靠窗口要先从 OBS 摘掉（<see cref="ObsWindow.Dispose"/> 里做），
    /// 页面在窗口销毁前还能正常请求 host。
    /// </para>
    /// <para>
    /// 不阻塞调用方：卸载可能就发生在 OBS 的 Qt 主线程上，所以每个 host 都丢到后台停
    /// （不在这里同步等 <see cref="ObsHost.StopAsync"/>）。停止完顺带释放 Kestrel。
    /// </para>
    /// <para>
    /// 多次调用是安全的：只有第一次真正干活，之后是空操作。
    /// </para>
    /// </remarks>
    internal void Close()
    {
        ObsWindow[] windows;
        ObsHost[] hosts;

        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
            windows = [.. _windows];
            _windows.Clear();
            hosts = [.. _hosts.Values];
            _hosts.Clear();
        }

        // 窗口在调用的线程上同步释放：Dispose 自己会切到 Qt 主线程去做原生操作。
        foreach (ObsWindow window in windows)
        {
            try
            {
                window.Dispose();
            }
            catch (Exception ex)
            {
                // 一个窗口收拾不干净不能拖累其它窗口和 host。
                System.Diagnostics.Debug.WriteLine($"Disposing OB# window \"{window.Id}\" failed: {ex}");
            }
        }

        foreach (ObsHost host in hosts)
            StopInBackground(host);
    }

    // host 的停止放到后台：StopAsync 要等 Kestrel 收尾，不能在 Qt 主线程上同步等。
    private static void StopInBackground(ObsHost host)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 停不下来也只能算了：插件已经卸载，进程自己会回收。
                System.Diagnostics.Debug.WriteLine($"Stopping OB# host \"{host.Name}\" failed: {ex}");
            }
        });
    }
}
