using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OBSharp.Hosts;

using ObsInterop;

namespace OBSharp;

public static class OBSModule
{
    private static unsafe obs_module* s_obs_module_pointer;
    private static OBSModuleHost s_host = default!;
    private static ILogger s_logger = default!;
    public const string Name = "OB#";
    public const string Description = "通过 CoreCLR 运行插件";

    [UnmanagedCallersOnly]
    public static unsafe void SetPointer(obs_module* module)
    {
        s_obs_module_pointer = module;
        string? path = Marshal.PtrToStringUTF8((nint)Obs.obs_get_module_data_path(module));
        Debug.Assert(path is not null);
        path = Path.GetFullPath(path);

        OBSModuleHostBuilder builder = new(path);

        s_host = builder.Build();

        s_logger ??= s_host.Service.GetRequiredService<ILoggerFactory>().CreateLogger("OBSModule");
        s_logger.LogDebug("SetPointer");
        s_logger.LogInformation("Data Path: {}", path);
        s_logger.LogInformation("Initialized!");
    }

    [UnmanagedCallersOnly]
    public static int Load()
    {
        s_logger.LogDebug("Load");
        return 1;
    }

    [UnmanagedCallersOnly]
    public static unsafe byte* GetName()
    {
        s_logger.LogDebug("GetName");
        int size = Encoding.UTF8.GetByteCount(Name);
        void* lpsz = NativeMemory.Alloc((nuint)size, sizeof(byte));
        Encoding.UTF8.GetBytes(Name, new Span<byte>(lpsz, size));
        return (byte*)lpsz;
    }

    [UnmanagedCallersOnly]
    public static unsafe byte* GetDescription()
    {
        s_logger.LogDebug("GetDescription");
        int size = Encoding.UTF8.GetByteCount(Description);
        void* lpsz = NativeMemory.Alloc((nuint)size, sizeof(byte));
        Encoding.UTF8.GetBytes(Description, new Span<byte>(lpsz, size));
        return (byte*)lpsz;
    }

    [UnmanagedCallersOnly]
    public static void SetLocale(nint locale)
    {
        string? str = Marshal.PtrToStringUTF8(locale);
        s_logger.LogDebug("SetLocale: {}", str);
    }

    [UnmanagedCallersOnly]
    public static void PostLoad()
    {
        s_logger.LogDebug("PostLoad");
    }

    [UnmanagedCallersOnly]
    public static void Unload()
    {
        s_logger.LogDebug("Unload");
    }

    [UnmanagedCallersOnly]
    public static void FreeLocale()
    {
        s_logger.LogDebug("FreeLocale");
    }
}
