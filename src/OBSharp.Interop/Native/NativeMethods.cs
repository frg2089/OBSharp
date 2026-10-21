using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OBSharp.Interop.Native;

/// <summary>
/// One-to-one P/Invoke declarations for <c>OBSharp.Interop.Native</c>: one method per
/// exported function, in header order.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>thin</b> binding layer. It adds nothing: no thread checks, no exception
/// translation, no caching. Convenience wrappers live in <see cref="NativeApi"/>; anything
/// with real behaviour lives in <see cref="OBSharp.Interop.UI.Internal"/> and
/// <see cref="OBSharp.Interop.UI"/>.
/// </para>
/// <para>
/// Everything here is <see langword="internal"/>. Native imports are an implementation
/// detail of <c>OBSharp.Interop</c> and must never appear in the public surface.
/// </para>
/// <para>
/// Three conventions:
/// </para>
/// <list type="bullet">
/// <item>
/// String parameters are NUL-terminated UTF-8. The native side copies them immediately, so
/// the managed buffer can be collected as soon as the call returns.
/// </item>
/// <item>
/// Returned <c>const char*</c> are all <b>borrowed</b>: owned by the native library and
/// invalidated by the next call of the same kind or by object destruction. They therefore
/// carry <see cref="OBSStringMarshaller"/>, which copies without freeing. The default
/// marshaller would <c>CoTaskMemFree</c> a pointer the runtime does not own, corrupting the
/// heap.
/// </item>
/// <item>
/// Returned handles (widget / window / dialog / handler) are borrowed Qt / CEF pointers. The
/// C# side decides when to destroy them; the native library adds no reference counting
/// (except <c>CefResourceHandler</c>, which CEF itself reference-counts).
/// </item>
/// </list>
/// <para>
/// Threading: everything except <c>obsharp_version</c>, <c>obsharp_set_log_callback</c>,
/// <c>obsharp_is_ui_thread</c>, <c>obsharp_find_module</c>, <c>obsharp_find_symbol</c> and
/// <c>obsharp_resource_handler_resume_*</c> requires the Qt main thread — go through
/// <see cref="NativeApi.InvokeUI"/> first.
/// </para>
/// <para>
/// Layout and signatures must stay in sync with
/// <c>src/OBSharp.Interop.Native/include/OBSharp.Interop.Native.h</c>.
/// <see cref="LibraryImportAttribute"/> resolves exports by name at run time only; a wrong
/// signature compiles fine and corrupts the stack at the call site.
/// </para>
/// </remarks>
internal static unsafe partial class NativeMethods
{
    /// <summary>The native library name, as passed to every <see cref="LibraryImportAttribute"/>.</summary>
    internal const string LibraryName = "OBSharp.Interop.Native";

    #region Version, logging and threads

    /// <summary>Human readable version of the loaded native library.</summary>
    /// <remarks>
    /// Static storage on the native side, but still treated as borrowed for consistency.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalUsing(typeof(OBSStringMarshaller))]
    internal static partial string obsharp_version();

    /// <summary>Installs the managed log sink. No-op if <paramref name="fn"/> is the default.</summary>
    /// <remarks>Any thread. Called once, lazily, the first time the UI layer needs the native library.</remarks>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_set_log_callback")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_set_log_callback(delegate* unmanaged[Cdecl]<NativeLogLevel, byte*, void> fn);

    /// <summary>Whether the calling thread is the Qt main thread.</summary>
    /// <remarks>Any thread.</remarks>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_is_ui_thread")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool obsharp_is_ui_thread();

    /// <summary>Runs <paramref name="fn"/> synchronously on the Qt main thread.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_invoke_ui")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int obsharp_invoke_ui(delegate* unmanaged[Cdecl]<nint, void> fn, nint user);

    #endregion

    #region Host symbol lookup

    /// <summary>Finds an already loaded module; returns 0 when it is not loaded.</summary>
    /// <remarks>Any thread. Never loads a module as a side effect.</remarks>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_find_module", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_find_module(string name);

    /// <summary>Resolves a symbol inside a module (0 = process-global lookup).</summary>
    /// <remarks>Any thread.</remarks>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_find_symbol", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_find_symbol(nint module, string name);

    #endregion

    #region obs-browser QCef panel

    /// <summary>Forwards to <c>QCef::init_browser()</c>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_qcef_init_browser")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool obsharp_qcef_init_browser(nint cef);

    /// <summary>Forwards to <c>QCef::initialized()</c>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_qcef_is_initialized")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool obsharp_qcef_is_initialized(nint cef);

    /// <summary>Forwards to <c>QCef::wait_for_browser_init()</c>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_qcef_wait_for_init")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool obsharp_qcef_wait_for_init(nint cef);

    /// <summary>
    /// Creates the QCefWidget and takes over its title / URL callbacks in the same call.
    /// </summary>
    /// <param name="cef">The <c>QCef*</c> panel interface.</param>
    /// <param name="parent">Parent <c>QWidget*</c>, or 0.</param>
    /// <param name="url">Initial URL, already mapped by the caller.</param>
    /// <param name="cookieManager"><c>QCefCookieManager*</c>, normally 0.</param>
    /// <param name="callbacks">Title / URL callbacks; the native side copies them immediately.</param>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_qcef_create_widget", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_qcef_create_widget(nint cef, nint parent, string url, nint cookieManager,
        in NativeTypes.CefWidgetCallbacks callbacks);

    #endregion

    #region QCefWidget operations

    /// <summary>Navigates the widget to <paramref name="url"/>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_set_url", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_cef_widget_set_url(nint widget, string url);

    /// <summary>Replaces the script injected before every document is created.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_set_startup_script",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_cef_widget_set_startup_script(nint widget, string? script);

    /// <summary>Allows or blocks popups.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_allow_all_popups")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_cef_widget_allow_all_popups(nint widget, [MarshalAs(UnmanagedType.I1)] bool allow);

    /// <summary>Reloads the current document.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_reload")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_cef_widget_reload(nint widget);

    /// <summary>Zooms: negative out, positive in, zero reset.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_zoom")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool obsharp_cef_widget_zoom(nint widget, int direction);

    /// <summary>Executes JavaScript in the current document.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_execute_script",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_cef_widget_execute_script(nint widget, string script);

    /// <summary>Tears the CEF browser down, leaving the QWidget alive.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_close_browser")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_cef_widget_close_browser(nint widget);

    /// <summary>Clears the title / URL callbacks passed at creation time.</summary>
    /// <remarks>
    /// Teardown only — re-subscribing still happens in
    /// <see cref="obsharp_qcef_create_widget"/>. Needed because a widget adopted by OBS's dock
    /// outlives the managed object, so the callbacks must be silenced before the
    /// <see cref="System.Runtime.InteropServices.GCHandle"/> is freed.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_cef_widget_detach_callbacks")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_cef_widget_detach_callbacks(nint widget);

    #endregion

    #region QWidget operations

    /// <summary>Resizes the widget.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_resize")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_resize(nint widget, int width, int height);

    /// <summary>Moves the widget.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_move")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_move(nint widget, int x, int y);

    /// <summary>Reads the current size. Either out pointer may be 0.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_size(nint widget, int* width, int* height);

    /// <summary>Shows or hides the widget.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_visible")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_set_visible(nint widget, [MarshalAs(UnmanagedType.I1)] bool visible);

    /// <summary>Whether the widget is visible.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_is_visible")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool obsharp_widget_is_visible(nint widget);

    /// <summary>Shows and raises the widget.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_show")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_show(nint widget);

    /// <summary>Hides the widget.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_hide")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_hide(nint widget);

    /// <summary>Schedules a repaint.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_update")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_update(nint widget);

    /// <summary>Destroys the widget once control returns to the event loop.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_delete_later")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_delete_later(nint widget);

    /// <summary>Reparents the widget; 0 detaches it.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_parent")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_set_parent(nint widget, nint parent);

    /// <summary>Current parent widget, or 0.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_parent")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_widget_parent(nint widget);

    /// <summary>Sets the window title.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_window_title",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_set_window_title(nint widget, string? title);

    /// <summary>Toggles the always-on-top window flag.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_topmost")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_set_topmost(nint widget, [MarshalAs(UnmanagedType.I1)] bool topmost);

    /// <summary>Pins the widget to a fixed size.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_fixed_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_set_fixed_size(nint widget, int width, int height);

    /// <summary>Makes the widget background translucent.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_translucent_background")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int obsharp_widget_set_translucent_background(nint widget);

    /// <summary>Paints the widget background with the given <c>0xAARRGGBB</c> colour.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_background_color")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int obsharp_widget_set_background_color(nint widget, uint argb);

    /// <summary>Places <paramref name="child"/> inside <paramref name="container"/>; 0 clears it.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_set_content")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_set_content(nint container, nint child);

    /// <summary>Finds a widget by OS window id.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_find")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_widget_find(nuint nativeId);

    /// <summary>Installs an event filter; returns the token used to remove it.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_install_event_filter")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_widget_install_event_filter(nint widget,
        delegate* unmanaged[Cdecl]<nint, nint, int, byte> fn, nint user);

    /// <summary>Removes an event filter installed earlier.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_widget_remove_event_filter")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_widget_remove_event_filter(nint widget, nint filter);

    #endregion

    #region Top-level window

    /// <summary>Creates a plain top-level window and takes over its close notification.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_window_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_window_create(in NativeTypes.WindowCallbacks callbacks);

    #endregion

    #region File dialog

    /// <summary>Creates a <c>QFileDialog</c> without showing it.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_file_dialog_create(nint parent);

    /// <summary>The current active window, or 0.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_active_window")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial nint obsharp_active_window();

    /// <summary>Sets the dialog title.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_title",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_title(nint dialog, string? title);

    /// <summary>Sets the initial directory.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_directory",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_directory(nint dialog, string? directory);

    /// <summary>Sets the Qt name filter.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_name_filter",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_name_filter(nint dialog, string? filter);

    /// <summary>Includes or excludes hidden and system files.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_show_hidden")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_show_hidden(nint dialog, [MarshalAs(UnmanagedType.I1)] bool show);

    /// <summary>Selects between open and save mode.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_accept_mode")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_accept_mode(nint dialog, NativeFileDialogAcceptMode acceptMode);

    /// <summary>Selects what the dialog allows the user to pick.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_file_mode")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_file_mode(nint dialog, NativeFileDialogFileMode fileMode);

    /// <summary>Appends a suffix to the typed file name on save.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_default_suffix",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_default_suffix(nint dialog, string? suffix);

    /// <summary>Restricts the dialog to directories only.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_set_show_dirs_only")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_set_show_dirs_only(nint dialog,
        [MarshalAs(UnmanagedType.I1)] bool value);

    /// <summary>Prefills the file name field.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_select_file",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_file_dialog_select_file(nint dialog, string? filename);

    /// <summary>Shows the dialog modelessly; completes exactly once through <paramref name="done"/>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_open")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int obsharp_file_dialog_open(nint dialog,
        delegate* unmanaged[Cdecl]<nint, nint, byte, void> done, nint user);

    /// <summary>Number of selected paths, read inside the completion callback.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_selected_count")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int obsharp_file_dialog_selected_count(nint dialog);

    /// <summary>One selected path, read inside the completion callback.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_file_dialog_selected_path")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalUsing(typeof(OBSStringMarshaller))]
    internal static partial string? obsharp_file_dialog_selected_path(nint dialog, int index);

    #endregion

    #region Scheme handler bridge

    /// <summary>Registers the scheme handler factory and takes over its callbacks.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_scheme_register", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int obsharp_scheme_register(string scheme, string domain,
        in NativeTypes.ResourceHandlerCallbacks callbacks);

    /// <summary>Sets the HTTP status code of the response.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_response_set_status")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_response_set_status(nint response, int status);

    /// <summary>Sets the HTTP reason phrase.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_response_set_status_text",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_response_set_status_text(nint response, string? text);

    /// <summary>Sets the MIME type; separate from the <c>Content-Type</c> header.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_response_set_mime_type",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_response_set_mime_type(nint response, string? mimeType);

    /// <summary>Replaces the whole response header map.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_response_set_headers")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_response_set_headers(nint response, NativeTypes.RequestHeader* headers,
        int headerCount);

    /// <summary>Resumes a read that returned <c>0</c>. Any thread.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_resource_handler_resume_read")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_resource_handler_resume_read(nint handler, int bytesAvailable);

    /// <summary>Resumes a skip that returned <c>0</c>. Any thread.</summary>
    [LibraryImport(LibraryName, EntryPoint = "obsharp_resource_handler_resume_skip")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void obsharp_resource_handler_resume_skip(nint handler, long bytesSkipped);

    #endregion
}
