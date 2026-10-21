namespace OBSharp.Interop.Native;

/// <summary>
/// Mirrors <c>obsharp_file_dialog_accept_mode</c>: whether the dialog opens or saves.
/// </summary>
/// <remarks>
/// Values must match the C enum exactly; they are passed straight through to
/// <see cref="NativeMethods.obsharp_file_dialog_set_accept_mode"/>.
/// </remarks>
internal enum NativeFileDialogAcceptMode
{
    /// <summary>Pick an existing file (<c>QFileDialog::AcceptOpen</c>).</summary>
    Open = 0,

    /// <summary>Pick a save target (<c>QFileDialog::AcceptSave</c>).</summary>
    Save = 1,
}

/// <summary>
/// Mirrors <c>obsharp_file_dialog_file_mode</c>: what the user is allowed to select.
/// </summary>
/// <remarks>
/// Values must match the C enum exactly; they are passed straight through to
/// <see cref="NativeMethods.obsharp_file_dialog_set_file_mode"/>.
/// </remarks>
internal enum NativeFileDialogFileMode
{
    /// <summary>Exactly one existing file (<c>QFileDialog::ExistingFile</c>).</summary>
    ExistingFile = 0,

    /// <summary>One or more existing files (<c>QFileDialog::ExistingFiles</c>).</summary>
    ExistingFiles = 1,

    /// <summary>Any file name, existing or not (<c>QFileDialog::AnyFile</c>).</summary>
    AnyFile = 2,

    /// <summary>A directory (<c>QFileDialog::Directory</c>).</summary>
    Directory = 3,
}
