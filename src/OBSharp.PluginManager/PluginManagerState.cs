namespace OBSharp.PluginManager;

/// <summary>
/// 面板的共享状态：SignalR 的交互式组件都是一次连接一个实例，但插件列表要跨组件
/// 保持一致，而且「启停插件」这种操作会影响整个宿主，所以放在单例里。
/// </summary>
/// <remarks>
/// <para>
/// 宿主在停用插件时会抱着它自己的锁（<c>OBSharpPluginManager._gate</c>），而插件管理器
/// 面板自己也是个插件；在面板里停用别的插件没问题，停用自己会先把整条 SignalR 连接断掉。
/// </para>
/// <para>
/// 这里不缓存 <c>Plugins</c>：每次都从宿主现取，保证看到的是最新状态（别的途径也能改
/// <c>plugins.ini</c>）。
/// </para>
/// </remarks>
public sealed class PluginManagerState
{
    private readonly Lock _gate = new();

    /// <summary>
    /// 最近一次操作的提示；交互式页面把它显示成一条消息条。
    /// </summary>
    public string? Message { get; private set; }

    /// <summary>
    /// 上一条提示是不是错误。
    /// </summary>
    public bool MessageIsError { get; private set; }

    /// <summary>
    /// 状态变化的通知：任意交互式组件订阅它来刷新界面。
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// 写一条成功提示。
    /// </summary>
    public void Info(string message) => Set(message, isError: false);

    /// <summary>
    /// 写一条错误提示。
    /// </summary>
    public void Fail(string message) => Set(message, isError: true);

    /// <summary>
    /// 清掉当前提示。
    /// </summary>
    public void Clear() => Set(null, isError: false);

    private void Set(string? message, bool isError)
    {
        lock (_gate)
        {
            Message = message;
            MessageIsError = isError;
        }

        Changed?.Invoke();
    }
}
