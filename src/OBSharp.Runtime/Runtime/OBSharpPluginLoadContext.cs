using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

internal sealed partial class OBSharpPluginLoadContext(string path, ILogger logger)
    : AssemblyLoadContext(Path.GetFileNameWithoutExtension(path), true)
{
    private static readonly AssemblyDependencyResolver HostResolver = new(typeof(OBSharpPluginLoadContext).Assembly.Location);
    private readonly AssemblyDependencyResolver _pluginResolver = new(path);

    /// <summary>
    /// Logger used by the source-generated log methods below.
    /// </summary>
    /// <remarks>
    /// Must be an instance field (not a primary-constructor parameter capture) — the
    /// <see cref="LoggerMessageAttribute"/> generator looks for a field of this exact name.
    /// </remarks>
    private readonly ILogger _logger = logger;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? string.Empty;

        // 三种来源依次尝试，顺序不能换：宿主已经有的程序集必须复用宿主那份，
        // 否则插件和宿主会各自加载一份 OBSharp.Interop，跨边界的类型对不上。
        if (HostResolver.ResolveAssemblyToPath(assemblyName) is not null)
        {
            LogAssemblyFromHost(name);
            return base.Load(assemblyName);
        }

        if (Default.Assemblies.Any(i => i.GetName().Name == assemblyName.Name))
        {
            LogAssemblyAlreadyLoaded(name);
            return base.Load(assemblyName);
        }

        if (_pluginResolver.ResolveAssemblyToPath(assemblyName) is { Length: not 0 } path)
        {
            LogAssemblyFromPlugin(name, path);
            return LoadFromAssemblyPath(path);
        }

        // 共享框架（Microsoft.NETCore.App / Microsoft.AspNetCore.App）里的程序集必须显式
        // 找 Default 那个上下文要。
        //
        // 为什么 base.Load() 不行：自定义 AssemblyLoadContext 的 Load 只查自己的
        // AssemblyDependencyResolver 和本 ALC 已加载的程序集，**不会**回落到共享框架 ——
        // 框架程序集是按需装配的，也不一定已经在 Default.Assemblies 里。于是
        // Microsoft.AspNetCore.* 一路返回 null，插件加载在 GetExportedTypes 阶段炸成
        // FileNotFoundException。宿主是 ASP.NET 应用（runtimeconfig 里列了
        // Microsoft.AspNetCore.App），Default 上下文才认识这些程序集。
        //
        // FileNotFoundException 要吞掉：这个前缀也覆盖插件自己带的
        // Microsoft.Extensions.* 包，而那些是由上面的 _pluginResolver 解析的，走到这里
        // 说明确实没有，继续 base.Load() 让它按常规失败。
        if (IsSharedFrameworkAssembly(name))
        {
            try
            {
                Assembly fromDefault = Default.LoadFromAssemblyName(assemblyName);
                LogAssemblyFromSharedFramework(name);
                return fromDefault;
            }
            catch (FileNotFoundException)
            {
                // 不是框架程序集；交给下面的常规路径。
            }
        }

        // 走到这里说明宿主、已加载集合、插件目录、共享框架都没有：交给默认上下文，
        // 让它按常规搜索，失败时由运行时抛（日志里留一条，好跟「为什么 NoSuchMethod」对上）。
        LogAssemblyUnresolved(name);
        return base.Load(assemblyName);
    }

    /// <summary>
    /// 是否属于需要向默认上下文索取的共享框架程序集。
    /// </summary>
    /// <remarks>
    /// 按前缀判断而不是精确列表：共享框架里的程序集有几百个，逐个列举既不可维护，
    /// 也会在 .NET 版本升级时悄悄漏掉新加的。前缀之外的都走插件自己的 resolver。
    /// </remarks>
    private static bool IsSharedFrameworkAssembly(string name)
        => name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Extensions", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Net.Http", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase);

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libpath = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", unmanagedDllName);
        if (!File.Exists(libpath))
            libpath = Path.Combine(AppContext.BaseDirectory, unmanagedDllName);
        if (!File.Exists(libpath))
            libpath = Path.Combine(Path.GetDirectoryName(path)!, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", unmanagedDllName);
        if (!File.Exists(libpath))
            libpath = Path.Combine(Path.GetDirectoryName(path)!, unmanagedDllName);

        if (File.Exists(libpath))
        {
            LogUnmanagedDllResolved(unmanagedDllName, libpath);
            return LoadUnmanagedDllFromPath(libpath);
        }

        LogUnmanagedDllUnresolved(unmanagedDllName);
        return base.LoadUnmanagedDll(unmanagedDllName);
    }

    [LoggerMessage(LogLevel.Debug, "LoadContext: assembly {AssemblyName} comes from the shared framework")]
    private partial void LogAssemblyFromSharedFramework(string assemblyName);

    [LoggerMessage(LogLevel.Debug, "LoadContext: assembly {AssemblyName} comes from the host")]
    private partial void LogAssemblyFromHost(string assemblyName);

    [LoggerMessage(LogLevel.Debug, "LoadContext: assembly {AssemblyName} is already loaded in the default context")]
    private partial void LogAssemblyAlreadyLoaded(string assemblyName);

    [LoggerMessage(LogLevel.Debug, "LoadContext: assembly {AssemblyName} resolved from the plugin directory: {Path}")]
    private partial void LogAssemblyFromPlugin(string assemblyName, string path);

    [LoggerMessage(LogLevel.Debug, "LoadContext: assembly {AssemblyName} not found in host or plugin; deferring to the default context")]
    private partial void LogAssemblyUnresolved(string assemblyName);

    [LoggerMessage(LogLevel.Debug, "LoadContext: native library {LibraryName} resolved to {Path}")]
    private partial void LogUnmanagedDllResolved(string libraryName, string path);

    [LoggerMessage(LogLevel.Debug, "LoadContext: native library {LibraryName} not found; deferring to the default resolver")]
    private partial void LogUnmanagedDllUnresolved(string libraryName);
}
