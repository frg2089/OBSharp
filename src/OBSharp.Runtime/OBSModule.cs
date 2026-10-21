using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OBSharp.Interop;
using OBSharp.Runtime;

namespace OBSharp;

public static partial class OBSModule
{
    private static IHost? s_app;

    [UnmanagedCallersOnly]
    public static unsafe void SetPointer(ObsModule* lpModule)
    {
        ref var module = ref Unsafe.AsRef<ObsModule>(lpModule);
        var binaryPath = module.GetModuleBinaryPath();
        var dataPath = module.GetModuleDataPath();
        Debug.Assert(!string.IsNullOrEmpty(binaryPath));
        Debug.Assert(!string.IsNullOrEmpty(dataPath));

        NativeLibrary.SetDllImportResolver(typeof(Interop.Browser.WebView).Assembly, NativeLibraryResolver);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings()
        {
            ApplicationName = "OB#",
            ContentRootPath = dataPath,
        });
        builder.Logging.AddProvider(new ObsLoggerProvider("OBSharp"));

        builder.Services
            .AddOptions<OBSharpHostOptions>()
            .BindConfiguration("OBSharp")
            .Configure(options =>
            {
                options.BinaryPath ??= binaryPath;
                options.DataPath ??= dataPath;
            });

        builder.Services.AddSingleton<OBSharpPluginRegister>();
        builder.Services.AddSingleton<OBSharpPluginManager>();
        builder.Services.AddSingleton<OBSharpHost>();

        s_app = builder.Build();
    }

    [UnmanagedCallersOnly]
    public static int Load()
    {
        if (s_app is null)
            return 0;

        s_app.Start();

        return s_app.Services.GetRequiredService<OBSharpHost>().Load() ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    public static void PostLoad()
    {
        if (s_app is null)
            return;

        s_app.Services.GetRequiredService<OBSharpHost>().PostLoad();
    }

    [UnmanagedCallersOnly]
    public static void Unload()
    {
        if (s_app is null)
            return;

        s_app.Services.GetRequiredService<OBSharpHost>().Unload();

        s_app.StopAsync().Wait();
    }

    [UnmanagedCallersOnly]
    public static nint GetName()
    {
        if (s_app is null)
            return 0;

        var name = s_app.Services.GetRequiredService<OBSharpHost>().Name;
        return Marshal.StringToCoTaskMemUTF8(name);
    }

    [UnmanagedCallersOnly]
    public static nint GetDescription()
    {
        if (s_app is null)
            return 0;

        var description = s_app.Services.GetRequiredService<OBSharpHost>().Description;
        return Marshal.StringToCoTaskMemUTF8(description);
    }

    [UnmanagedCallersOnly]
    public static void SetLocale(nint locale)
    {
        if (s_app is null)
            return;

        s_app.Services.GetRequiredService<OBSharpHost>().SetLocale(Marshal.PtrToStringUTF8(locale));
    }

    [UnmanagedCallersOnly]
    public static void FreeLocale()
    {
        if (s_app is null)
            return;

        s_app.Services.GetRequiredService<OBSharpHost>().FreeLocale();
    }

    private static nint NativeLibraryResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string prefix = string.Empty;
        string surfix = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            surfix = ".dll";
        }
        else if (OperatingSystem.IsLinux())
        {
            prefix = "lib";
            surfix = ".so";
        }
        else if (OperatingSystem.IsMacOS())
        {
            prefix = "lib";
            surfix = ".dylib";
        }

        var libpath = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", $"{prefix}{libraryName}{surfix}");
        if (!File.Exists(libpath))
            libpath = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", $"{libraryName}{surfix}");
        if (!File.Exists(libpath))
            libpath = Path.Combine(AppContext.BaseDirectory, $"{prefix}{libraryName}{surfix}");
        if (!File.Exists(libpath))
            libpath = Path.Combine(AppContext.BaseDirectory, $"{libraryName}{surfix}");

        if (File.Exists(libpath))
            return NativeLibrary.Load(libpath);

        return 0;

    }
}
