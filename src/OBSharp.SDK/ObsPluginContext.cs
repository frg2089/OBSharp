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
/// 插件用 <see cref="RegisterHost"/> 注册任意多个 host（每个 host 一台只监听 IPC 端点的
/// Kestrel），再自己决定什么时候启动它们、把页面挂到哪儿。
/// </para>
/// <para>
/// 「挂到哪儿」是三件独立的事，SDK 不把它们捏成一个「窗口」概念：
/// <see cref="ObsBrowser"/> 负责一块页面，<see cref="ObsHost.AddDockPanel"/> 负责挂成
/// OBS 的停靠面板，<see cref="ObsWindow"/> 负责开一个普通顶层窗口。
/// </para>
/// </remarks>
public sealed class ObsPluginContext
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ObsHost> _hosts = new(StringComparer.Ordinal);
    private readonly List<ObsBrowser> _browsers = [];
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
    /// 已经 <see cref="ObsHost.DisposeAsync"/> 的 host 会被移出这个列表。
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
    /// 按名字找 host；没有时返回 <see langword="null"/>。
    /// </summary>
    public ObsHost? FindHost(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_gate)
            return _hosts.GetValueOrDefault(name);
    }

    /// <summary>
    /// 注册一个 host。只注册、不启动，插件自己决定什么时候 <see cref="ObsHost.Start"/>。
    /// </summary>
    /// <param name="name">
    /// host 名：小写字母、数字、连字符，1~48 个字符，首尾不能是连字符。
    /// 同时是命名管道 / UDS 的一段和 CEF 里 domain 的前缀。
    /// </param>
    /// <remarks>
    /// 同名幂等：已经注册过就返回同一个对象（这时 <see cref="ObsHost.IsStarted"/> 可能是 true）。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="name"/> 不合规。</exception>
    /// <exception cref="ObjectDisposedException">context 已经关闭（插件正在卸载）。</exception>
    public ObsHost RegisterHost(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!ObsHost.IsValidName(name))
            throw new ArgumentOutOfRangeException(nameof(name), name, "host 名必须是小写字母 / 数字 / 连字符，首尾不能是连字符，最多 48 个字符。");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            if (_hosts.TryGetValue(name, out ObsHost? existing))
                return existing;

            var host = new ObsHost(Name, DataFolder, name);
            _hosts[name] = host;
            return host;
        }
    }

    /// <summary>
    /// 建一块浏览器，并登记到插件名下（卸载时自动释放）。
    /// </summary>
    /// <param name="host">页面所属的 host。</param>
    /// <param name="url">初始地址；为 <see langword="null"/> 时用 host 的地址。</param>
    /// <exception cref="ObjectDisposedException">context 已经关闭。</exception>
    public ObsBrowser CreateBrowser(ObsHost host, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ThrowIfClosed();

        ObsBrowser browser = ObsBrowser.Create(host, url);
        TrackBrowser(browser);
        return browser;
    }

    /// <summary>
    /// 建一块浏览器，并登记到插件名下（卸载时自动释放）。<b>不阻塞调用方</b>。
    /// </summary>
    /// <param name="host">页面所属的 host。</param>
    /// <param name="url">初始地址；为 <see langword="null"/> 时用 host 的地址。</param>
    /// <exception cref="ObjectDisposedException">context 已经关闭。</exception>
    public async Task<ObsBrowser> CreateBrowserAsync(ObsHost host, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ThrowIfClosed();

        ObsBrowser browser = await ObsBrowser.CreateAsync(host, url).ConfigureAwait(false);
        TrackBrowser(browser);
        return browser;
    }

    /// <summary>
    /// 开一个普通顶层窗口，并登记到插件名下（卸载时自动销毁）。
    /// </summary>
    /// <param name="options">窗口参数。</param>
    /// <exception cref="ObjectDisposedException">context 已经关闭。</exception>
    public ObsWindow CreateWindow(ObsWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfClosed();

        var window = ObsWindow.Create(options);
        TrackWindow(window);
        return window;
    }

    /// <summary>
    /// 插件开过的全部浏览器，按创建顺序排列。
    /// </summary>
    public IReadOnlyList<ObsBrowser> Browsers
    {
        get
        {
            lock (_gate)
                return [.. _browsers];
        }
    }

    /// <summary>
    /// 插件开过的全部窗口，按创建顺序排列。
    /// </summary>
    public IReadOnlyList<ObsWindow> Windows
    {
        get
        {
            lock (_gate)
                return [.. _windows];
        }
    }

    internal void TrackHost(ObsHost host)
    {
        lock (_gate)
        {
            if (!_closed)
                return;
        }

        // 注册时 context 正好在关闭：立刻收拾掉。
        _ = host.DisposeAsync();
    }

    internal void TrackBrowser(ObsBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        lock (_gate)
        {
            if (!_closed)
            {
                _browsers.Add(browser);
                return;
            }
        }

        // context 已经关闭：别留下一个没人管的浏览器。
        browser.Dispose();
    }

    internal void TrackWindow(ObsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            if (!_closed)
            {
                _windows.Add(window);
                return;
            }
        }

        window.Dispose();
    }

    internal void UntrackHost(ObsHost host)
    {
        lock (_gate)
            _hosts.Remove(host.Name);
    }

    internal void UntrackBrowser(ObsBrowser browser)
    {
        lock (_gate)
            _browsers.Remove(browser);
    }

    internal void UntrackWindow(ObsWindow window)
    {
        lock (_gate)
            _windows.Remove(window);
    }

    private void ThrowIfClosed()
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_closed, this);
    }

    internal WebApplicationBuilder CreateBuilder() => throw new NotSupportedException(
        "ObsPluginContext.CreateBuilder 已经移除：插件请用 Context.RegisterHost(name) 自己起 Kestrel，" +
        "再把页面挂成停靠面板（host.AddDockPanel）或普通窗口（Context.CreateWindow）。");

    // 插件 Web 应用的监听地址：Windows 是命名管道名，Unix 是 UDS 路径。
    private string GetEndpoint() => OperatingSystem.IsWindows()
        ? Name
        : Path.Combine(Path.GetTempPath(), "obsharp", Name + ".socket");

    /// <summary>
    /// 插件卸载时由宿主调用（<see cref="ObsPlugin.CloseContext"/>）：先释放插件开的浏览器和
    /// 窗口，再停掉这个插件注册的全部 host。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先浏览器 / 窗口、后 host：页面上可能还有请求在飞，先撤掉 UI 再停服务更干净。
    /// 停靠面板要先从 OBS 摘掉（<see cref="ObsHost.RemoveDockPanel"/>），否则 OBS 会留着
    /// 一个指向已卸载插件的 Widget。
    /// </para>
    /// <para>
    /// 不阻塞调用方：卸载可能就发生在 OBS 的 Qt 主线程上，所以每个 host 都丢到后台停
    /// （不在这里同步等 <see cref="ObsHost.StopAsync"/>）。
    /// </para>
    /// <para>
    /// 多次调用是安全的：只有第一次真正干活，之后是空操作。
    /// </para>
    /// </remarks>
    internal void Close()
    {
        ObsBrowser[] browsers;
        ObsWindow[] windows;
        ObsHost[] hosts;

        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
            browsers = [.. _browsers];
            windows = [.. _windows];
            hosts = [.. _hosts.Values];
            _browsers.Clear();
            _windows.Clear();
            _hosts.Clear();
        }

        foreach (ObsWindow window in windows)
        {
            try
            {
                window.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Disposing OB# window of plugin \"{Name}\" failed: {ex}");
            }
        }

        foreach (ObsBrowser browser in browsers)
        {
            try
            {
                browser.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Disposing OB# browser of plugin \"{Name}\" failed: {ex}");
            }
        }

        foreach (ObsHost host in hosts)
        {
            try
            {
                ToolsMenu.RemoveAllFor(host);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Removing tool menu items of plugin \"{Name}\" failed: {ex}");
            }

            // 停 host：StopAsync 内部自己排线程，不阻塞这里（卸载可能就在 Qt 主线程上）。
            // 不再额外包一层 Task.Run —— 谁负责等待线程就由那一层负责。
            try
            {
                host.StopAsync().ContinueWith(
                    static task => System.Diagnostics.Debug.WriteLine(
                        $"Stopping an OB# host failed: {task.Exception}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Stopping OB# host \"{host.Name}\" failed: {ex}");
            }
        }
    }
}
