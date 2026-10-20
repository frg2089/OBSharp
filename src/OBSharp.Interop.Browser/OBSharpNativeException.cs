using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OBSharp.Interop.Browser;

[Serializable]
public partial class OBSharpNativeException(OBSharpResult code, string message) : Exception(message)
{
    /// <summary>
    /// 原生返回码。
    /// </summary>
    public OBSharpResult Result { get; } = code;

    public OBSharpNativeException(OBSharpResult code) : this(code, obsharp_browser_native_last_error())
    { }

    public static void ThrowIfFailed(int result) => ThrowIfFailed((OBSharpResult)result);

    public static void ThrowIfFailed(OBSharpResult result)
    {
        if (result is OBSharpResult.Ok)
            return;

        throw new OBSharpNativeException(result);
    }

    [LibraryImport("OBSharp.Interop.Browser.Native", EntryPoint = "obsharp_browser_native_last_error")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalUsing(typeof(OBSStringMarshaller))]
    private static partial string obsharp_browser_native_last_error();
}
