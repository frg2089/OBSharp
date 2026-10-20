using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OBSharp;

/// <summary>
/// 建 <see cref="ObsBrowser"/> 的工厂。注册进 DI 之后，页面 / 服务可以按需拿一块浏览器，
/// 不用自己管 host 和 endpoint。
/// </summary>
/// <remarks>
/// 浏览器是<b>有状态的原生资源</b>（一块 CEF Widget），所以默认按 <b>Scoped</b> 注册：
/// 一个请求 / 一个作用域拿一块，作用域结束时自动 <see cref="ObsBrowser.Dispose"/>。
/// 要长期持有的（比如挂进停靠面板的那块）不要从 DI 拿，用
/// <see cref="ObsPluginContext.CreateBrowser"/> 显式建、自己管生命周期 —— 那种浏览器
/// 的 Widget 交给 OBS 之后生命周期已经不由我们决定了。
/// </remarks>
public interface IObsBrowserFactory
{
    /// <summary>
    /// 建一块浏览器。
    /// </summary>
    /// <param name="host">页面所属的 host。</param>
    /// <param name="url">初始地址；为 <see langword="null"/> 时用 host 的地址。</param>
    ObsBrowser Create(ObsHost host, string? url = null);
}

internal sealed class ObsBrowserFactory(ObsPluginContext context) : IObsBrowserFactory
{
    public ObsBrowser Create(ObsHost host, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        // 走 context 建：登记到插件名下，插件卸载时兜底释放。
        return context.CreateBrowser(host, url);
    }
}

/// <summary>
/// 把 OB# 的浏览器工厂注册进 DI 容器。
/// </summary>
public static class ObsServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IObsBrowserFactory"/>（Scoped：一个作用域一块浏览器，作用域结束时释放）。
    /// </summary>
    /// <param name="services">插件的服务集合，通常来自 <c>host.Builder.Services</c>。</param>
    /// <param name="context">插件环境，一般是 <see cref="ObsPlugin.Context"/>。</param>
    /// <returns>同一个 <paramref name="services"/>，方便链式调用。</returns>
    /// <remarks>
    /// <para>
    /// 用途：插件里用 DI 的地方（Blazor 组件、后台服务）可以直接注入
    /// <see cref="IObsBrowserFactory"/> 建浏览器，不必把 host / endpoint 传来传去。
    /// </para>
    /// <para>
    /// 注意浏览器要等 CEF 就绪才建得出来，可能阻塞几秒：别在 OBS 的 Qt 主线程上解析它。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">任一参数是 null。</exception>
    public static IServiceCollection AddObsBrowsers(this IServiceCollection services, ObsPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.TryAddSingleton(context);
        services.TryAddScoped<IObsBrowserFactory>(provider =>
            new ObsBrowserFactory(provider.GetRequiredService<ObsPluginContext>()));

        return services;
    }
}
