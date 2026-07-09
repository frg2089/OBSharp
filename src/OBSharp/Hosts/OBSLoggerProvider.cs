using Microsoft.Extensions.Logging;

namespace OBSharp.Hosts;

internal sealed class OBSLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new OBSLogger(categoryName);

    public void Dispose()
    {
    }
}
