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
        // 三种来源依次尝试，顺序不能换：宿主已经有的程序集必须复用宿主那份，
        // 否则插件和宿主会各自加载一份 OBSharp.Interop，跨边界的类型对不上。
        if (HostResolver.ResolveAssemblyToPath(assemblyName) is not null)
        {
            LogAssemblyFromHost(assemblyName.Name ?? "(unknown)");
            return base.Load(assemblyName);
        }

        if (Default.Assemblies.Any(i => i.GetName().Name == assemblyName.Name))
        {
            LogAssemblyAlreadyLoaded(assemblyName.Name ?? "(unknown)");
            return base.Load(assemblyName);
        }

        if (_pluginResolver.ResolveAssemblyToPath(assemblyName) is { Length: not 0 } path)
        {
            LogAssemblyFromPlugin(assemblyName.Name ?? "(unknown)", path);
            return LoadFromAssemblyPath(path);
        }

        // 走到这里说明插件自己也没带这个依赖：交给默认上下文，让它按常规搜索，
        // 失败时由运行时抛（日志里留一条，好跟「为什么 NoSuchMethod」对上）。
        LogAssemblyUnresolved(assemblyName.Name ?? "(unknown)");
        return base.Load(assemblyName);
    }

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
