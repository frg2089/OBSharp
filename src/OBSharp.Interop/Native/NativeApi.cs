using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Native;

/// <summary>
/// Thin convenience layer over <see cref="NativeMethods"/>: thread marshalling, exception
/// translation, and the log sink the native library calls back into.
/// </summary>
/// <remarks>
/// <para>
/// This type stays deliberately thin. It only does what must be done correctly on <b>every</b>
/// call; nothing here encodes behaviour. Host resolution, URL mapping, HTTP framing and the
/// rest live in <see cref="OBSharp.Interop.UI.Internal"/>.
/// </para>
/// <para>
/// Everything in this namespace is <see langword="internal"/>: native imports are an
/// implementation detail of <c>OBSharp.Interop</c> and never appear in its public surface.
/// </para>
/// </remarks>
internal static unsafe class NativeApi
{
    /// <summary>
    /// Receives every log line produced by the native library.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by <see cref="OBSharp.Interop.UI.NativeLogSink"/> the first time the host supplies an
    /// <see cref="Microsoft.Extensions.Logging.ILogger"/>.
    /// </para>
    /// <para>
    /// A plain delegate rather than a dependency on the logging abstractions: this namespace
    /// only moves strings. Mapping to a logger happens one layer up, where the logger lives.
    /// </para>
    /// </remarks>
    internal static Action<NativeLogLevel, string>? LogSink { get; set; }

    /// <summary>
    /// Version of the loaded native library.
    /// </summary>
    /// <remarks>
    /// The first thing to check when a rebuilt library appears to have no effect: the deployed
    /// directory may contain more than one copy.
    /// </remarks>
    internal static string Version => NativeMethods.obsharp_version();

    /// <summary>
    /// Whether the calling thread is the Qt main thread.
    /// </summary>
    /// <remarks>
    /// Most native calls require the main thread. Prefer <see cref="InvokeUI"/> over testing
    /// this directly.
    /// </remarks>
    internal static bool IsUIThread => NativeMethods.obsharp_is_ui_thread();

    /// <summary>
    /// Runs <paramref name="action"/> synchronously on the Qt main thread.
    /// </summary>
    /// <param name="action">The work; exceptions thrown inside are rethrown on the caller's thread.</param>
    /// <exception cref="NativeException">The native side could not reach the Qt main thread.</exception>
    /// <remarks>
    /// <para>
    /// Calls straight through when already on the main thread. Otherwise the native side posts
    /// with <c>Qt::BlockingQueuedConnection</c>, so this returns only once
    /// <paramref name="action"/> has completed. Never block the main thread waiting for
    /// something that itself needs the main thread — that deadlocks.
    /// </para>
    /// <para>
    /// Exceptions must not cross an <see cref="UnmanagedCallersOnlyAttribute"/> boundary, so
    /// the thunk stashes them and <see cref="ExceptionDispatchInfo"/> rethrows here, preserving
    /// the original stack trace.
    /// </para>
    /// </remarks>
    internal static void InvokeUI(Action action)
    {
        if (IsUIThread)
        {
            action();
            return;
        }

        UiInvocation invocation = new() { Action = action };
        GCHandle handle = GCHandle.Alloc(invocation);
        try
        {
            int result = NativeMethods.obsharp_invoke_ui(&NativeInvokeUI, GCHandle.ToIntPtr(handle));
            NativeException.ThrowIfFailed(result, "obsharp_invoke_ui failed: the Qt main thread could not be reached.");
        }
        finally
        {
            handle.Free();
        }

        // The blocking post guarantees the action has finished by now.
        if (invocation.Error is not null)
            ExceptionDispatchInfo.Capture(invocation.Error).Throw();
    }

    /// <summary>
    /// Finds an already loaded module; returns 0 when it is not loaded.
    /// </summary>
    /// <param name="name">Module file name, e.g. <c>obs.dll</c> or <c>libobs.so.0</c>.</param>
    /// <remarks>Any thread. Never loads a module as a side effect.</remarks>
    internal static nint FindModule(string name) => NativeMethods.obsharp_find_module(name);

    /// <summary>
    /// Resolves a symbol inside a module; <paramref name="module"/> 0 means process-global.
    /// </summary>
    /// <remarks>Any thread. The result can be cast to a <c>delegate* unmanaged[Cdecl]</c>.</remarks>
    internal static nint FindSymbol(nint module, string name) => NativeMethods.obsharp_find_symbol(module, name);

    /// <summary>
    /// Installs the native log callback, at most once per process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called lazily from the UI layer the first time it actually needs the native library.
    /// Deliberately <b>not</b> a <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/>:
    /// <c>OBSharp.Interop</c> is also consumed by the NativeAOT loader, which only uses the libobs
    /// bindings in the root namespace and never ships <c>OBSharp.Interop.Native</c>. A module
    /// initializer would run on any type touch, P/Invoke a library that is not there, and — just
    /// as bad — pin the whole native binding surface against trimming.
    /// </para>
    /// <para>
    /// Idempotent and thread-safe: the UI layer may reach this from several threads at once
    /// during startup.
    /// </para>
    /// </remarks>
    internal static void InstallLogSink()
    {
        if (Interlocked.Exchange(ref s_logSinkInstalled, 1) is not 0)
            return;

        NativeMethods.obsharp_set_log_callback(&OnNativeLog);
    }

    /// <summary>0 until <see cref="InstallLogSink"/> has run; 1 afterwards.</summary>
    private static int s_logSinkInstalled;

    /// <summary>
    /// The native library's log entry point.
    /// </summary>
    /// <param name="level">Severity chosen by the native side.</param>
    /// <param name="message">Borrowed UTF-8; copied before this method returns.</param>
    /// <remarks>
    /// Runs on whichever thread produced the line, including CEF's IO thread. Nothing here may
    /// throw across the native boundary.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeLog(NativeLogLevel level, byte* message)
    {
        if (message is null)
            return;

        string? text = Marshal.PtrToStringUTF8((nint)message);
        if (text is null)
            return;

        Action<NativeLogLevel, string>? sink = LogSink;
        if (sink is null)
        {
            // Only reachable if the UI layer has not attached a logger yet, or if a host
            // deliberately unsubscribes.
            System.Diagnostics.Debug.WriteLine($"[OBSharp.Interop.Native] {level}: {text}");
            return;
        }

        // A failing logging subscriber must never propagate into the native stack.
        try
        {
            sink(level, text);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Native log sink failed: {exception}");
        }
    }

    /// <summary>State handed to the Qt-main-thread thunk.</summary>
    private sealed class UiInvocation
    {
        /// <summary>The work to run.</summary>
        public required Action Action { get; init; }

        /// <summary>Exception thrown by <see cref="Action"/>, if any.</summary>
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// Thunk invoked by the native side on the Qt main thread.
    /// </summary>
    /// <param name="handle">A <see cref="GCHandle"/> wrapping a <see cref="UiInvocation"/>.</param>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void NativeInvokeUI(nint handle)
    {
        if (GCHandle.FromIntPtr(handle).Target is not UiInvocation invocation)
            return;

        // An exception must never cross the native stack; stash it and rethrow on the caller.
        try
        {
            invocation.Action?.Invoke();
        }
        catch (Exception exception)
        {
            invocation.Error = exception;
        }
    }
}
