using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OBSharp.Interop;
using OBSharp.Interop.Abi;
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
        NativeLibrary.SetDllImportResolver(typeof(AbiApi).Assembly, NativeLibraryResolver);

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
        LogModuleBinaryPath(startupLogger, binaryPath);
        LogModuleDataPath(startupLogger, dataPath);
        LogManagedHostPath(startupLogger, AppContext.BaseDirectory);
        LogRuntimeVersion(startupLogger, Environment.Version);
    }

    [UnmanagedCallersOnly]
    public static int Load()
    {
        if (s_app is null)
            return 0;

        var logger = s_app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OBSharp.OBSModule");
        LogLoadStarting(logger);

        s_app.Start();
        LogHostStarted(logger);

        var loaded = s_app.Services.GetRequiredService<OBSharpHost>().Load();
        LogLoadFinished(logger, loaded);
        return loaded ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    public static void PostLoad()
    {
        if (s_app is null)
            return;

        LogPostLoadNotifying(s_app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OBSharp.OBSModule"));

        s_app.Services.GetRequiredService<OBSharpHost>().PostLoad();
    }

    [UnmanagedCallersOnly]
    public static void Unload()
    {
        if (s_app is null)
            return;

        var logger = s_app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OBSharp.OBSModule");
        LogUnloadStoppingPlugins(logger);

        s_app.Services.GetRequiredService<OBSharpHost>().Unload();

        LogUnloadStoppingHost(logger);
        s_app.StopAsync().Wait();
        LogUnloadDone(logger);
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

        // 被 hostfxr 以「组件」方式加载时，AppContext.BaseDirectory 不是托管程序集所在的 any/ 目录，
        // 所以以程序集自身位置为准；RuntimeInformation.RuntimeIdentifier 是运行时的构建 RID，
        // 也不一定等于 runtimes/ 下的目录名，这里按系统 + 架构显式拼。
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            var other => other.ToString().ToLowerInvariant(),
        };
        var rid = $"{os}-{arch}";

        var libraries = new[]
        {
            Path.GetDirectoryName(typeof(OBSModule).Assembly.Location),
            Path.GetDirectoryName(assembly.Location),
            AppContext.BaseDirectory,
        }
        .Distinct()
        .OfType<string>()
        .SelectMany(i => new[] { i, Path.Combine(i, "runtimes", rid, "native") }.Distinct())
        .SelectMany(i => new[] { Path.Combine(i, $"{prefix}{libraryName}{surfix}"), Path.Combine(i, $"{libraryName}{surfix}") }.Distinct());

        foreach (var library in libraries)
        {
            if (File.Exists(library) && NativeLibrary.TryLoad(library, out var handle))
                return handle;
        }

        return 0;
    }

    [LoggerMessage(LogLevel.Debug, "Module binary path: {BinaryPath}")]
    private static partial void LogModuleBinaryPath(ILogger logger, string? binaryPath);

    [LoggerMessage(LogLevel.Debug, "Module data path:   {DataPath}")]
    private static partial void LogModuleDataPath(ILogger logger, string? dataPath);

    [LoggerMessage(LogLevel.Debug, "Managed host path:  {ManagedPath}")]
    private static partial void LogManagedHostPath(ILogger logger, string? managedPath);

    [LoggerMessage(LogLevel.Debug, ".NET runtime:       {RuntimeVersion}")]
    private static partial void LogRuntimeVersion(ILogger logger, Version runtimeVersion);

    [LoggerMessage(LogLevel.Debug, "OBSModule.Load: starting the host")]
    private static partial void LogLoadStarting(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "OBSModule.Load: host started; beginning plugin load")]
    private static partial void LogHostStarted(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "OBSModule.Load: finished (result={Result})")]
    private static partial void LogLoadFinished(ILogger logger, bool result);

    [LoggerMessage(LogLevel.Debug, "OBSModule.PostLoad: notifying plugins")]
    private static partial void LogPostLoadNotifying(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "OBSModule.Unload: stopping plugins")]
    private static partial void LogUnloadStoppingPlugins(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "OBSModule.Unload: stopping the host")]
    private static partial void LogUnloadStoppingHost(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "OBSModule.Unload: done")]
    private static partial void LogUnloadDone(ILogger logger);
}
