using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

/// <summary>
/// 极简 <see cref="ILoggerFactory"/>：造出来的 logger 直接把日志写进 OBS 的 <c>blog</c>。
/// 宿主不需要 DI、配置过滤和第三方 provider，所以只用
/// Microsoft.Extensions.Logging.Abstractions，不引实现包。
/// </summary>
internal sealed class OBSharpLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new OBSharpLogger(categoryName);

    // 宿主不挂 provider，过滤和落地都在 OBSharpLogger 里。
    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
