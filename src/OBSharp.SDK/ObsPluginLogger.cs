using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

using OBSharp.Interop;

namespace OBSharp;

/// <summary>
/// 插件日志的落地：把 .NET 的 <see cref="LogLevel"/> 映射到 OBS 的 log level 写进 OBS 日志，
/// 每行是 <c>[插件标识符] [级别] 分类: 消息</c>（跟宿主的 <c>[OBSharp] ...</c> 一个形状），
/// scope 串在消息前面。
/// </summary>
internal sealed class ObsLogger(string pluginId, string categoryName, IExternalScopeProvider scopes) : ILogger
{
    // libobs 的 log level（blog 的第一个参数）：LOG_ERROR / LOG_WARNING / LOG_INFO / LOG_DEBUG / LOG_LOG。
    private const int ObsLogError = 100;
    private const int ObsLogWarning = 200;
    private const int ObsLogInfo = 300;
    private const int ObsLogDebug = 400;

    // 过滤是 LoggerFactory 按 Logging:LogLevel 做掉的，到这里就写。
    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => scopes.Push(state);

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
            LogLevel.Debug => ObsLogDebug,
            LogLevel.Information => ObsLogInfo,
            LogLevel.Warning => ObsLogWarning,
            LogLevel.Error => ObsLogError,
            LogLevel.Critical => 50,
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

        StringBuilder? text = new($"[{pluginId}] {level,-5} {categoryName,-16}: ");
        scopes.ForEachScope(static (scope, builder) => builder.Append(scope).Append(": "), text);
        text.Append(formatter(state, exception));

        string line = text.ToString();
        Debug.WriteLine(line);
        Obs.Log(obsLogLevel, line);

        // The exception itself was being dropped: formatter() renders the message only, so a
        // failed plugin load produced one line with no type, message or stack. That is exactly
        // the case where the detail matters most, so emit it on its own lines.
        if (exception is not null)
        {
            foreach (string detail in Describe(exception))
                Obs.Log(obsLogLevel, $"[{pluginId}] {level,-5} {categoryName,-16}: {detail}");
        }
    }

    /// <summary>
    /// Renders an exception (and its inner exceptions) as indented lines.
    /// </summary>
    /// <remarks>
    /// Kept multi-line rather than one blob: OBS's log is line-oriented, and a 40-line stack
    /// trace prefixed on every line is far easier to read than one enormous line. Inner
    /// exceptions are included because loader failures are almost always wrapped
    /// (TargetInvocationException -> FileNotFoundException -> ...).
    /// </remarks>
    private static IEnumerable<string> Describe(Exception exception)
    {
        var depth = 0;
        for (Exception? current = exception; current is not null; current = current.InnerException, depth++)
        {
            string indent = new(' ', depth * 2);
            yield return $"{indent}{current.GetType().FullName}: {current.Message}";

            if (current.StackTrace is { Length: not 0 } stack)
            {
                foreach (string frame in stack.Split('\n'))
                    yield return $"{indent}  {frame.TrimEnd()}";
            }
        }
    }
}
