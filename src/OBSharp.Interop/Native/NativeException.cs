namespace OBSharp.Interop.Native;

/// <summary>
/// The native library returned a result code other than <see cref="NativeResult.Ok"/>.
/// </summary>
/// <remarks>
/// <para>
/// The ABI has no "get last error message" export, so the message always comes from the
/// caller: describe the failing operation at the catch site
/// (<c>"obsharp_scheme_register(https, foo.obsharp) failed"</c> is far more useful than
/// <c>"Unavailable"</c>).
/// </para>
/// <para>
/// <see cref="ThrowIfFailed(NativeResult, string?)"/> falls back to
/// <see cref="Enum.ToString()"/> when no message is supplied, so a call site that only has
/// a result code still throws something readable.
/// </para>
/// <para>
/// This type never escapes the assembly: it is caught and translated into
/// <see cref="OBSharp.Interop.UI.OBSharpNativeException"/> at the public API boundary.
/// </para>
/// </remarks>
[Serializable]
internal sealed class NativeException(NativeResult code, string? message) : Exception(message)
{
    /// <summary>
    /// The native result code.
    /// </summary>
    public NativeResult Result { get; } = code;

    /// <summary>
    /// Throws when <paramref name="result"/> is not <see cref="NativeResult.Ok"/>.
    /// </summary>
    /// <param name="result">The <c>int32_t</c> returned by the native call.</param>
    /// <param name="message">Description of the failing operation; falls back to the result code.</param>
    public static void ThrowIfFailed(int result, string? message = null)
        => ThrowIfFailed((NativeResult)result, message);

    /// <summary>
    /// Throws when <paramref name="result"/> is not <see cref="NativeResult.Ok"/>.
    /// </summary>
    /// <param name="result">The native result code.</param>
    /// <param name="message">Description of the failing operation; falls back to the result code.</param>
    public static void ThrowIfFailed(NativeResult result, string? message = null)
    {
        if (result is NativeResult.Ok)
            return;

        throw new NativeException(result, message ?? result.ToString());
    }
}
