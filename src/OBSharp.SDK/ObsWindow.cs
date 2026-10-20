using System.Diagnostics;

using OBSharp.Interop.Browser;

namespace OBSharp;

/// <summary>
/// 一个普通顶层窗口（<c>Qt::Window</c>）：普通标题栏、进任务栏、可单独 Alt+Tab。
/// </summary>
/// <remarks>
/// <para>
/// 它<b>只是容器</b>，不含浏览器：内容由插件自己决定（通常是一块
/// <see cref="ObsBrowser.Widget"/>，也可以是任何 <c>QWidget</c>）。这和
/// <see cref="ObsBrowser"/> 的分工是刻意的 —— 同一块浏览器既能放进窗口，也能交给 OBS
/// 当停靠面板，不用为了换个位置重建。
/// </para>
/// <para>
/// 创建要在后台线程做（等 CEF 冷启动可能几秒，并且会同步切到 Qt 主线程）。
/// </para>
/// <para>
/// 用户点关闭按钮时窗口只会<b>隐藏</b>并触发 <see cref="CloseRequested"/>；窗口对象和
/// 里面的内容都还在。想真正销毁就 <see cref="Dispose"/>，之后要再显示得重新
/// <see cref="Create"/> 一个（关掉的原生窗口不能复用）。
/// </para>
/// </remarks>
public sealed class ObsWindow : IDisposable
{
    private readonly CefWindow? _window;
    private bool _disposed;

    private ObsWindow(CefWindow window)
    {
        _window = window;
        _window.CloseRequested += OnNativeCloseRequested;
    }

    /// <summary>
    /// 用户点了窗口的关闭按钮：原生侧已经把窗口隐藏了。
    /// </summary>
    /// <remarks>
    /// 在 Qt 主线程上触发。窗口对象没有销毁，内容也还在。
    /// </remarks>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 建一个普通顶层窗口。失败时抛 <see cref="OBSharpNativeException"/>。
    /// </summary>
    /// <param name="options">窗口参数。</param>
    public static ObsWindow Create(ObsWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        OBSharpResult result = OBSharpNative.Initialize(options.InitializeTag);
        if (result is not OBSharpResult.Ok)
            throw new OBSharpNativeException(result, $"OB# 浏览器不可用：{result}。");

        CefWindow window = CefWindow.Create(new WindowOptions
        {
            Title = options.Title,
            Width = options.Width,
            Height = options.Height,
            Topmost = options.Topmost,
            Resizable = options.Resizable,
        });

        return new ObsWindow(window);
    }

    /// <summary>
    /// 建一个普通顶层窗口，<b>不阻塞调用方</b>。
    /// </summary>
    /// <param name="options">窗口参数。</param>
    /// <remarks>
    /// 把「等 CEF」挪到线程池上，所以可以安全地在 OBS 的 Qt 主线程（比如工具菜单的
    /// 点击回调）里 await 它。
    /// </remarks>
    public static Task<ObsWindow> CreateAsync(ObsWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Create(options));
    }

    /// <summary>
    /// 窗口标题。
    /// </summary>
    public string Title
    {
        get => _window?.Title ?? string.Empty;
        set => _window?.Title = value;
    }

    /// <summary>
    /// 窗口尺寸。
    /// </summary>
    public (int Width, int Height) Size
    {
        get => _window?.Size ?? (0, 0);
        set => _window?.Size = value;
    }

    /// <summary>
    /// 是否可见。
    /// </summary>
    public bool IsVisible => _window?.IsVisible ?? false;

    /// <summary>
    /// 把内容放进窗口（通常是 <see cref="ObsBrowser.Widget"/>）。传 0 清空。
    /// </summary>
    /// <remarks>
    /// 窗口<b>不接管</b>内容的所有权：销毁窗口不会销毁它，得自己
    /// <see cref="ObsBrowser.Dispose"/>。
    /// </remarks>
    public nint Content
    {
        get => _window?.Content ?? 0;
        set => _window?.Content = value;
    }

    /// <summary>
    /// 窗口本身的 <c>QWidget*</c>。
    /// </summary>
    public nint Widget => _window?.Widget ?? 0;

    /// <summary>
    /// 显示窗口。
    /// </summary>
    public void Show() => _window?.Show();

    /// <summary>
    /// 隐藏窗口（不销毁，内容还在）。
    /// </summary>
    public void Hide() => _window?.Hide();

    /// <summary>
    /// 调整窗口大小。
    /// </summary>
    public void Resize(int width, int height) => _window?.SetSize(width, height);

    /// <summary>
    /// 销毁原生窗口。<b>不</b>销毁里面的内容。
    /// </summary>
    /// <remarks>
    /// 多次调用是安全的。销毁之后要再显示得重新 <see cref="Create"/>：关掉的原生窗口
    /// 不能复用（里面的 Qt 资源已经被回收了）。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _window?.CloseRequested -= OnNativeCloseRequested;

        try
        {
            _window?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[OBSharp.SDK] 销毁窗口失败：{exception}");
        }
    }

    private void OnNativeCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, e);
}
