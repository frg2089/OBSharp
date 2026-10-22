using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ObsharpLogLevel = global::OBSharp.Interop.OBSharp.ObsharpLogLevel;

namespace OBSharp.Interop.Abi;

/// <summary>
/// 生成绑定之上的薄便利层：主线程封送、异常翻译、原生日志回调、借用句柄包装。
/// 只做「每次调用都必须做对」的事，业务判断一律不放这里。
/// </summary>
internal static unsafe class AbiApi
{
    /// <summary>接收原生库的日志行；由 UI 层的 NativeLogSink 设置。</summary>
    internal static Action<AbiLogLevel, string>? LogSink { get; set; }

    /// <summary>原生库版本号。产物换了没生效时先查它——部署目录里可能有多份 DLL。</summary>
    internal static string Version => Obsharp.GetVersion() ?? string.Empty;

    /// <summary>当前线程是否是 Qt 主线程。优先用 InvokeUI，别自己判。</summary>
    internal static bool IsUIThread => Thread.IsUiThread();

    /// <summary>
    /// 同步切到 Qt 主线程执行 <paramref name="action"/>；已在主线程时直通。
    /// 异常不允许跨原生边界，thunk 里存下后在这里重抛（保留原栈）。
    /// </summary>
    /// <exception cref="AbiException">到不了 Qt 主线程。</exception>
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
            bool ok = Thread.UiInvoke(s_uiInvoke, GCHandle.ToIntPtr(handle));
            AbiException.ThrowIfFailed(ok ? 0 : -1,
                "obsharp_ui_invoke failed: the Qt main thread could not be reached.");
        }
        finally
        {
            handle.Free();
        }

        // 阻塞投递，到这里 action 必然跑完了
        if (invocation.Error is not null)
            ExceptionDispatchInfo.Capture(invocation.Error).Throw();
    }

    /// <summary>查已加载的模块；未加载返回 0。任何线程可用，绝不顺手加载。</summary>
    internal static nint FindModule(string name) => Host.FindModule(name);

    /// <summary>解析模块内符号；module 为 0 表示进程全局。结果可转 delegate* unmanaged[Cdecl]。</summary>
    internal static nint FindSymbol(nint module, string name) => Host.FindSymbol(module, name);

    /// <summary>
    /// 安装原生日志回调，进程内至多一次。刻意不用 ModuleInitializer：
    /// NativeAOT 加载器只碰根命名空间的 libobs 绑定，模块初始化会在任何类型触碰时
    /// 去 P/Invoke 一个不存在的库。
    /// </summary>
    internal static void InstallLogSink()
    {
        if (Interlocked.Exchange(ref s_logSinkInstalled, 1) is not 0)
            return;

        Logger.SetLogger(s_logFn);
    }

    /// <summary>0 = 未安装，1 = 已安装。</summary>
    private static int s_logSinkInstalled;

    // 委托实例必须静态持有：原生侧长期持有这些委托，不能被 GC 回收。
    private static readonly ObsharpLogFn s_logFn = OnNativeLog;
    private static readonly ObsharpUiInvokeFn s_uiInvoke = NativeInvokeUI;

    /// <summary>
    /// 原生日志入口。跑在产出行的线程上（含 CEF IO 线程），任何异常不得越过原生边界。
    /// </summary>
    private static void OnNativeLog(ObsharpLogLevel level, string? message)
    {
        if (message is null)
            return;

        Action<AbiLogLevel, string>? sink = LogSink;
        if (sink is null)
        {
            // UI 层还没挂 logger（或宿主主动退订）时的兜底
            System.Diagnostics.Debug.WriteLine($"[OBSharp.Interop.Abi] {level}: {message}");
            return;
        }

        try
        {
            sink(MapLevel(level), message);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Native log sink failed: {exception}");
        }
    }

    /// <summary>原生日志级别 → libobs 的 blog() 数值级别。</summary>
    private static AbiLogLevel MapLevel(ObsharpLogLevel level) => level switch
    {
        ObsharpLogLevel.OBSHARP_LOG_WARNING => AbiLogLevel.Warning,
        ObsharpLogLevel.OBSHARP_LOG_ERROR or ObsharpLogLevel.OBSHARP_LOG_CRITICAL => AbiLogLevel.Error,
        ObsharpLogLevel.OBSHARP_LOG_TRACE or ObsharpLogLevel.OBSHARP_LOG_DEBUG => AbiLogLevel.Debug,
        _ => AbiLogLevel.Info,
    };

    /// <summary>切主线程 thunk 的状态包。</summary>
    private sealed class UiInvocation
    {
        /// <summary>要跑的活。</summary>
        public required Action Action { get; init; }

        /// <summary>action 抛出的异常（如果有）。</summary>
        public Exception? Error { get; set; }
    }

    /// <summary>原生侧在 Qt 主线程上调用的 thunk。</summary>
    private static void NativeInvokeUI(nint handle)
    {
        if (GCHandle.FromIntPtr(handle).Target is not UiInvocation invocation)
            return;

        try
        {
            invocation.Action?.Invoke();
        }
        catch (Exception exception)
        {
            invocation.Error = exception;
        }
    }

    // ---- 借用包装助手 ------------------------------------------------------------
    //
    // UI 层自管生命周期（nint 字段 + 手动销毁）；这里的包装一律 ownsHandle: false，
    // 只把调用派发到生成的薄封装方法上，绝不触发 ReleaseHandle。

    /// <summary>裸指针 → 借用 <see cref="QWidget"/>。</summary>
    internal static QWidget Widget(nint ptr) => new(ptr, ownsHandle: false);

    /// <summary>裸指针 → 借用 <see cref="QCefWidget"/>。</summary>
    internal static QCefWidget CefWidget(nint ptr) => new(ptr, ownsHandle: false);

    /// <summary>裸指针 → 借用 <see cref="QFileDialog"/>。</summary>
    internal static QFileDialog Dialog(nint ptr) => new(ptr, ownsHandle: false);

    /// <summary>裸指针 → 借用 <see cref="QCef"/>。</summary>
    internal static QCef Cef(nint ptr) => new(ptr, ownsHandle: false);

    /// <summary>裸指针 → 借用 <see cref="CefResponse"/>。</summary>
    internal static CefResponse Response(nint ptr) => new(ptr, ownsHandle: false);

    /// <summary>裸指针 → 借用 <see cref="ResourceHandler"/>。</summary>
    internal static ResourceHandler Handler(nint ptr) => new(ptr, ownsHandle: false);

    /// <summary>裸指针 → 借用 <see cref="QObject"/>。</summary>
    internal static QObject QObject(nint ptr) => new(ptr, ownsHandle: false);

    /// <summary>
    /// 立即销毁 widget（obsharp_widget_destroy，内部是 deleteLater）。顶替旧 ABI 的
    /// delete_later——销毁统一走这里或句柄释放。
    /// </summary>
    internal static void DestroyWidget(nint ptr) => Widget(ptr).Destroy();

    /// <summary>
    /// 取走裸指针并解除托管释放责任：调用方从此自管生命周期。
    /// 用于「生成的工厂返回拥有句柄、UI 层按裸指针自管」的交接。
    /// </summary>
    internal static nint Detach(SafeHandle handle)
    {
        nint ptr = handle.DangerousGetHandle();
        handle.SetHandleAsInvalid();
        return ptr;
    }
}
