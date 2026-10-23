using Microsoft.Extensions.Options;

using OBSharp.Interop.UI;

namespace OBSharp.PluginManager;

public sealed partial class PluginManagerWindow : Window
{
    private readonly ILogger<PluginManagerWindow> _logger;
    private readonly bool _devTools;
    private WebView _webView;

    public PluginManagerWindow(ILogger<PluginManagerWindow> logger, IOptions<PluginManagerOptions> options)
        : base(GetOptions(), logger)
    {
        _logger = logger;
        _devTools = options.Value.DevTools;
        _webView = CreateWebView();
        SetContent(_webView.Widget);
        CloseRequested += OnClosing;
    }

    private void OnClosing(object? sender, EventArgs e)
    {
        SetContent(0);
        _webView.Dispose();
    }

    private static WindowOptions GetOptions() => new()
    {
        Title = "OB# 插件管理器",
        Width = 960,
        Height = 640,
        Visible = true,
    };

    private WebView CreateWebView() => new(
        new()
        {
            Host = "plugin-manager",
            DevTools = _devTools,
        }, _logger);

    public override int Show()
    {
        _webView.Dispose();
        _webView = CreateWebView();
        SetContent(_webView.Widget);
        return base.Show();
    }
}
