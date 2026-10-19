using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace OBSharp.Interop;

/// <summary>
/// libobs 的 <c>char*</c> 返回值专用的字符串 marshaller：只复制，不释放。
/// </summary>
/// <remarks>
/// <para>
/// libobs 返回的字符串（<c>obs_get_module_binary_path</c>、<c>obs_get_module_data_path</c>、
/// <c>obs_source_get_name</c> 之类）都由 libobs 自己持有和回收（内部是 <c>bstrdup</c> 出来的），
/// 调用方只能读，不能释放。
/// </para>
/// <para>
/// 而 <see cref="System.Runtime.InteropServices.LibraryImportAttribute"/> 默认的 <c>string</c> marshaller
/// 认为“返回值那块缓冲区是被调用方用 <c>CoTaskMemAlloc</c> 分配的”：复制成托管字符串之后，它还会在
/// <c>finally</c> 里调用 <c>Free</c>（也就是 <c>Marshal.FreeCoTaskMem</c>）把它释放掉。释放一个不属于
/// 自己的指针就是堆损坏——在 OBS 里的表现是 <c>0xC0000374</c>（STATUS_HEAP_CORRUPTION，ntdll 里
/// fast-fail，不弹崩溃框），插件一加载进程就直接没了。
/// </para>
/// <para>
/// 所以这里 <see cref="ConvertToManaged"/> 只用 <see cref="Encoding.UTF8"/> 复制出一份托管字符串，
/// <see cref="Free"/> 把 <c>byte*</c> 原样丢掉：内存是 libobs 的，谁都不动。
/// </para>
/// <para>
/// 用法——只加在返回值上，参数照旧用 <c>StringMarshalling</c>（参数方向的缓冲区是 marshaller 自己分配的，
/// 默认行为本来就是对的）：
/// <code>
/// [LibraryImport("obs", EntryPoint = "obs_get_module_binary_path")]
/// [return: MarshalUsing(typeof(OBSStringMarshaller))]
/// private static partial string? obs_get_module_binary_path(ObsModule* module);
/// </code>
/// </para>
/// </remarks>
[CustomMarshaller(typeof(string), MarshalMode.Default, typeof(OBSStringMarshaller))]
public static unsafe class OBSStringMarshaller
{
    /// <summary>
    /// 把 libobs 的 UTF-8 字符串复制成托管字符串。<paramref name="unmanaged"/> 归 libobs，这里只读。
    /// </summary>
    public static string? ConvertToManaged(byte* unmanaged)
        => Marshal.PtrToStringUTF8((nint)unmanaged);

}
