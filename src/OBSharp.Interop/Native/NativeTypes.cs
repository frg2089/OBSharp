using System.Runtime.InteropServices;

namespace OBSharp.Interop.Native;

/// <summary>
/// Memory layouts of the callback and record structures passed across the C ABI.
/// </summary>
/// <remarks>
/// <para>
/// Field order and size must match the corresponding <c>struct</c> declarations in
/// <c>src/OBSharp.Interop.Native/include/OBSharp.Interop.Native.h</c> exactly.
/// </para>
/// <para>
/// The native library copies every one of these structures immediately on the call that
/// takes it, so callers may pass them by <see langword="in"/> and let the GC move them
/// afterwards.
/// </para>
/// </remarks>
internal static unsafe class NativeTypes
{
    /// <summary>
    /// <c>obsharp_cef_widget_callbacks</c>: title / URL notifications of a QCefWidget.
    /// </summary>
    /// <remarks>
    /// Both callbacks run on the Qt main thread and receive a borrowed UTF-8 pointer, which
    /// must be copied to a managed string inside the callback.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CefWidgetCallbacks
    {
        /// <summary>Opaque context forwarded to both callbacks.</summary>
        public nint User;

        /// <summary><c>(user, value)</c> when the document title changes.</summary>
        public delegate* unmanaged[Cdecl]<nint, byte*, void> OnTitleChanged;

        /// <summary><c>(user, value)</c> when the document URL changes.</summary>
        public delegate* unmanaged[Cdecl]<nint, byte*, void> OnUrlChanged;
    }

    /// <summary>
    /// <c>obsharp_window_callbacks</c>: close notification of a plain top-level window.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCallbacks
    {
        /// <summary>Opaque context forwarded to the callback.</summary>
        public nint User;

        /// <summary><c>(user)</c> when the user closes the window. May be <see langword="null"/>.</summary>
        public delegate* unmanaged[Cdecl]<nint, void> OnCloseRequested;
    }

    /// <summary>
    /// <c>obsharp_request_header</c>: one request or response header.
    /// </summary>
    /// <remarks>
    /// Both fields are borrowed NUL-terminated UTF-8 pointers that only live for the duration
    /// of the callback (or native call) they belong to.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RequestHeader
    {
        /// <summary>Header name (borrowed UTF-8).</summary>
        public byte* Name;

        /// <summary>Header value (borrowed UTF-8).</summary>
        public byte* Value;
    }

    /// <summary>
    /// <c>obsharp_resource_handler_callbacks</c>: the C# back end of a
    /// <c>CefResourceHandler</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every callback here runs on <b>CEF's IO thread</b>, never on the Qt main thread —
    /// do not touch Qt from them without marshalling.
    /// </para>
    /// <para>
    /// <c>handler</c> is the per-request handle (<c>CefResourceHandler*</c>). The C# side uses
    /// it as a key for per-request state and drops that state in <see cref="Destroy"/>.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResourceHandlerCallbacks
    {
        /// <summary>Opaque context forwarded to every callback.</summary>
        public nint User;

        /// <summary><c>(user, handler, method, url, headers, count, body, body_length)</c>.</summary>
        public delegate* unmanaged[Cdecl]<nint, nint, byte*, byte*, RequestHeader*, int, void*, int, int> OnOpen;

        /// <summary><c>(user, handler, response)</c>; returns the body length, or -1 if unknown.</summary>
        public delegate* unmanaged[Cdecl]<nint, nint, nint, long> GetResponseHeaders;

        /// <summary><c>(user, handler, buffer, size)</c>; &gt;0 bytes written, 0 no data yet, &lt;0 end.</summary>
        public delegate* unmanaged[Cdecl]<nint, nint, void*, int, int> Read;

        /// <summary><c>(user, handler, bytes_to_skip, skipped)</c>; &gt;0 done, 0 pending, &lt;0 failed.</summary>
        public delegate* unmanaged[Cdecl]<nint, nint, long, long*, int> Skip;

        /// <summary><c>(user, handler)</c> the request was cancelled.</summary>
        public delegate* unmanaged[Cdecl]<nint, nint, void> Cancel;

        /// <summary><c>(user, handler)</c> the handler is being destroyed.</summary>
        public delegate* unmanaged[Cdecl]<nint, nint, void> Destroy;
    }
}
