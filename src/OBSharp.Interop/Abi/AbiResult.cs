namespace OBSharp.Interop.Abi;

/// <summary>
/// 原生库的结果码，数值必须与 <c>obsharp_result</c> 一致。
/// 非 <see cref="Ok"/> 由绑定层转成 <see cref="AbiException"/>。
/// </summary>
internal enum AbiResult
{
    /// <summary>成功。</summary>
    Ok = 0,

    /// <summary>参数非法（空指针、空字符串……）。</summary>
    InvalidArgument = -1,

    /// <summary>缺宿主对象（obs-browser 模块、CEF、Qt 应用……）。</summary>
    Unavailable = -2,

    /// <summary>调用线程不对。</summary>
    WrongThread = -3,

    /// <summary>其他内部失败（CEF 任务投递失败、注册失败……）。</summary>
    Internal = -4,
}
