using System.Collections.Immutable;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OBSharp.Plugins;

internal sealed partial class PluginManager(ILogger<PluginManager> logger)
{
    private readonly Dictionary<Type, PluginLoadContext> _plugins = [];

    public IEnumerable<ServiceDescriptor> LoadPlugins()
    {
        logger.LogInformation("");
        yield break;
    }

    private ServiceDescriptor? LoadPlugin(string path)
    {
        logger.LogInformation("Loading plugin {}", path);

        PluginLoadContext context = new(path);
        var asm = context.LoadFromAssemblyPath(path);

        var plugins = asm.ExportedTypes.Where(static i => i.IsAssignableTo(typeof(ObsPlugin))).ToImmutableArray();
        if (plugins.Length is not 1)
        {
            context.Unload();
            return null;
        }

        var type = plugins[0];
        _plugins[type] = context;
        return ServiceDescriptor.Singleton(typeof(ObsPlugin), type);
    }
}
