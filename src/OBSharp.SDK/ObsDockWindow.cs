using OBSharp.Interop;
using OBSharp.Interop.Browser;

namespace OBSharp;

/// <summary>
/// 一个停靠窗口：在 OBS 里是一个面板（<c>QDockWidget</c>，通过
/// <c>obs_frontend_add_custom_qdock()</c> 挂进主窗口），内含一块 CEF 浏览器。
/// </summary>
/// <remarks>
/// <para>
/// 创建顺序：原生侧先建 <c>QDockWidget</c>，然后在 Qt 主线程上把它的 Widget 交给
/// <c>obs_frontend_add_custom_qdock(id, widget)</c>；<see cref="ObsWindow.Dispose"/> 时先
/// <c>obs_frontend_remove_dock(id)</c> 把面板摘掉，再销毁原生窗口。
/// </para>
/// <para>
/// 面板能不能显示由 OBS 的停靠布局决定，所以 <see cref="ObsWindow.Show"/> 只是让 OBS 显示它；
/// 用户关掉面板是 OBS 的行为，不影响这个托管对象。
/// </para>
/// </remarks>
public sealed class ObsDockWindow : ObsWindow
{
    // 原生对象：只在 Show / Dispose 的串行路径里写，读的时候可能跨线程，所以是 volatile。
    private volatile CefDockWindow? _dock;
    private volatile bool _attached;

    internal ObsDockWindow(ObsWindowOptions options)
        : this(null, options)
    {
    }

    /// <summary>
    /// 建一个停靠窗口。由 <see cref="ObsPluginContext.AddDockWindow"/> 调用，插件不直接 new。
    /// </summary>
    /// <param name="context">插件上下文；窗口会登记到 <see cref="ObsPluginContext.Windows"/>。</param>
    /// <param name="options">窗口参数。</param>
    internal ObsDockWindow(ObsPluginContext? context, ObsWindowOptions options)
        : base(context, options)
    {
        ApplyShowOnCreate(options.ShowOnCreate);
    }

    /// <summary>
    /// 建原生停靠窗口并挂进 OBS（第一次 <see cref="ObsWindow.Show"/>）。
    /// </summary>
    private protected override bool CreateCore()
    {
        var options = new DockWindowOptions
        {
            Id = Id,
            Title = Title,
            Visible = false,
            Width = Width,
            Height = Height,
            Browser = CreateBrowserOptions(),
        };

        CefDockWindow dock = CefDockWindow.Create(options);

        // 挂进 OBS：AddCustomQdock 要在 Qt 主线程上调，并且 Widget 得是刚建出来那个。
        bool added = false;
        try
        {
            OBSharpNative.InvokeUi(() =>
            {
                nint widget = dock.Widget;
                if (widget == 0)
                {
                    LogWarning($"停靠窗口 \"{Id}\" 没有 Widget，挂不进 OBS。");
                    return;
                }

                added = ObsFrontend.AddCustomQdock(Id, widget);
            });
        }
        catch (Exception exception)
        {
            LogWarning($"把停靠窗口 \"{Id}\" 挂进 OBS 失败：{exception.Message}");
        }

        if (!added)
        {
            // OBS 没收养它（id 被占了，或者根本不在 OBS 里）：自己收拾掉，免得泄漏。
            dock.Dispose();
            return false;
        }

        _dock = dock;
        _attached = true;
        return true;
    }

    /// <summary>
    /// 显示面板。
    /// </summary>
    private protected override void ShowCore() => _dock?.Show();

    /// <summary>
    /// 隐藏面板（OBS 那边只是把它收起来，不销毁）。
    /// </summary>
    private protected override void HideCore() => _dock?.Hide();

    /// <summary>
    /// 读面板可见状态。
    /// </summary>
    private protected override bool GetVisibleCore() => _dock?.IsVisible ?? false;

    /// <summary>
    /// 同步面板标题。
    /// </summary>
    private protected override void SetTitleCore(string title)
    {
        if (_dock is not null)
            _dock.Title = title;
    }

    /// <summary>
    /// 从 OBS 摘掉面板。摘不掉（比如进程正在退出）只记警告：面板留在 OBS 里，
    /// 之后销毁原生窗口的风险由 OBS 自己承担，但至少不抛。
    /// </summary>
    private protected override void DetachCore()
    {
        if (!_attached)
            return;

        try
        {
            OBSharpNative.InvokeUi(() => ObsFrontend.RemoveDock(Id));
        }
        catch (Exception exception)
        {
            LogWarning($"从 OBS 摘掉停靠窗口 \"{Id}\" 失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 销毁原生停靠窗口和里面的浏览器。
    /// </summary>
    private protected override void DisposeCore()
    {
        CefDockWindow? dock = _dock;
        _dock = null;
        _attached = false;
        dock?.Dispose();
    }
}
