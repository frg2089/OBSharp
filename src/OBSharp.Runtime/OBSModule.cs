using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using OBSharp.Runtime;

using OBSharp.Interop;

namespace OBSharp;

public static partial class OBSModule
{
    private static OBSharpHost s_host = default!;
    public const string Name = "OB#";
    public const string Description = "通过 CoreCLR 运行插件";

    [UnmanagedCallersOnly]
    public static unsafe void SetPointer(ObsModule* module)
    {
        string? path = module->GetModuleDataPath();
        Debug.Assert(path is not null);
        path = Path.GetFullPath(path);

        OBSharpHostBuilder builder = new(path);

        s_host = builder.Build();

        s_host.SetPointer(module);
    }

    [UnmanagedCallersOnly]
    public static int Load() => s_host.Load() ? 1 : 0;

    [UnmanagedCallersOnly]
    public static void PostLoad() => s_host.PostLoad();

    [UnmanagedCallersOnly]
    public static void Unload() => s_host.Unload();

    [UnmanagedCallersOnly]
    public static unsafe byte* GetName()
    {
        int size = Encoding.UTF8.GetByteCount(Name);
        void* lpsz = NativeMemory.Alloc((nuint)size, sizeof(byte));
        Encoding.UTF8.GetBytes(Name, new Span<byte>(lpsz, size));
        return (byte*)lpsz;
    }

    [UnmanagedCallersOnly]
    public static unsafe byte* GetDescription()
    {
        int size = Encoding.UTF8.GetByteCount(Description);
        void* lpsz = NativeMemory.Alloc((nuint)size, sizeof(byte));
        Encoding.UTF8.GetBytes(Description, new Span<byte>(lpsz, size));
        return (byte*)lpsz;
    }

    [UnmanagedCallersOnly]
    public static void SetLocale(nint locale) => s_host.SetLocale(Marshal.PtrToStringUTF8(locale));

    [UnmanagedCallersOnly]
    public static void FreeLocale() => s_host.FreeLocale();
}
