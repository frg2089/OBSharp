using Microsoft.Extensions.Logging;

using OBSharp.Interop.Abi;

namespace OBSharp.Interop.UI;

/// <summary>
/// Routes log lines produced by the native library into <see cref="ILogger"/>.
/// </summary>
/// <remarks>
/// <para>
/// The native library never resolves libobs' <see cref="object.ToString"/>-style logging itself.
/// It calls the function pointer installed by <see cref="AbiApi.InstallLogSink"/>, which lands
/// here. That keeps "where do log lines go" in managed code, next to the
/// <see cref="ILogger"/> and its filters.
/// </para>
/// <para>
/// The sink is process-wide because the native library is. <see cref="Install"/> replaces it.
/// </para>
/// <para>
/// Message text arrives pre-formatted from the native side, so every
/// <see cref="LoggerMessageAttribute"/> here takes the whole line as a single <c>{Message}</c>
/// placeholder. Level dispatch is an explicit switch because the native levels mirror libobs'
/// <see cref="AbiLogLevel"/> constants and do not line up with <see cref="LogLevel"/>.
/// </para>
/// </remarks>
internal static partial class NativeLogSink
{
    /// <summary>Guards <see cref="_logger"/>: the native side logs from any thread.</summary>
    private static readonly Lock Gate = new();

    /// <summary>Current sink; <see langword="null"/> until <see cref="Install"/> runs.</summary>
    private static ILogger? _logger;

    /// <summary>
    /// Points the native log callback at <paramref name="logger"/>'s category.
    /// </summary>
    /// <param name="logger">Logger that receives every native log line.</param>
    /// <remarks>
    /// <para>
    /// Safe to call repeatedly; the most recent logger wins. Called from the public
    /// <see cref="WebView"/> and <see cref="Window"/> constructors, because that is the earliest
    /// point at which the host's logger is available.
    /// </para>
    /// <para>
    /// Takes an <see cref="ILogger"/> rather than an <see cref="ILoggerFactory"/> because the
    /// public constructors already receive one, and reusing it keeps the native lines in the
    /// same category the caller picked.
    /// </para>
    /// <para>
    /// This is also where the native log callback gets installed. It has to happen lazily rather
    /// than in a module initializer: the same assembly is linked into the NativeAOT loader, which
    /// never loads the native library. Reaching this method means the caller is about to create a
    /// browser or a window, so the native library is definitely needed.
    /// </para>
    /// </remarks>
    internal static void Install(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // Install the native callback once, before the first native log line can be produced.
        // A missing native library is a real failure here (the caller is about to P/Invoke it
        // anyway), so let the exception surface instead of hiding it.
        AbiApi.InstallLogSink();

        lock (Gate)
        {
            _logger = logger;

            // The native library calls back through Abi; point it here. Idempotent —
            // assigning the same delegate again is harmless.
            AbiApi.LogSink = Write;
        }
    }

    /// <summary>
    /// Forwards one native log line to the installed logger.
    /// </summary>
    /// <param name="level">Severity chosen by the native side.</param>
    /// <param name="message">The already-formatted message.</param>
    /// <remarks>
    /// Called from the unmanaged callback in <see cref="Abi"/>, i.e. on whatever thread
    /// the native library happens to be running. Nothing here may throw.
    /// </remarks>
    internal static void Write(AbiLogLevel level, string message)
    {
        ILogger? logger;
        lock (Gate)
            logger = _logger;

        if (logger is null)
        {
            // Nothing is listening yet. The native side already mirrored the line to stderr
            // during that window, so dropping it here loses nothing.
            return;
        }

        switch (level)
        {
            case AbiLogLevel.Error:
                LogNativeError(logger, message);
                break;

            case AbiLogLevel.Warning:
                LogNativeWarning(logger, message);
                break;

            case AbiLogLevel.Debug:
                LogNativeDebug(logger, message);
                break;

            case AbiLogLevel.Info:
            default:
                LogNativeInformation(logger, message);
                break;
        }
    }

    /// <summary>Writes an information-level line that originated in managed code.</summary>
    /// <remarks>
    /// Used by types that have no <see cref="ILogger"/> of their own (the static bridge and the
    /// static file picker). Routes through the same sink so every line still lands in one place.
    /// </remarks>
    internal static void WriteInformation(string message) => Write(AbiLogLevel.Info, message);

    /// <summary>Writes a warning-level line that originated in managed code.</summary>
    internal static void WriteWarning(string message) => Write(AbiLogLevel.Warning, message);

    /// <summary>Writes a debug-level line that originated in managed code.</summary>
    internal static void WriteDebug(string message) => Write(AbiLogLevel.Debug, message);

    /// <summary>Writes an error-level line that originated in managed code.</summary>
    internal static void WriteError(string message) => Write(AbiLogLevel.Error, message);

    /// <summary>Writes one line from the native library at error level.</summary>
    [LoggerMessage(LogLevel.Error, "Native: {Message}")]
    private static partial void LogNativeError(ILogger logger, string message);

    /// <summary>Writes one line from the native library at warning level.</summary>
    [LoggerMessage(LogLevel.Warning, "Native: {Message}")]
    private static partial void LogNativeWarning(ILogger logger, string message);

    /// <summary>Writes one line from the native library at information level.</summary>
    [LoggerMessage(LogLevel.Information, "Native: {Message}")]
    private static partial void LogNativeInformation(ILogger logger, string message);

    /// <summary>Writes one line from the native library at debug level.</summary>
    [LoggerMessage(LogLevel.Debug, "Native: {Message}")]
    private static partial void LogNativeDebug(ILogger logger, string message);
}
