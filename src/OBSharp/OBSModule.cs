using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ObsInterop;

namespace OBSharp;

public static class OBSModule
{
    private static unsafe obs_module* s_obs_module_pointer;
    private static readonly IServiceProvider ServiceProvider;
    private static readonly ILogger Logger;
    public const string Name = "OB#";
    public const string Description = "通过 CoreCLR 运行插件";

    static OBSModule()
    {
        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.AddProvider(new OBSLoggerProvider());
        });
        ServiceProvider = services.BuildServiceProvider();
        Logger ??= ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("OBSModule");
        Logger.LogInformation("Initialized!");
    }

    [UnmanagedCallersOnly]
    public static unsafe void SetPointer(obs_module* module)
    {
        s_obs_module_pointer = module;
        Logger.LogDebug("SetPointer");
    }

    [UnmanagedCallersOnly]
    public static int Load()
    {
        Logger.LogDebug("Load");
        return 1;
    }

    [UnmanagedCallersOnly]
    public static unsafe byte* GetName()
    {
        Logger.LogDebug("GetName");
        int size = Encoding.UTF8.GetByteCount(Name);
        void* lpsz = NativeMemory.Alloc((nuint)size, sizeof(byte));
        Encoding.UTF8.GetBytes(Name, new Span<byte>(lpsz, size));
        return (byte*)lpsz;
    }

    [UnmanagedCallersOnly]
    public static unsafe byte* GetDescription()
    {
        Logger.LogDebug("GetDescription");
        int size = Encoding.UTF8.GetByteCount(Description);
        void* lpsz = NativeMemory.Alloc((nuint)size, sizeof(byte));
        Encoding.UTF8.GetBytes(Description, new Span<byte>(lpsz, size));
        return (byte*)lpsz;
    }

    [UnmanagedCallersOnly]
    public static void SetLocale(nint locale)
    {
        string? str = Marshal.PtrToStringUTF8(locale);
        Logger.LogDebug("SetLocale: {}", str);
    }

    [UnmanagedCallersOnly]
    public static void PostLoad()
    {
        Logger.LogDebug("PostLoad");
    }

    [UnmanagedCallersOnly]
    public static void Unload()
    {
        Logger.LogDebug("Unload");
    }

    [UnmanagedCallersOnly]
    public static void FreeLocale()
    {
        Logger.LogDebug("FreeLocale");
    }
}
