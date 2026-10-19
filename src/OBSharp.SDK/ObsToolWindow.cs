using System.Diagnostics;

using OBSharp.Interop.Browser;

namespace OBSharp;

/// <summary>
/// 一个工具窗口：独立的顶层窗口（原生侧 <c>Qt::Window | Qt::Tool</c>，可置顶），
/// 内含一块 CEF 浏览器。
/// </summary>
/// <remarks>
/// <para>
/// 用户点窗口的关闭按钮时，原生侧只把窗口隐藏，然后触发 <see cref="CloseRequested"/>
/// （在 Qt 主线程上），窗口对象不会自动销毁：要真正释放请调
/// <see cref="ObsWindow.Dispose"/>（插件卸载时 <see cref="ObsPluginContext"/> 会自动做）。
/// </para>
/// <para>
/// 第一次 <see cref="ObsWindow.Show"/> 才创建浏览器并导航，之后 <see cref="ObsWindow.Show"/> /
/// <see cref="ObsWindow.Hide"/> 只是显示 / 隐藏。
/// </para>
/// </remarks>
public sealed class ObsToolWindow : ObsWindow
{
    // 原生对象：只在 Show / Dispose 的串行路径里写，读的时候可能跨线程，所以是 volatile。
    private volatile CefToolWindow? _tool;

    internal ObsToolWindow(ObsWindowOptions options)
        : this(null, options)
    {
    }

    /// <summary>
    /// 建一个工具窗口。由 <see cref="ObsPluginContext.AddToolWindow"/> 调用，插件不直接 new。
    /// </summary>
    /// <param name="context">插件上下文；窗口会登记到 <see cref="ObsPluginContext.Windows"/>。</param>
    /// <param name="options">窗口参数。</param>
    internal ObsToolWindow(ObsPluginContext? context, ObsWindowOptions options)
        : base(context, options)
    {
        ApplyShowOnCreate(options.ShowOnCreate);
    }

    /// <summary>
    /// 用户点了窗口的关闭按钮：原生侧已经隐藏了窗口，不会销毁。
    /// </summary>
    /// <remarks>
    /// 在 Qt 主线程上触发，处理函数里不要做阻塞的事（尤其不要同步等主线程）；
    /// 只是关掉窗口的话不处理也可以，窗口下次 <see cref="ObsWindow.Show"/> 还能出来。
    /// </remarks>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 调整窗口大小，单位是像素。
    /// </summary>
    /// <remarks>
    /// 窗口还没创建（第一次 <see cref="ObsWindow.Show"/> 之前）时只记下来，创建时按新尺寸建。
    /// </remarks>
    /// <param name="width">新宽度，必须大于 0。</param>
    /// <param name="height">新高度，必须大于 0。</param>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        // 还没创建就先记下来，第一次 Show 建窗口时按这个尺寸建。
        Width = width;
        Height = height;

        if (IsDisposed || _tool is null)
            return;

        TryInvoke("调整工具窗口大小", () => _tool.SetSize(width, height));
    }

    /// <summary>
    /// 建原生工具窗口（第一次 <see cref="ObsWindow.Show"/>）。
    /// </summary>
    private protected override bool CreateCore()
    {
        var options = new ToolWindowOptions
        {
            Title = Title,
            Visible = false,
            Width = Width,
            Height = Height,
            Topmost = Topmost,
            Resizable = true,
            Browser = CreateBrowserOptions(),
        };

        CefToolWindow tool = CefToolWindow.Create(options);
        tool.CloseRequested += OnNativeCloseRequested;
        _tool = tool;
        return true;
    }

    /// <summary>
    /// 显示窗口。
    /// </summary>
    private protected override void ShowCore() => _tool?.Show();

    /// <summary>
    /// 隐藏窗口（不销毁，页面和浏览器都还在）。
    /// </summary>
    private protected override void HideCore() => _tool?.Hide();

    /// <summary>
    /// 读窗口可见状态。
    /// </summary>
    private protected override bool GetVisibleCore() => _tool?.IsVisible ?? false;

    /// <summary>
    /// 同步窗口标题。
    /// </summary>
    private protected override void SetTitleCore(string title)
    {
        if (_tool is not null)
            _tool.Title = title;
    }

    /// <summary>
    /// 销毁原生工具窗口和里面的浏览器。
    /// </summary>
    private protected override void DisposeCore()
    {
        CefToolWindow? tool = _tool;
        _tool = null;

        if (tool is null)
            return;

        try
        {
            tool.CloseRequested -= OnNativeCloseRequested;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[OBSharp.SDK] 摘掉工具窗口 \"{Id}\" 的关闭回调失败：{exception}");
        }

        tool.Dispose();
    }

    // 原生回调在 Qt 主线程上；这里只转发，不碰原生对象。
    private void OnNativeCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, e);
}
