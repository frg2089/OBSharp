using Microsoft.Extensions.Logging;

namespace OBSharp;

/// <summary>
/// 把插件的 .NET 日志接到 OBS 日志上（落地在 <see cref="ObsLogger"/>）。
/// <see cref="ObsPluginContext.CreateBuilder"/> 会把它注册进插件的 Web 应用，所以插件里
/// 通过 DI 拿到的 <see cref="ILogger"/> / <see cref="ILogger{T}"/> / <see cref="ILoggerFactory"/>
/// 写出来的日志都会落进 OBS 日志，每行以插件标识符开头；过滤按 .NET 自己的规则走
/// （即插件的 <c>appsettings.json</c> 里的 <c>Logging:LogLevel</c>）。
/// </summary>
internal sealed class ObsLoggerProvider(string pluginId) : ILoggerProvider, ISupportExternalScope
{
    // LoggerFactory 会把整份 factory 共用的 scope 提供者塞进来（要在它之后建的 logger 才拿得到，
    // 所以 CreateLogger 时现读）；在它塞进来之前先自己兜一个。
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new ObsLogger(pluginId, categoryName, _scopes);

    // provider 自己不留资源：日志是直接写给 libobs 的。
    public void Dispose()
    {
    }
}
