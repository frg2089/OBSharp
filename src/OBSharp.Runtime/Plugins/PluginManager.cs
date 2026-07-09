using System.Collections.Immutable;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace OBSharp.Plugins;

public sealed partial class PluginManager(Dictionary<Type, PluginLoadContext> store, ILogger<PluginManager> logger)
{
    public static void LoadPlugins(IServiceCollection services, TextReader pluginList)
    {
        Dictionary<Type, PluginLoadContext> store = [];
        LinkedList<KeyValuePair<LogLevel, string>> log = [];
        while (pluginList.ReadLine() is string raw)
        {
            ReadOnlySpan<char> line = raw;
            if (line.Contains(';'))
                line = line[..line.IndexOf(';')];
            if (line.Contains('#'))
                line = line[..line.IndexOf('#')];

            line = line.Trim();
            if (line.IsEmpty)
                continue;

            ReadOnlySpan<char> path = default;
            ReadOnlySpan<char> status = default;
            foreach (var range in line.Split('='))
            {
                if (path.IsEmpty)
                {
                    path = line[range].Trim();
                }
                else
                {
                    status = line[range].Trim();
                    break;
                }
            }

            if (!bool.TryParse(status, out bool enable) || !enable)
                continue;

            string asmPath = path.ToString();
            log.AddLast(KeyValuePair.Create(LogLevel.Debug, $"Load plugin \"{asmPath}\"."));
            PluginLoadContext context = new(asmPath);
            var asm = context.LoadFromAssemblyPath(asmPath);

            var plugins = asm.ExportedTypes.Where(static i => i.IsAssignableTo(typeof(ObsPlugin))).ToImmutableArray();
            if (!plugins.Any())
            {
                log.AddLast(KeyValuePair.Create(LogLevel.Error, $"Cannot load plugin \"{path}\". because \"{path}\" have not implemented ObsPlugin."));
                context.Unload();
                continue;
            }
            else if (plugins.Length is not 1)
            {
                log.AddLast(KeyValuePair.Create(LogLevel.Error, $"Cannot load plugin \"{path}\". because \"{path}\" have multiple implemented ObsPlugin."));
                context.Unload();
                continue;
            }

            log.AddLast(KeyValuePair.Create(LogLevel.Information, $"Load plugin \"{asmPath}\" success."));
            var type = plugins[0];
            store[type] = context;
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(ObsPlugin), type));
        }

        services.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<PluginManager>>();
            var current = log.First;
            while (current is not null)
            {
                if (logger.IsEnabled(current.Value.Key))
                    logger.Log(current.Value.Key, "{}", current.Value.Value);
                current = current.Next;
            }

            return ActivatorUtilities.CreateInstance<PluginManager>(provider, store, logger);
        });
    }

    public void UnloadPlugins(Type pluginType)
    {
        if (!store.TryGetValue(pluginType, out var context))
            return;

        LogUnloadPlugin(pluginType);
        store.Remove(pluginType);
        context.Unload();
    }

    [LoggerMessage(LogLevel.Debug, "Unload plugin {PluginType}")]
    private partial void LogUnloadPlugin(Type pluginType);
}
