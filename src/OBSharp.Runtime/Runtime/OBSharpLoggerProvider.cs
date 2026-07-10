using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

internal sealed class OBSharpLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new OBSharpLogger(categoryName);

    public void Dispose()
    {
    }
}
