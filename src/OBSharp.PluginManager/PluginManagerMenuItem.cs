
using System.Runtime.InteropServices;

using OBSharp.Interop;

namespace OBSharp.PluginManager;

public sealed class PluginManagerMenuItem(IServiceProvider serviceProvider) : IHostedService
{
    /// <summary>
    /// 「工具」菜单和窗口标题上显示的文字。
    /// </summary>
    private const string Title = "OB# 插件管理器";

    private PluginManagerWindow? _window;
    private GCHandle? _handle;

    public IServiceProvider ServiceProvider => serviceProvider;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _handle = GCHandle.Alloc(this);
        ObsFrontend.AddToolsMenuItem(Title, Toggle, GCHandle.ToIntPtr(_handle.Value));

        return Task.CompletedTask;
    }

    private static void Toggle(nint arg0)
    {
        var handle = GCHandle.FromIntPtr(arg0);
        var menuItem = (PluginManagerMenuItem)handle.Target!;

        switch (menuItem._window)
        {
            case { IsVisible: true }:
                menuItem._window.Hide();
                break;
            case { IsVisible: false }:
                menuItem._window.Show();
                break;
            case null:
                menuItem._window = menuItem.ServiceProvider.GetRequiredService<PluginManagerWindow>();
                if (!menuItem._window.IsVisible)
                    menuItem._window.Show();
                break;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_handle.HasValue)
        {
            _handle.Value.Free();
            _handle = null;
        }
        return Task.CompletedTask;
    }

}
