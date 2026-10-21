using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 文件选择对话框：打开文件 / 保存文件 / 选择文件夹。
/// </summary>
/// <remarks>
/// <para>
/// 对应原生头文件里的 <c>obsharp_file_picker_*</c>。原生侧用
/// <c>QFileDialog::open()</c> 显示窗口模态对话框，启动调用会立即返回；用户选完 /
/// 取消后，原生回调在 Qt 主线程上完成返回的 <see cref="Task{TResult}"/>。
/// </para>
/// <para>
/// 启动对话框这一步要切到 Qt 主线程（<see cref="Internal.InvokeUI"/>），但它只负责
/// 把窗口 show 出来，不会等用户操作。用户交互发生在 Qt 的事件循环里，所以调用线程
/// 不会被阻塞。
/// </para>
/// <para>
/// <b>不要</b>在 UI 线程上对返回的 Task 调 <c>.Result</c> / <c>.Wait()</c>：完成回调
/// 需要在同一个 UI 线程上跑，阻塞它会死锁。用 <see langword="await"/>。
/// </para>
/// <para>
/// 取消不是错误：<see cref="FilePickerResult.Canceled"/> 为 <see langword="true"/>、
/// <see cref="FilePickerResult.Paths"/> 为空。真正的失败抛
/// <see cref="OBSharpNativeException"/>。
/// </para>
/// </remarks>
public static unsafe partial class FilePicker
{
    /// <summary>
    /// 打开文件。取消时 <see cref="FilePickerResult.Canceled"/> 为
    /// <see langword="true"/>。
    /// </summary>
    public static Task<FilePickerResult> OpenFileAsync(FilePickerOptions? options = null)
        => ShowAsync(PickerKind.Open, options, false);

    /// <summary>
    /// 打开多个文件。取消时 <see cref="FilePickerResult.Canceled"/> 为
    /// <see langword="true"/>；结果按用户选择顺序放在
    /// <see cref="FilePickerResult.Paths"/> 中。
    /// </summary>
    public static Task<FilePickerResult> OpenFilesAsync(FilePickerOptions? options = null)
        => ShowAsync(PickerKind.Open, options, true);

    /// <summary>
    /// 保存文件。取消时 <see cref="FilePickerResult.Canceled"/> 为
    /// <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// 用户输入的文件名已经存在时，Qt 会自己询问是否覆盖。
    /// </remarks>
    public static Task<FilePickerResult> SaveFileAsync(FilePickerOptions? options = null)
        => ShowAsync(PickerKind.Save, options, false);

    /// <summary>
    /// 选择文件夹。取消时 <see cref="FilePickerResult.Canceled"/> 为
    /// <see langword="true"/>。
    /// </summary>
    public static Task<FilePickerResult> SelectFolderAsync(FilePickerOptions? options = null)
        => ShowAsync(PickerKind.Folder, options, false);

    private static Task<FilePickerResult> ShowAsync(PickerKind kind, FilePickerOptions? options, bool allowMultiple)
    {
        PickerState state = new();
        Internal.InvokeUI(() =>
        {
            nint title = 0;
            nint directory = 0;
            nint fileName = 0;
            nint filter = 0;
            nint defaultSuffix = 0;

            bool started = false;

            try
            {
                title = Marshal.StringToCoTaskMemUTF8(options?.Title);
                directory = Marshal.StringToCoTaskMemUTF8(options?.Directory);
                fileName = Marshal.StringToCoTaskMemUTF8(options?.FileName);
                filter = Marshal.StringToCoTaskMemUTF8(BuildFilter(options?.Filters));
                defaultSuffix = Marshal.StringToCoTaskMemUTF8(options?.DefaultSuffix);

                NativeFilePickerOptions native = new()
                {
                    Size = (uint)sizeof(NativeFilePickerOptions),
                    Title = (byte*)title,
                    Directory = (byte*)directory,
                    FileName = (byte*)fileName,
                    Filter = (byte*)filter,
                    DefaultSuffix = (byte*)defaultSuffix,
                    AllowMultiple = allowMultiple ? 1 : 0,
                    ShowHidden = options?.ShowHiddenFiles == true ? 1 : 0,
                    ParentWidget = options?.ParentWidget ?? 0,
                };

                nint user = GCHandle.ToIntPtr(state.Handle);
                int result = kind switch
                {
                    PickerKind.Open => obsharp_file_picker_open(native, &OnPickerDone, user),
                    PickerKind.Save => obsharp_file_picker_save(native, &OnPickerDone, user),
                    PickerKind.Folder => obsharp_file_picker_select_folder(native, &OnPickerDone, user),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported picker kind"),
                };

                OBSharpNativeException.ThrowIfFailed(result);

                started = true;
            }
            catch (Exception exception)
            {
                if (started)
                {
                    // 对话框已经 show 出来了，句柄由回调释放；回调稍后会把结果写进
                    // 一个已经 faulted 的 Task（TrySetResult 会返回 false）。
                    state.Completion.TrySetException(exception);
                }
                else
                {
                    state.Handle.Free();
                    state.Completion.TrySetException(exception);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(title);
                Marshal.FreeCoTaskMem(directory);
                Marshal.FreeCoTaskMem(fileName);
                Marshal.FreeCoTaskMem(filter);
                Marshal.FreeCoTaskMem(defaultSuffix);
            }
        });

        return state.Completion.Task;
    }

    /// <summary>
    /// 把 <c>"显示名|模式,模式"</c> 形式的数组拼成 Qt 的
    /// <c>"显示名 (模式 模式);;显示名 (模式)"</c> 形式。没有有效项时返回
    /// <see langword="null"/>，交给原生侧显示所有文件。
    /// </summary>
    private static string? BuildFilter(string[]? filters)
    {
        if (filters is null || filters.Length is 0)
            return null;

        List<string> groups = new(filters.Length);

        foreach (string? filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter))
                continue;

            int separator = filter.IndexOf('|');
            string label = separator >= 0 ? filter[..separator].Trim() : string.Empty;
            string patterns = (separator >= 0 ? filter[(separator + 1)..] : filter).Trim();

            string[] masks = patterns.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (masks.Length is 0)
                continue;

            if (label.Length is 0)
                label = "File";

            groups.Add($"{label} ({string.Join(' ', masks)})");
        }

        return groups.Count > 0 ? string.Join(";;", groups) : null;
    }

    private static FilePickerResult ReadResult(nint result)
    {
        if (result is 0)
            return new FilePickerResult { Canceled = true };

        try
        {
            bool canceled = obsharp_file_picker_result_cancelled(result) is not 0;
            int count = obsharp_file_picker_result_count(result);
            string[] paths = new string[count];

            for (int i = 0; i < count; i++)
                paths[i] = obsharp_file_picker_result_path(result, i) ?? string.Empty;

            return new FilePickerResult
            {
                Canceled = canceled || paths.Length is 0,
                Paths = paths,
            };
        }
        finally
        {
            obsharp_file_picker_result_destroy(result);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPickerDone(nint user, nint result)
    {
        GCHandle handle = GCHandle.FromIntPtr(user);
        if (handle.Target is not PickerState state)
            return;

        try
        {
            state.Completion.TrySetResult(ReadResult(result));
        }
        catch (Exception exception)
        {
            state.Completion.TrySetException(exception);
        }
        finally
        {
            handle.Free();
        }
    }

    private enum PickerKind
    {
        Open,
        Save,
        Folder,
    }

    private sealed class PickerState
    {
        public TaskCompletionSource<FilePickerResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GCHandle Handle { get; }

        public PickerState()
        {
            Handle = GCHandle.Alloc(this);
        }
    }

    #region Native Methods
    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_file_picker_open")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_file_picker_open(
        in NativeFilePickerOptions options,
        delegate* unmanaged[Cdecl]<nint, nint, void> done,
        nint user);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_file_picker_save")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_file_picker_save(
        in NativeFilePickerOptions options,
        delegate* unmanaged[Cdecl]<nint, nint, void> done,
        nint user);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_file_picker_select_folder")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_file_picker_select_folder(
        in NativeFilePickerOptions options,
        delegate* unmanaged[Cdecl]<nint, nint, void> done,
        nint user);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_file_picker_result_cancelled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_file_picker_result_cancelled(nint result);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_file_picker_result_count")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int obsharp_file_picker_result_count(nint result);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_file_picker_result_path")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalUsing(typeof(OBSStringMarshaller))]
    private static partial string? obsharp_file_picker_result_path(nint result, int index);

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_file_picker_result_destroy")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void obsharp_file_picker_result_destroy(nint result);
    #endregion

    /// <summary>
    /// <c>obsharp_file_picker_options</c> 的内存布局。字段顺序、大小必须跟
    /// <c>include/OBSharp.Interop.Browser.Native.h</c> 一字不差。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFilePickerOptions
    {
        public uint Size;

        public byte* Title;
        public byte* Directory;
        public byte* FileName;
        public byte* Filter;
        public byte* DefaultSuffix;

        public int AllowMultiple;
        public int ShowHidden;

        public nint ParentWidget;
    }
}
