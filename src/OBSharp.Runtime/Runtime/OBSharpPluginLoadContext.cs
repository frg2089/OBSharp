using System.Reflection;
using System.Runtime.Loader;

namespace OBSharp.Runtime;

/// <summary>
/// 每个插件一份的加载上下文（可卸载）。
/// 凡是宿主自己能解析到的程序集（OBSharp.SDK、OBSharp.Interop、
/// Microsoft.Extensions.* ……）一律交给默认上下文加载：否则插件自己的副本会带来
/// 第二份类型身份，<c>IsAssignableTo(typeof(ObsPlugin))</c> 会失败、注入
/// <c>ILogger&lt;T&gt;</c> 也解析不到，插件永远加载不上。
/// </summary>
public sealed class OBSharpPluginLoadContext(
    string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
{
    // 宿主（OBSharp.Runtime）的依赖解析器，靠它旁边的 OBSharp.Runtime.deps.json。
    private static readonly AssemblyDependencyResolver HostResolver = new(typeof(OBSharpPluginLoadContext).Assembly.Location);
    private readonly AssemblyDependencyResolver _pluginResolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (HostResolver.ResolveAssemblyToPath(assemblyName) is not null)
            return null;

        string? path = _pluginResolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
