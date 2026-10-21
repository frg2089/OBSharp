namespace OBSharp.Interop.Native;

/// <summary>
/// Log levels understood by the native library.
/// </summary>
/// <remarks>
/// <para>
/// The numeric values match libobs' <c>blog()</c> constants (<c>util/base.h</c>), so the
/// level can be forwarded to the host without a second mapping table.
/// </para>
/// <para>
/// The values are <b>not</b> ordered by severity — that is a libobs historical artifact
/// (<see cref="Error"/> = 500, <see cref="Debug"/> = 400, <see cref="Info"/> = 300,
/// <see cref="Warning"/> = 200). Never compare them.
/// </para>
/// </remarks>
internal enum NativeLogLevel
{
    /// <summary>libobs <c>LOG_ERROR</c> (500).</summary>
    Error = 500,

    /// <summary>libobs <c>LOG_WARNING</c> (200).</summary>
    Warning = 200,

    /// <summary>libobs <c>LOG_INFO</c> (300).</summary>
    Info = 300,

    /// <summary>libobs <c>LOG_DEBUG</c> (400).</summary>
    Debug = 400,
}
