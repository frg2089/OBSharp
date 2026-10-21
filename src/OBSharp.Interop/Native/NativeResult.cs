namespace OBSharp.Interop.Native;

/// <summary>
/// Result code returned by the native library.
/// </summary>
/// <remarks>
/// Values must match <c>obsharp_result</c> in <c>OBSharp.Interop.Native.h</c> exactly.
/// Every export returning <c>int32_t</c> returns one of these; the binding layer converts
/// a non-<see cref="Ok"/> value into a <see cref="NativeException"/>.
/// </remarks>
internal enum NativeResult
{
    /// <summary>Success.</summary>
    Ok = 0,

    /// <summary>An argument was invalid (null pointer, empty string, ...).</summary>
    InvalidArgument = -1,

    /// <summary>A required host object is missing (obs-browser module, CEF, Qt application, ...).</summary>
    Unavailable = -2,

    /// <summary>The call was made on a thread that is not allowed for this function.</summary>
    WrongThread = -3,

    /// <summary>Any other internal failure (CEF task dispatch failed, registration failed, ...).</summary>
    Internal = -4,
}
