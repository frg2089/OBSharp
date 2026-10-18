namespace OBSharp.Interop.Browser;

/// <summary>
/// 原生库返回了失败码时抛出。消息里带上原生侧的
/// <see cref="OBSharpNative.LastError"/>。
/// </summary>
public sealed class OBSharpNativeException : Exception
{
    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="result">原生返回码。</param>
    /// <param name="message">原生 <c>obsharp_native_last_error()</c> 的内容。</param>
    public OBSharpNativeException(OBSharpResult result, string message)
        : base($"OBSharp.Interop.Browser.Native 调用失败（{result}）：{message}")
    {
        Result = result;
    }

    /// <summary>
    /// 原生返回码。
    /// </summary>
    public OBSharpResult Result { get; }

    internal static void ThrowIfFailed(OBSharpResult result)
    {
        if (result != OBSharpResult.Ok)
            throw new OBSharpNativeException(result, OBSharpNative.LastError);
    }
}
