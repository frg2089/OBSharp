using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace OBSharp.Runtime;

internal sealed class OBSharpPluginLoadContext(string path)
    : AssemblyLoadContext(Path.GetFileNameWithoutExtension(path), true)
{
    private static readonly AssemblyDependencyResolver HostResolver = new(typeof(OBSharpPluginLoadContext).Assembly.Location);
    private readonly AssemblyDependencyResolver _pluginResolver = new(path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (HostResolver.ResolveAssemblyToPath(assemblyName) is not null)
            return base.Load(assemblyName);

        if (Default.Assemblies.Any(i => i.GetName().Name == assemblyName.Name))
            return base.Load(assemblyName);

        if (_pluginResolver.ResolveAssemblyToPath(assemblyName) is { Length: not 0 } path)
            return LoadFromAssemblyPath(path);

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
            return LoadUnmanagedDllFromPath(libpath);

        return base.LoadUnmanagedDll(unmanagedDllName);
    }
}
