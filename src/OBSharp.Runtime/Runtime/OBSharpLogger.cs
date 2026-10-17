using System.Diagnostics;

using Microsoft.Extensions.Logging;

using OBSharp.Interop;

namespace OBSharp.Runtime;

internal sealed class OBSharpLogger(string categoryName) : ILogger
{
    public const int LOG_ERROR = 100;
    public const int LOG_WARNING = 200;
    public const int LOG_INFO = 300;
    public const int LOG_DEBUG = 400;

    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var scope = new Scope(state, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        int obsLogLevel = logLevel switch
        {
            LogLevel.Trace => 500,
            LogLevel.Debug => LOG_DEBUG,
            LogLevel.Information => LOG_INFO,
            LogLevel.Warning => LOG_WARNING,
            LogLevel.Error => LOG_ERROR,
            LogLevel.Critical => 50,
            LogLevel.None => -1,
            _ => -1,
        };

        if (obsLogLevel is -1)
            return;

        string level = logLevel switch
        {
            LogLevel.Trace => "[TRACE]",
            LogLevel.Debug => "[DEBUG]",
            LogLevel.Information => "[INFO]",
            LogLevel.Warning => "[WARN]",
            LogLevel.Error => "[ERROR]",
            LogLevel.Critical => "[CRIT]",
            _ => string.Empty,
        };

        string message = formatter(state, exception);

        var scope = CurrentScope.Value;
        while (scope != null)
        {
            message = $"{scope.State}: {message}";
            scope = scope.Parent;
        }

        string text = $"[OBSharp] {level,-5} {categoryName,-16}: {message}";
        Debug.WriteLine(text);
        ObsExtensions.Log(obsLogLevel, text);
    }

    private sealed class Scope(object state, Scope? parent) : IDisposable
    {
        public object State { get; } = state;
        public Scope? Parent { get; } = parent;

        public void Dispose()
        {
            CurrentScope.Value = Parent;
        }
    }
}
