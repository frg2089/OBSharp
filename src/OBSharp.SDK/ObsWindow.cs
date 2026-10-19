using System.Diagnostics;

using OBSharp.Interop;
using OBSharp.Interop.Browser;

namespace OBSharp;

/// <summary>
/// 插件窗口的基类：一块 CEF 浏览器，加一个 OBS 容器（停靠面板或顶层工具窗口）。
/// </summary>
/// <remarks>
/// <para>
/// 窗口是懒创建的：第一次 <see cref="Show"/> 时才初始化 <c>OBSharp.Interop.Browser</c>、
/// 建浏览器并导航，之后 <see cref="Show"/> / <see cref="Hide"/> 只是显示 / 隐藏。
/// 所以构造窗口对象很便宜，可以放心在 <see cref="ObsPlugin.PostLoad"/> 里建。
/// </para>
/// <para>
/// 第一次 <see cref="Show"/> 可能等 CEF 就绪（冷启动几秒）而阻塞，并且会同步切到 OBS 的 Qt
/// 主线程；建议从后台线程调用，或者用 <see cref="ObsWindowOptions.ShowOnCreate"/> 在加载阶段
/// 就建好。不要在 OBS 主线程上调用第一次 <see cref="Show"/>：那会在等 CEF 的时候把界面卡住。
/// </para>
/// <para>
/// 进程里没有 obs-browser / CEF 起不来时只记一条警告，不抛异常：窗口对象仍然可用，
/// 只是 <see cref="IsVisible"/> 一直是 <see langword="false"/>（之后再 <see cref="Show"/> 会重试）。
/// 所有会碰原生库的调用也都包在 try/catch 里，失败只记警告。
/// </para>
/// <para>
/// 线程安全：创建 / 显示由一次性的「创建中」标记串行化，<see cref="Dispose"/> 不阻塞调用方
/// （尤其在 Qt 主线程上被调时）——如果销毁时正好有另一个线程在建窗口，原生清理交给那个线程收尾。
/// </para>
/// </remarks>
public abstract class ObsWindow : IDisposable
{
    private readonly ObsPluginContext? _context;
    private readonly ObsHost? _host;
    private readonly string _url;
    private readonly string? _endpoint;
    private readonly bool _topmost;

    private volatile string _title;

    // 原生窗口已经建出来（Show 第一次成功 / 之后一直为 true，直到销毁）。
    private volatile bool _created;

    // 有人要销毁这个窗口；真正干活的是 CompleteDispose（只干一次）。
    private volatile bool _disposeRequested;
    private int _disposeClaimed;

    // 1 表示有线程正在第一次 Show 里建窗口 / 显示它。创建会阻塞（等 CEF、切主线程），
    // 所以销毁不能拿着锁等它，只能把清理交给它收尾。
    private int _creating;

    internal ObsWindow(ObsPluginContext? context, ObsWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Id))
            throw new ArgumentException("窗口 id 不能为空。", nameof(options));
        if (options.Title is null)
            throw new ArgumentException("窗口标题不能为 null。", nameof(options));

        Id = options.Id;
        _title = options.Title;
        _context = context;
        _host = options.Host;
        _topmost = options.Topmost;

        _url = ResolveUrl(options);

        // 端点由 host 推导：ObsHost.Endpoint 就是规范形态（Windows \\.\pipe\obsharp\<name>，
        // Unix /tmp/obsharp/<name>.socket），这里直接沿用，免得两边写法漂移。
        _endpoint = _host?.Endpoint;

        Width = options.Width > 0 ? options.Width : 720;
        Height = options.Height > 0 ? options.Height : 480;

        // 先登记到插件上下文，再按需 Show。ShowOnCreate 由派生类在自己的构造函数末尾触发
        // （基类构造函数里不能调虚方法：那时派生类的字段还没初始化）。
        _context?.TrackWindow(this);
    }

    /// <summary>
    /// 窗口 id：停靠面板在 OBS 里的 id（也是 <c>QDockWidget</c> 的 objectName）。
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 窗口标题。可读可写；创建之后再写会把新标题同步给原生窗口。
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _title = value;

            // 没创建就等创建时用新标题；已释放就只记下来，都不抛。
            if (_created && !_disposeRequested)
                TryInvoke("设置窗口标题", () => SetTitleCore(value));
        }
    }

    /// <summary>
    /// 窗口页面的规范地址。一般形如 <c>obsharp://&lt;host&gt;/</c>（走 host 的 IPC 端点），
    /// 也可以是普通 URL（此时 <see cref="Host"/> 为 <see langword="null"/>，页面按普通网络访问）。
    /// </summary>
    public string Url => _url;

    /// <summary>
    /// 窗口页面所属的 host；页面是普通地址时为 <see langword="null"/>（host 与窗口解耦）。
    /// </summary>
    public ObsHost? Host => _host;

    /// <summary>
    /// 是否可见。还没创建 / 已释放 / 原生侧拿不到状态时都是 <see langword="false"/>。
    /// </summary>
    public bool IsVisible
    {
        get
        {
            if (!_created || _disposeRequested)
                return false;

            bool visible = false;
            TryInvoke("读取窗口可见状态", () => visible = GetVisibleCore());
            return visible;
        }
    }

    /// <summary>
    /// 窗口的当前宽度：创建时是初始宽度，工具窗口 <see cref="ObsToolWindow.Resize"/> 之后是最新值。
    /// </summary>
    protected int Width { get; set; }

    /// <summary>
    /// 窗口的当前高度：创建时是初始高度，工具窗口 <see cref="ObsToolWindow.Resize"/> 之后是最新值。
    /// </summary>
    protected int Height { get; set; }

    /// <summary>
    /// 是否置顶（只有工具窗口会用）。
    /// </summary>
    protected bool Topmost => _topmost;

    /// <summary>
    /// 是否已经请求销毁（<see cref="Dispose"/> 调过）。
    /// </summary>
    protected bool IsDisposed => _disposeRequested;

    /// <summary>
    /// 创建完之后的收尾：<see cref="ObsWindowOptions.ShowOnCreate"/> 为 <see langword="true"/> 时显示窗口。
    /// </summary>
    /// <param name="showOnCreate">窗口参数里的 <see cref="ObsWindowOptions.ShowOnCreate"/>。</param>
    /// <remarks>
    /// 由派生类在自己的构造函数最后调用：基类构造函数里不能调虚方法（派生类的字段还没初始化）。
    /// </remarks>
    private protected void ApplyShowOnCreate(bool showOnCreate)
    {
        if (showOnCreate)
            Show();
    }

    /// <summary>
    /// 显示窗口。第一次调用才创建浏览器并导航（可能阻塞几秒）；之后只是显示。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 创建失败（没有 obs-browser、CEF 没起来、OBS 拒绝了停靠面板 id……）只记警告并返回，
    /// 窗口对象保留，之后再调会重试。
    /// </para>
    /// <para>
    /// 另一个线程正在第一次 <see cref="Show"/>（还在等 CEF）时，这次调用直接返回：创建完
    /// 总会显示出来，排队没有意义。
    /// </para>
    /// </remarks>
    public void Show()
    {
        if (Interlocked.CompareExchange(ref _creating, 1, 0) != 0)
            return;

        try
        {
            if (_disposeRequested)
            {
                LogWarning($"窗口 \"{Id}\" 已经释放，Show() 被忽略。");
                return;
            }

            if (!_created && !CreateOnce())
                return;

            // 创建期间可能被 Dispose 了：finally 里会收尾，这里别再把它显示出来。
            if (_disposeRequested)
                return;

            TryInvoke("显示窗口", ShowCore);
        }
        finally
        {
            Volatile.Write(ref _creating, 0);

            // 创建 / 显示期间另一个线程调了 Dispose（它看到「创建中」，把清理留给了这里）。
            if (_disposeRequested)
                Dispose();
        }
    }

    /// <summary>
    /// 隐藏窗口（不销毁，浏览器和页面都还在）。
    /// </summary>
    public void Hide()
    {
        if (!_created || _disposeRequested)
            return;

        TryInvoke("隐藏窗口", HideCore);
    }

    /// <summary>
    /// 销毁窗口：停靠窗口先从 OBS 摘掉面板，再销毁原生窗口和浏览器。多次调用是安全的。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 故意没有终结器：原生窗口还活着的时候会一直握着托管回调，忘记 <see cref="Dispose"/>
    /// 只会泄漏原生对象，不会从 GC 线程去删一个 OBS 还引用着的 <c>QDockWidget</c>。
    /// </para>
    /// <para>
    /// 不阻塞：如果另一个线程正在第一次 <see cref="Show"/> 里等 CEF，清理交给它收尾。
    /// 所以 <see cref="Dispose"/> 可以在 Qt 主线程上安全调用。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        _disposeRequested = true;

        // 有线程正在建窗口：它创建完看到标记会自己收拾（并再调一次 Dispose）。
        if (Volatile.Read(ref _creating) != 0)
            return;

        CompleteDispose();
    }

    /// <summary>
    /// 真正释放原生对象（只干一次）。调用前必须保证没有线程在创建窗口。
    /// </summary>
    private void CompleteDispose()
    {
        if (Interlocked.Exchange(ref _disposeClaimed, 1) != 0)
            return;

        bool created = _created;
        _created = false;

        if (created)
            TryInvoke("把窗口从 OBS 摘掉", DetachCore);

        TryInvoke("销毁窗口", DisposeCore);

        // 从插件上下文的窗口列表里摘掉自己。不抛：插件正在卸载时这一步失败无所谓。
        try
        {
            _context?.UntrackWindow(this);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[OBSharp.SDK] 把窗口 \"{Id}\" 从插件上下文里摘除失败：{exception}");
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 按窗口参数拼浏览器创建参数：host 名、URL、由 host 推导出的 IPC 端点。
    /// </summary>
    private protected BrowserOptions CreateBrowserOptions() => new()
    {
        Host = _host?.Name,
        Url = _url,
        Endpoint = _endpoint,
        EndpointKind = OBSharpEndpointKind.Auto,
    };

    /// <summary>
    /// 第一次 <see cref="Show"/> 时初始化原生库并创建窗口。失败只记警告并返回 false。
    /// </summary>
    private bool CreateOnce()
    {
        string logTag = _context?.Name ?? "OBSharp";

        try
        {
            OBSharpResult result = OBSharpNative.Initialize(logTag);
            if (result is not OBSharpResult.Ok)
            {
                // 大多是没有 obs-browser：插件照常跑，只是没有这个窗口。
                LogWarning($"窗口 \"{Id}\" 创建不了：CEF 不可用（{result}）。");
                return false;
            }
        }
        catch (Exception exception)
        {
            LogWarning($"窗口 \"{Id}\" 初始化 OBSharp.Interop.Browser 失败：{exception.Message}");
            return false;
        }

        try
        {
            if (!CreateCore())
                return false;
        }
        catch (Exception exception)
        {
            LogWarning($"窗口 \"{Id}\" 创建失败：{exception.Message}");
            return false;
        }

        _created = true;
        return true;
    }

    /// <summary>
    /// 执行一个原生调用；失败只记警告。
    /// </summary>
    private protected void TryInvoke(string action, Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            LogWarning($"{action}失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 写一条 OBS 警告。拿不到 libobs（比如不在 OBS 进程里）时退回调试输出。
    /// </summary>
    private protected static void LogWarning(string message)
    {
        string line = "[OBSharp.SDK] " + message;
        try
        {
            ObsExtensions.Log(200, line);
        }
        catch
        {
            Debug.WriteLine(line);
        }
    }

    /// <summary>
    /// 建原生窗口 / 浏览器（第一次 <see cref="Show"/>）。成功返回 <see langword="true"/>。
    /// </summary>
    private protected abstract bool CreateCore();

    /// <summary>
    /// 显示原生窗口。
    /// </summary>
    private protected abstract void ShowCore();

    /// <summary>
    /// 隐藏原生窗口。
    /// </summary>
    private protected abstract void HideCore();

    /// <summary>
    /// 读原生窗口的可见状态。
    /// </summary>
    private protected abstract bool GetVisibleCore();

    /// <summary>
    /// 把标题同步给原生窗口。
    /// </summary>
    private protected abstract void SetTitleCore(string title);

    /// <summary>
    /// 销毁时先从 OBS 摘掉（只有停靠窗口需要，工具窗口什么都不做）。
    /// </summary>
    private protected virtual void DetachCore()
    {
    }

    /// <summary>
    /// 销毁原生窗口和浏览器。
    /// </summary>
    private protected abstract void DisposeCore();

    // 窗口的规范地址：没给 Url 时用 host 的地址；走 obsharp:// 时必须与 host 一致。
    private static string ResolveUrl(ObsWindowOptions options)
    {
        ObsHost? host = options.Host;
        string? url = string.IsNullOrEmpty(options.Url) ? host?.Url : options.Url;

        if (string.IsNullOrEmpty(url))
        {
            throw new ArgumentException(
                "窗口必须给 Url，或者给一个 Host（此时默认访问 host 的地址）。", nameof(options));
        }

        if (url.StartsWith("obsharp://", StringComparison.OrdinalIgnoreCase))
        {
            if (host is null)
            {
                throw new ArgumentException(
                    $"窗口地址 \"{url}\" 走 OB# host，必须同时给 Host（先注册 host 再开窗口）。", nameof(options));
            }

            // 地址里的 host 段必须与 Host.Name 一致，否则原生侧会拒绝创建。
            string expected = "obsharp://" + host.Name;
            bool matches = url.Length >= expected.Length
                && string.Compare(url, 0, expected, 0, expected.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (url.Length == expected.Length || url[expected.Length] is '/' or '?' or '#');

            if (!matches)
            {
                throw new ArgumentException(
                    $"窗口地址 \"{url}\" 里的 host 与 Host.Name（\"{host.Name}\"）不一致。", nameof(options));
            }
        }

        return url;
    }
}
