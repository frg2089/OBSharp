using Microsoft.Extensions.Logging;

namespace OBSharp;

internal sealed class OBSLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new OBSLogger(categoryName);

    public void Dispose()
    {
    }
}
