using System.Diagnostics;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OBSharp.Interop;
using OBSharp.Interop.Browser;

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
/// <c>https://&lt;name&gt;.obsharp/...</c> 再转给这里的命名管道 / UDS。
/// </para>
/// <para>
/// 之所以在 CEF 里是 <c>https</c>：那只是让页面落在<b>安全上下文</b>里（否则
/// <c>crypto.subtle</c> / <c>serviceWorker</c> / <c>clipboard</c> 这些 API 用不了）。
/// 这些 https 请求由原生库的 scheme handler 直接接住转给 IPC 端点，<b>不做 TLS</b>、
/// 不上网络栈，所以没有任何加解密开销。插件和页面里写地址时仍然用 <see cref="Url"/>
/// （<c>obsharp://</c>），映射由原生库统一做。
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
    /// 插件和页面用的规范地址：<c>obsharp://&lt;name&gt;/</c>。
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
    /// 已经 <see cref="DisposeAsync"/> 时是 <see langword="true"/>。
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 插件名（用于日志前缀，跟 <see cref="ObsPlugin.Name"/> 一致）。
    /// </summary>
    internal string PluginName => _pluginName;

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
    internal static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name) && HostNamePattern().IsMatch(name);

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

    // ---- OBS 集成 ----------------------------------------------------------
    //
    // 这两组方法就是 SDK 对 OBS 前端的全部暴露面。刻意做得薄：SDK 不发明窗口体系，
    // 只把「浏览器（ObsBrowser）」和「放到哪儿」两件事分开。

    // 本 host 挂上去的停靠面板 id。host 销毁时统一撤掉，免得留下指向已卸载插件的面板。
    private readonly HashSet<string> _dockPanels = [];
    private readonly Lock _dockGate = new();

    private void TrackDockPanel(string id)
    {
        lock (_dockGate)
            _dockPanels.Add(id);
    }

    private void UntrackDockPanel(string id)
    {
        lock (_dockGate)
            _dockPanels.Remove(id);
    }

    /// <summary>
    /// 建一块浏览器并挂成 OBS 的停靠面板，<b>不阻塞调用方</b>。
    /// </summary>
    /// <param name="id">面板 id（<c>QDockWidget</c> 的 objectName），全局唯一。</param>
    /// <param name="title">「停靠窗口」菜单里显示的文字，同时也是面板标题。</param>
    /// <param name="url">初始地址；为 <see langword="null"/> 时用 host 的地址。</param>
    /// <returns>挂上返回浏览器对象；失败（CEF 不可用 / id 被占 / 不在 OBS 里）返回 null。</returns>
    /// <remarks>
    /// <para>
    /// 建浏览器要等 CEF 冷启动（几秒），而 <see cref="ObsPlugin.Load"/> 跑在 OBS 的 Qt
    /// 主线程上，同步等会卡住整个界面。所以这里把「等 CEF」挪到线程池上，
    /// 插件侧只管 <c>await</c> 或者干脆不等。
    /// </para>
    /// <para>
    /// 返回值在挂上之后才有意义：不关心结果的话直接
    /// <c>_ = host.AddDockPanelAsync(...)</c> 就行；要记着这块浏览器（比如卸载时要
    /// 区分它和窗口那块）就 await 它。
    /// </para>
    /// </remarks>
    public async Task<ObsBrowser?> AddDockPanelAsync(string id, string title, string? url = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        // ConfigureAwait(false)：建浏览器是纯原生调用，不需要回原来的调度器。
        // 这一句就是「不要在 Qt 主线程上等」的全部实现，插件侧不用再写 Task.Run。
        ObsBrowser browser = await Task.Run(() => ObsBrowser.Create(this, url)).ConfigureAwait(false);

        if (!AddDockPanel(id, title, browser))
        {
            browser.Dispose();
            return null;
        }

        return browser;
    }

    /// <summary>
    /// 把一块浏览器的 Widget 挂成 OBS 的停靠面板。
    /// </summary>    /// <param name="id">面板 id（<c>QDockWidget</c> 的 objectName），全局唯一。</param>
    /// <param name="title">「停靠窗口」菜单里显示的文字，同时也是面板标题。</param>
    /// <param name="browser">要显示的浏览器。</param>
    /// <returns>挂上了返回 <see langword="true"/>；id 被占用或不在 OBS 里返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// <para>
    /// 底层调 <c>obs_frontend_add_dock_by_id()</c>。这个 API 内部走 OBS 自己的
    /// <c>AddDockWidget</c>，所以它会**自动**把面板加进「停靠窗口」菜单 ——
    /// 不需要额外做什么（不像 <c>obs_frontend_add_custom_qdock</c> 只挂 dock 不加菜单）。
    /// </para>
    /// <para>
    /// 挂上之后 Widget 归 OBS 所有，<b>不要</b>再 <see cref="ObsBrowser.Dispose"/>：
    /// 撤掉面板请用 <see cref="RemoveDockPanel"/>。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="browser"/> 是 null。</exception>
    public bool AddDockPanel(string id, string title, ObsBrowser browser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(browser);

        if (browser.Widget == 0)
            return false;

        bool added = false;
        OBSharpNative.InvokeUi(
            () => added = ObsFrontend.AddDockById(id, title, browser.Widget));

        if (added)
            TrackDockPanel(id);

        return added;
    }

    /// <summary>
    /// 撤掉 <see cref="AddDockPanel"/> 挂上去的面板。
    /// </summary>
    /// <remarks>
    /// 面板里的浏览器 Widget 归 OBS，撤掉之后由 OBS 销毁；调用方不要再碰那个
    /// <see cref="ObsBrowser"/>（也不要 <see cref="ObsBrowser.Dispose"/>）。
    /// </remarks>
    public void RemoveDockPanel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        OBSharpNative.InvokeUi(() => ObsFrontend.RemoveDock(id));
        UntrackDockPanel(id);
    }

    /// <summary>
    /// 起 Kestrel 并在「工具」菜单里加一项；点它执行 <paramref name="onClick"/>。
    /// </summary>
    /// <param name="text">菜单项文字。</param>
    /// <param name="onClick">点击回调，在 Qt 主线程上执行。</param>
    /// <returns>加上了返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 注意：OBS <b>没有</b>「移除工具菜单项」的 API，加进去的那一项会一直在。所以
    /// 回调必须能在插件卸载后安全地什么都不做 —— 用 <see cref="ObsPluginContext"/> 的
    /// 存活状态判断，或者把窗口引用置空。
    /// </para>
    /// <para>
    /// 只加菜单项、不建窗口：窗口应该在回调里懒创建（点一次才建，关掉就销毁），
    /// 否则插件一加载就白白吃掉一个 CEF 实例。
    /// </para>
    /// </remarks>
    public bool AddToolsMenuItem(string text, Action onClick)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(onClick);

        return ToolsMenu.Add(this, text, onClick);
    }

    /// <summary>
    /// 断开本 host 加过的全部「工具」菜单项回调。
    /// </summary>
    /// <remarks>
    /// OBS 没有「移除菜单项」的 API，那些项仍留在菜单里；断开之后点了什么都不做。
    /// <see cref="DisposeAsync"/> 会自动做这一步，一般不用手动调。
    /// </remarks>
    public void ClearToolsMenuItems() => ToolsMenu.RemoveAllFor(this);
}
