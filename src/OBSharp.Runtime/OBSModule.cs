using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OBSharp.Interop;
using OBSharp.Runtime;

using Host = Microsoft.Extensions.Hosting.Host;

namespace OBSharp;

public static partial class OBSModule
{
    private static IHost? s_app;

    [UnmanagedCallersOnly]
    public static void SetPointer(nint lpModule)
    {
        ObsModule module = new(lpModule, false);
        var binaryPath = module.GetModuleBinaryPath();
        binaryPath = Path.GetDirectoryName(binaryPath);
        binaryPath = Path.GetDirectoryName(binaryPath);
        var dataPath = module.GetModuleDataPath();
        Debug.Assert(!string.IsNullOrEmpty(binaryPath));
        Debug.Assert(!string.IsNullOrEmpty(dataPath));

        // 原生库（OBSharp.Interop.Native）与托管绑定同属 OBSharp.Interop 程序集，所以
        // 解析器挂在 OBSharp.Interop 上即可：所有 P/Invoke 都从那个程序集发起。
        //
        // 这里刻意不用 NativeApi 取程序集 —— 它是 internal 的（原生导入不对外暴露），
        // 宿主看不到它。拿一个公开类型即可，两者在同一个程序集里。
        NativeLibrary.SetDllImportResolver(typeof(Interop.UI.WebView).Assembly, NativeLibraryResolver);

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

        // 这两个路径是排查「插件列表读不到 / 插件 dll 找不到」的第一条线索：
        // 它们决定 $bin / $binAny / $data 三个前缀解析到哪里。
        var startupLogger = s_app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OBSharp.OBSModule");
        startupLogger.LogDebug("Module binary path: {BinaryPath}", binaryPath);
        startupLogger.LogDebug("Module data path:   {DataPath}", dataPath);
        startupLogger.LogDebug("Managed host path:  {ManagedPath}", AppContext.BaseDirectory);
        startupLogger.LogDebug(".NET runtime:       {RuntimeVersion}", Environment.Version);
    }

    [UnmanagedCallersOnly]
    public static int Load()
    {
        if (s_app is null)
            return 0;

        var logger = s_app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OBSharp.OBSModule");
        logger.LogDebug("OBSModule.Load: starting the host");

        s_app.Start();
        logger.LogDebug("OBSModule.Load: host started; beginning plugin load");

        var loaded = s_app.Services.GetRequiredService<OBSharpHost>().Load();
        logger.LogDebug("OBSModule.Load: finished (result={Result})", loaded);
        return loaded ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    public static void PostLoad()
    {
        if (s_app is null)
            return;

        s_app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OBSharp.OBSModule")
            .LogDebug("OBSModule.PostLoad: notifying plugins");

        s_app.Services.GetRequiredService<OBSharpHost>().PostLoad();
    }

    [UnmanagedCallersOnly]
    public static void Unload()
    {
        if (s_app is null)
            return;

        var logger = s_app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OBSharp.OBSModule");
        logger.LogDebug("OBSModule.Unload: stopping plugins");

        s_app.Services.GetRequiredService<OBSharpHost>().Unload();

        logger.LogDebug("OBSModule.Unload: stopping the host");
        s_app.StopAsync().Wait();
        logger.LogDebug("OBSModule.Unload: done");
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
