namespace OBSharp.Interop.Abi;

/// <summary>
/// 原生调用返回了非 <see cref="AbiResult.Ok"/> 的结果码。
/// ABI 没有「取最后错误消息」的导出，消息一律来自调用方——在 catch 现场把失败的操作
/// 描述清楚（"obsharp_scheme_register(https, foo.obsharp) failed" 远好于 "Unavailable"）。
/// 不会逃出本程序集：公开 API 边界上转成 <see cref="OBSharp.Interop.UI.OBSharpNativeException"/>。
/// </summary>
[Serializable]
internal sealed class AbiException(AbiResult code, string? message) : Exception(message)
{
    /// <summary>原生结果码。</summary>
    public AbiResult Result { get; } = code;

    /// <summary><paramref name="result"/> 不是 <see cref="AbiResult.Ok"/> 时抛出。</summary>
    public static void ThrowIfFailed(int result, string? message = null)
        => ThrowIfFailed((AbiResult)result, message);

    /// <summary><paramref name="result"/> 不是 <see cref="AbiResult.Ok"/> 时抛出。</summary>
    public static void ThrowIfFailed(AbiResult result, string? message = null)
    {
        if (result is AbiResult.Ok)
            return;

        throw new AbiException(result, message ?? result.ToString());
    }
}
