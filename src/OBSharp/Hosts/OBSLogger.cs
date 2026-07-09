using System.Text;

using Microsoft.Extensions.Logging;

using ObsInterop;

namespace OBSharp.Hosts;

internal sealed class OBSLogger(string categoryName) : ILogger
{
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var scope = new Scope(state, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public unsafe void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        int log_level = logLevel switch
        {
            LogLevel.Trace => 500,
            LogLevel.Debug => ObsBase.LOG_DEBUG,
            LogLevel.Information => ObsBase.LOG_INFO,
            LogLevel.Warning => ObsBase.LOG_WARNING,
            LogLevel.Error => ObsBase.LOG_ERROR,
            LogLevel.Critical => 50,
            LogLevel.None => -1,
            _ => -1,
        };

        if (log_level is -1)
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

        byte[] bytes = Encoding.UTF8.GetBytes($"[OBSharp] {level,-5} {categoryName,-16}: {message}");
        fixed (byte* ptr = bytes)
            ObsBase.blog(log_level, (sbyte*)ptr);
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
