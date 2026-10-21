namespace OBSharp.Interop.UI;

/// <summary>
/// 原生调用失败时抛出的异常。
/// </summary>
/// <remarks>
/// <para>
/// 带的是历史上的 <see cref="OBSharpResult"/>（公开 API 的一部分：调用方会
/// <c>catch (OBSharpNativeException ex)</c> 之后看 <see cref="Result"/>）。底层的
/// <see cref="OBSharp.Interop.Native.NativeResult"/> 由 <see cref="NativeErrors"/> 映射过来。
/// </para>
/// <para>
/// <b>不再有「消息从原生侧取」的构造函数。</b>旧版这里会调
/// <c>obsharp_browser_native_last_error()</c> 去读原生侧线程局部的错误串，但新 ABI
/// （<c>OBSharp.Interop.Native</c>）刻意没有这个导出：错误是 C# 侧产生的，消息当然也由
/// C# 侧给出。留着那个重载只会在运行期 P/Invoke 一个不存在的符号 —— 编译期发现不了，
/// 运行到才崩。所以现在一律要求显式传消息。
/// </para>
/// </remarks>
[Serializable]
public partial class OBSharpNativeException(OBSharpResult code, string message) : Exception(message)
{
    /// <summary>
    /// 原生返回码。
    /// </summary>
    public OBSharpResult Result { get; } = code;

    /// <summary>
    /// 按返回码抛异常；消息用返回码的名字兜底（没有更具体的信息时用）。
    /// </summary>
    public OBSharpNativeException(OBSharpResult code) : this(code, code.ToString())
    {
    }

    public static void ThrowIfFailed(int result) => ThrowIfFailed((OBSharpResult)result);

    public static void ThrowIfFailed(OBSharpResult result)
    {
        if (result is OBSharpResult.Ok)
            return;

        throw new OBSharpNativeException(result);
    }
}
