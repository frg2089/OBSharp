using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using OBSharp.Interop.Native;

namespace OBSharp.Interop.UI;

/// <summary>
/// 文件选择对话框：打开文件 / 保存文件 / 选择文件夹。
/// </summary>
/// <remarks>
/// <para>
/// 对应原生头文件里的 <c>obsharp_file_dialog_*</c>。原生侧用
/// <c>QFileDialog::open()</c> 显示窗口模态对话框，启动调用会立即返回；用户选完 /
/// 取消后，原生回调在 Qt 主线程上完成返回的 <see cref="Task{TResult}"/>。
/// </para>
/// <para>
/// 启动对话框这一步要切到 Qt 主线程（<see cref="NativeBridge.InvokeUI"/>），但它只负责
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
/// <para>
/// 实现说明（跟旧版的差别）：旧版一条 <c>obsharp_file_picker_open(options, ...)</c> 就把
/// 参数全交出去了；新 ABI 把 QFileDialog 拆成「建一个空对话框 + 逐项 set + open」三步，
/// 所以那些参数现在在 <see cref="ShowAsync"/> 里逐条摆。对调用方完全透明。
/// </para>
/// </remarks>
public static unsafe partial class FilePicker
{
    /// <summary>
    /// <c>QFileDialog::AcceptMode</c>，按 Qt 的枚举顺序（顺序不能改）。
    /// </summary>
    private const NativeFileDialogAcceptMode AcceptOpen = NativeFileDialogAcceptMode.Open;
    private const NativeFileDialogAcceptMode AcceptSave = NativeFileDialogAcceptMode.Save;

    /// <summary>
    /// <c>QFileDialog::FileMode</c>，按 Qt 的枚举顺序（顺序不能改）。
    /// </summary>
    private const NativeFileDialogFileMode FileModeExistingFile = NativeFileDialogFileMode.ExistingFile;
    private const NativeFileDialogFileMode FileModeExistingFiles = NativeFileDialogFileMode.ExistingFiles;
    private const NativeFileDialogFileMode FileModeAnyFile = NativeFileDialogFileMode.AnyFile;
    private const NativeFileDialogFileMode FileModeDirectory = NativeFileDialogFileMode.Directory;

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

        try
        {
            // 整段「建对话框 → 摆参数 → open」都在 Qt 主线程上做：QFileDialog 是 Qt Widget，
            // 连构造都必须在主线程。NativeBridge.InvokeUI 是同步的，异常会搬回调用线程重抛。
            NativeBridge.InvokeUI(() =>
            {
                bool started = false;
                nint dialog = 0;

                try
                {
                    // 父窗口：先按 HWND 找、找不到再当 QWidget*（旧 ResolveFilePickerParent 的判断，
                    // 见 ParentResolver）。解析不出来就退回「当前活动窗口」——没有父级的对话框
                    // 在 Windows 上会跑到主窗口后面去，用户会以为程序卡住了。
                    nint parent = ParentResolver.Resolve(options?.ParentWidget ?? 0);
                    if (parent is 0)
                        parent = NativeMethods.obsharp_active_window();

                    dialog = NativeMethods.obsharp_file_dialog_create(parent);
                    if (dialog is 0)
                    {
                        throw NativeErrors.Create(
                            OBSharpResult.Internal,
                            "obsharp_file_dialog_create() returned a null handle: could not create the QFileDialog.");
                    }

                    Configure(dialog, kind, options, allowMultiple);

                    nint user = GCHandle.ToIntPtr(state.Handle);
                    int result = NativeMethods.obsharp_file_dialog_open(dialog, &OnPickerDone, user);
                    NativeException.ThrowIfFailed(
                        result, "obsharp_file_dialog_open() failed: the dialog could not be shown.");

                    // open() 成功之后对话框的所有权转到原生侧，它会 deleteLater 掉，
                    // 这边不能再碰 dialog（包括下面 catch 里的清理）。
                    started = true;
                }
                catch (Exception exception)
                {
                    if (!started && dialog is not 0)
                    {
                        // open() 没成功：对话框还归我们。新 ABI 没有专门的 destroy 导出，
                        // 但 QFileDialog 本身就是 QWidget，用 widget 的 deleteLater 收掉是对的
                        // —— 不这么做就是每失败一次漏一个窗口对象。
                        ClientOwnedDialogCleanup(dialog);
                    }

                    // 回调不会来了（或者回调已经会来但 Task 已经 faulted）：直接结束 Task。
                    // 用 TrySetException 而不是 SetException：started=true 的分支里回调可能
                    // 已经在 Qt 主线程上完成了它，硬 Set 会抛。
                    state.Completion.TrySetException(NativeErrors.TranslateIfNeeded(exception));

                    if (!started)
                        state.Handle.Free();
                }
            });
        }
        catch (Exception exception)
        {
            // 切不到 Qt 主线程（Qt 还没起来 / 已经退出）。不接住的话返回的 Task 永远不会完成，
            // 调用方 await 到天荒地老 —— 那比直接失败难查得多。
            state.Completion.TrySetException(NativeErrors.TranslateIfNeeded(exception));
        }

        return state.Completion.Task;
    }

    /// <summary>
    /// 按对话框类型逐项设置 QFileDialog 的参数。
    /// </summary>
    /// <remarks>
    /// 三种对话框共用一份 <see cref="FilePickerOptions"/>，不适用的字段直接跳过
    /// （旧 FilePicker.cpp 的 switch 也是这么写的）：
    /// <list type="bullet">
    /// <item>选择文件夹时不看 <see cref="FilePickerOptions.FileName"/> / Filters / DefaultSuffix；</item>
    /// <item>打开文件时不看 <see cref="FilePickerOptions.DefaultSuffix"/>。</item>
    /// </list>
    /// </remarks>
    private static void Configure(nint dialog, PickerKind kind, FilePickerOptions? options, bool allowMultiple)
    {
        // 空串一律不设：Qt 对空标题 / 空目录有自己的默认行为（继承上次访问的位置），
        // 显式设成空串反而会把它清掉。
        if (!string.IsNullOrEmpty(options?.Title))
            NativeMethods.obsharp_file_dialog_set_title(dialog, options.Title);

        if (!string.IsNullOrEmpty(options?.Directory))
            NativeMethods.obsharp_file_dialog_set_directory(dialog, options.Directory);

        string? filter = BuildFilter(options?.Filters);
        if (!string.IsNullOrEmpty(filter))
            NativeMethods.obsharp_file_dialog_set_name_filter(dialog, filter);

        // 隐藏文件：原生侧负责把 QDir::Hidden | QDir::System 或到现有的 filter() 上，
        // 必须在 setNameFilter 之后调（filter() 是从 name filters 推出来的）——
        // 顺序跟旧 FilePicker.cpp 保持一致。
        if (options?.ShowHiddenFiles == true)
            NativeMethods.obsharp_file_dialog_set_show_hidden(dialog, true);

        switch (kind)
        {
            case PickerKind.Open:
                NativeMethods.obsharp_file_dialog_set_accept_mode(dialog, AcceptOpen);
                NativeMethods.obsharp_file_dialog_set_file_mode(
                    dialog, allowMultiple ? FileModeExistingFiles : FileModeExistingFile);
                break;

            case PickerKind.Save:
                NativeMethods.obsharp_file_dialog_set_accept_mode(dialog, AcceptSave);
                // 保存对话框必须是 AnyFile：文件还不存在，用 ExistingFile 会让「保存」
                // 按钮永远灰着。
                NativeMethods.obsharp_file_dialog_set_file_mode(dialog, FileModeAnyFile);

                // 后缀只在保存时有意义（打开时用户选的是已有文件，补后缀没有意义）。
                if (!string.IsNullOrEmpty(options?.DefaultSuffix))
                    NativeMethods.obsharp_file_dialog_set_default_suffix(dialog, options.DefaultSuffix);
                break;

            case PickerKind.Folder:
                NativeMethods.obsharp_file_dialog_set_accept_mode(dialog, AcceptOpen);
                NativeMethods.obsharp_file_dialog_set_file_mode(dialog, FileModeDirectory);
                NativeMethods.obsharp_file_dialog_set_show_dirs_only(dialog, true);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported picker kind");
        }

        // 选择文件夹时不预填文件名：Qt 会把 filename 当成「要选中的子项」，
        // 在纯目录对话框里没有意义（旧实现同样跳过了这一条）。
        if (kind is not PickerKind.Folder && !string.IsNullOrEmpty(options?.FileName))
            NativeMethods.obsharp_file_dialog_select_file(dialog, options.FileName);
    }

    /// <summary>
    /// 收掉一个「还没交给 open()」的对话框。
    /// </summary>
    /// <remarks>
    /// 之所以能借用 widget 的 <c>deleteLater</c>：<c>QFileDialog</c> 是 <c>QWidget</c> 的子类，
    /// 而 <c>obsharp_widget_delete_later()</c> 就是 <c>QWidget::deleteLater()</c> 的一行转发。
    /// 用推迟删除而不是直接 delete：这个调用点可能正在 Qt 的事件派发栈上（<c>open()</c> 失败时
    /// 尤其可能在对话框自己的事件处理里），当场 delete 自己就是崩溃。
    /// </remarks>
    private static void ClientOwnedDialogCleanup(nint dialog)
    {
        try
        {
            NativeMethods.obsharp_widget_delete_later(dialog);
        }
        catch (NativeException exception)
        {
            // Teardown must not mask the real failure, but it must not vanish either:
            // a leaked QFileDialog shows up as a stuck dialog later.
            NativeLogSink.Write(
                NativeLogLevel.Warning,
                "Failed to release the file dialog after a failure: " + exception.Message);
        }
    }

    /// <summary>
    /// 把 <c>"显示名|模式,模式"</c> 形式的数组拼成 Qt 的
    /// <c>"显示名 (模式 模式);;显示名 (模式)"</c> 形式。没有有效项时返回
    /// <see langword="null"/>，交给原生侧显示所有文件。
    /// </summary>
    /// <remarks>
    /// 这个转换以前在 C++ 里（<c>FilePicker.cpp</c> 的 <c>BuildFilter</c> 对应部分在托管侧），
    /// 现在完整留在 C#：它纯粹是字符串处理，没有任何 Qt 类型参与。
    /// </remarks>
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

    /// <summary>
    /// 读一次选择结果。
    /// </summary>
    /// <param name="dialog">done 回调里拿到的对话框句柄（回调期间有效）。</param>
    /// <param name="cancelled">原生侧报的「是否取消」。</param>
    /// <remarks>
    /// 用户按了「打开 / 保存」但一个文件都没选出来时也按取消上报 —— 旧实现就是这么处理的
    /// （<c>paths.empty() → cancelled = true</c>），理由是「没选到东西」对调用方来说和取消
    /// 没有任何区别，报成功反而逼着每个调用方都再判一次空数组。
    /// </remarks>
    private static FilePickerResult ReadResult(nint dialog, bool cancelled)
    {
        if (cancelled || dialog is 0)
            return new FilePickerResult { Canceled = true };

        int count = NativeMethods.obsharp_file_dialog_selected_count(dialog);
        if (count <= 0)
            return new FilePickerResult { Canceled = true };

        string[] paths = new string[count];
        for (int i = 0; i < count; i++)
            paths[i] = NativeMethods.obsharp_file_dialog_selected_path(dialog, i) ?? string.Empty;

        return new FilePickerResult
        {
            Canceled = paths.Length is 0,
            Paths = paths,
        };
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPickerDone(nint user, nint dialog, byte cancelled)
    {
        GCHandle handle = GCHandle.FromIntPtr(user);
        if (handle.Target is not PickerState state)
            return;

        try
        {
            state.Completion.TrySetResult(ReadResult(dialog, cancelled is not 0));
        }
        catch (Exception exception)
        {
            // 回调里绝不能抛：异常会直接穿过原生栈（未定义行为，通常是进程崩）。
            state.Completion.TrySetException(NativeErrors.TranslateIfNeeded(exception));
        }
        finally
        {
            // 原生侧保证每次 open 恰好回调一次，所以这里释放句柄是安全的；
            // 释放之后即使再有回调（不应该有）也查不到 state，直接返回。
            handle.Free();
        }
    }

    private enum PickerKind
    {
        Open,
        Save,
        Folder,
    }

    /// <summary>
    /// 一次对话框的跨线程状态：UI 线程启动、Qt 主线程回调完成。
    /// </summary>
    /// <remarks>
    /// <see cref="Completion"/> 用 <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>：
    /// 完成回调跑在 Qt 主线程上，如果它同步执行 <c>await</c> 之后的续体，调用方的代码就会
    /// 在 Qt 的事件派发栈里跑起来 —— 那里拿到的异常没人接，而且容易在派发过程中做重入操作。
    /// </remarks>
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
}
