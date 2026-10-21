using Microsoft.Extensions.Logging;

using OBSharp.Interop.Native;

namespace OBSharp.Interop.UI;

/// <summary>
/// Routes log lines produced by the native library into <see cref="ILogger"/>.
/// </summary>
/// <remarks>
/// <para>
/// The native library never resolves libobs' <c>blog()</c> itself. It calls the function
/// pointer installed by <see cref="NativeApi.InstallLogSink"/>, which lands here. That keeps
/// "where do log lines go" in managed code, next to the <see cref="ILoggerFactory"/> and its
/// filters.
/// </para>
/// <para>
/// The sink is process-wide because the native library is. <see cref="Install"/> replaces it.
/// </para>
/// </remarks>
internal static class NativeLogSink
{
    /// <summary>Logger category used for lines that do not belong to a specific widget.</summary>
    private const string CategoryName = "OBSharp.Interop.Native";

    /// <summary>Guards <see cref="_logger"/>: the native side logs from any thread.</summary>
    private static readonly object Gate = new();

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
        NativeApi.InstallLogSink();

        lock (Gate)
        {
            _logger = logger;

            // The native library calls back through NativeApi; point it here. Idempotent —
            // assigning the same delegate again is harmless.
            NativeApi.LogSink = Write;
        }
    }

    /// <summary>
    /// Forwards one native log line to the installed logger.
    /// </summary>
    /// <param name="level">Severity chosen by the native side.</param>
    /// <param name="message">The already-formatted message.</param>
    /// <remarks>
    /// Called from the unmanaged callback in <see cref="NativeApi"/>, i.e. on whatever thread
    /// the native library happens to be running. Nothing here may throw.
    /// </remarks>
    internal static void Write(NativeLogLevel level, string message)
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

        LogLevel mapped = Map(level);

        // Bail out before formatting when the level is filtered away.
        if (!logger.IsEnabled(mapped))
            return;

        logger.Log(mapped, "{Message}", message);
    }

    /// <summary>
    /// Maps a native severity onto <see cref="LogLevel"/>.
    /// </summary>
    /// <remarks>
    /// Explicit switch on purpose: the native values mirror libobs' <c>blog()</c> constants and
    /// do not line up with <see cref="LogLevel"/>'s ordering.
    /// </remarks>
    private static LogLevel Map(NativeLogLevel level) => level switch
    {
        NativeLogLevel.Error => LogLevel.Error,
        NativeLogLevel.Warning => LogLevel.Warning,
        NativeLogLevel.Info => LogLevel.Information,
        NativeLogLevel.Debug => LogLevel.Debug,
        _ => LogLevel.Information,
    };
}
