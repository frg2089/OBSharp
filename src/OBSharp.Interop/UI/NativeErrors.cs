using OBSharp.Interop.Abi;

namespace OBSharp.Interop.UI;

/// <summary>
/// 把底层绑定的 <see cref="AbiException"/> 翻译成本程序集对外的
/// <see cref="OBSharpNativeException"/>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这一层：新 ABI 的错误码是 <see cref="AbiResult"/>（4 个负数值），
/// 而对外的公开异常 <see cref="OBSharpNativeException"/> 带的是历史上的
/// <see cref="OBSharpResult"/>（多了 <see cref="OBSharpResult.NotInitialized"/> /
/// <see cref="OBSharpResult.BrowserUnavailable"/> / <see cref="OBSharpResult.Endpoint"/>）。
/// 后者是公开 API 的一部分（调用方会 <c>catch (OBSharpNativeException ex)</c> 之后看
/// <c>ex.Result</c>，<see cref="FilePickerOptions"/> 的文档里也点名了它），所以不能因为底层
/// 换了枚举就把它删掉 —— 只能在这里做一次映射。
/// </para>
/// <para>
/// 另一条硬约束：<see cref="OBSharpNativeException"/> 有一个「只给返回码、消息从原生侧取
/// <c>last_error</c>」的旧构造函数，而新 ABI <b>没有</b> <c>last_error</c> 导出。所以这里
/// 一律走「返回码 + 明确消息」的构造函数，绝不让那个旧重载被隐式走到 —— 它会去 P/Invoke
/// 一个已经不存在的库。
/// </para>
/// </remarks>
internal static class NativeErrors
{
    /// <summary>
    /// 造一个对外异常。<paramref name="message"/> 为空时用返回码兜底。
    /// </summary>
    public static OBSharpNativeException Create(OBSharpResult code, string? message)
        => new(code, message ?? code.ToString());

    /// <summary>
    /// 把绑定的 <see cref="AbiException"/> 翻译成对外异常（保留原始消息）。
    /// </summary>
    public static OBSharpNativeException Translate(AbiException exception)
        => new(Map(exception.Result), exception.Message);

    /// <summary>
    /// 按需翻译：<see cref="AbiException"/> 换成对外类型，其它异常原样返回。
    /// </summary>
    /// <remarks>
    /// 给「catch 到 Exception 之后要把异常塞进 <c>TaskCompletionSource</c>」这种场景用 ——
    /// 那里 catch 的是基类（不能漏掉回调里任何意外异常），但又不该让调用方看见
    /// <see cref="AbiException"/> 这个内部类型。
    /// </remarks>
    public static Exception TranslateIfNeeded(Exception exception)
        => exception is AbiException native ? Translate(native) : exception;

    /// <summary>
    /// 跑一段可能抛 <see cref="AbiException"/> 的代码，把异常翻译成对外类型再抛出。
    /// </summary>
    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (AbiException exception)
        {
            throw Translate(exception);
        }
    }

    /// <summary>
    /// 切到 Qt 主线程执行并翻译异常。<see cref="AbiApi.InvokeUI"/> 的语义是
    /// 「同步执行，回调里抛的异常搬回调用线程重抛」，这里只补一层类型翻译。
    /// </summary>
    public static void RunUI(Action action) => Run(() => AbiApi.InvokeUI(action));

    /// <summary>
    /// <see cref="AbiResult"/> → <see cref="OBSharpResult"/> 的映射。
    /// </summary>
    /// <remarks>
    /// 一一对应，只有一处是「就近取近似」：<see cref="AbiResult.Unavailable"/> 翻成
    /// <see cref="OBSharpResult.BrowserUnavailable"/> —— 新 ABI 把「没初始化」和「宿主里没有
    /// obs-browser」合并成了一个 Unavailable，而对外仍然按旧语义报「浏览器不可用」，
    /// 因为调用方能做的处理是同一件事（提示环境不支持）。
    /// </remarks>
    public static OBSharpResult Map(AbiResult result) => result switch
    {
        AbiResult.Ok => OBSharpResult.Ok,
        AbiResult.InvalidArgument => OBSharpResult.InvalidArgument,
        AbiResult.Unavailable => OBSharpResult.BrowserUnavailable,
        AbiResult.WrongThread => OBSharpResult.WrongThread,
        AbiResult.Internal => OBSharpResult.Internal,
        _ => OBSharpResult.Internal,
    };
}
