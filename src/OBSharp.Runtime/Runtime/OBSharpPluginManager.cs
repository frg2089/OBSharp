using System.Collections.Immutable;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

public sealed partial class OBSharpPluginManager(Dictionary<Type, OBSharpPluginLoadContext> store, ILogger<OBSharpPluginManager> logger, IServiceProvider serviceProvider)
{
    public static void LoadPlugins(IServiceCollection services, TextReader pluginList)
    {
        Dictionary<Type, OBSharpPluginLoadContext> store = [];
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
            log.AddLast(KeyValuePair.Create(LogLevel.Debug, $"Loading plugin \"{asmPath}\"."));
            OBSharpPluginLoadContext context = new(asmPath);
            ImmutableArray<Type> plugins;
            try
            {
                var asm = context.LoadFromAssemblyPath(asmPath);
                plugins = [.. asm.ExportedTypes.Where(static i => i.IsClass && !i.IsAbstract && typeof(ObsPlugin).IsAssignableFrom(i))];
            }
            catch (Exception ex)
            {
                log.AddLast(KeyValuePair.Create(LogLevel.Error, $"Cannot load plugin \"{asmPath}\".\n{ex}"));
                context.Unload();
                continue;
            }

            if (plugins.IsEmpty)
            {
                log.AddLast(KeyValuePair.Create(LogLevel.Error, $"Cannot load plugin \"{asmPath}\": it has no ObsPlugin implementation."));
                context.Unload();
                continue;
            }

            if (plugins.Length is not 1)
            {
                log.AddLast(KeyValuePair.Create(LogLevel.Error, $"Cannot load plugin \"{asmPath}\": it has {plugins.Length} ObsPlugin implementations."));
                context.Unload();
                continue;
            }

            log.AddLast(KeyValuePair.Create(LogLevel.Information, $"Loaded plugin \"{asmPath}\"."));
            var type = plugins[0];
            if (!store.TryAdd(type, context))
            {
                log.AddLast(KeyValuePair.Create(LogLevel.Warning, $"Plugin \"{asmPath}\" is already loaded, skipped."));
                context.Unload();
                continue;
            }

            // 按插件自己的类型注册成单例：宿主靠它构造插件，插件构造函数里
            // 于是能注入任何宿主注册的服务（ILogger<T>、IConfiguration ……）。
            services.TryAdd(ServiceDescriptor.Singleton(type, type));
        }

        services.AddSingleton<OBSharpPluginManager>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<OBSharpPluginManager>>();
            var current = log.First;
            while (current is not null)
            {
                if (logger.IsEnabled(current.Value.Key))
                    logger.Log(current.Value.Key, "{}", current.Value.Value);
                current = current.Next;
            }

            return new(store, logger, provider);
        });
    }

    public IEnumerable<ObsPlugin> GetPlugins()
    {
        // 拿到快照再遍历：构造插件时可能失败，卸载时又会改动 store。
        foreach (var type in store.Keys.ToArray())
        {
            ObsPlugin? plugin = null;
            try
            {
                plugin = serviceProvider.GetService(type) as ObsPlugin;
            }
            catch (Exception ex)
            {
                LogCreatePluginFailed(ex, type);
            }

            if (plugin is not null)
                yield return plugin;
        }
    }

    public void UnloadPlugin(ObsPlugin plugin)
    {
        var pluginType = plugin.GetType();
        try
        {
            plugin.Unload();
            if (plugin is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            LogUnloadPluginFailed(ex, pluginType);
        }
        finally
        {
            // 插件自己抛异常也要把加载上下文卸掉，不然程序集一直留在内存里。
            if (store.Remove(pluginType, out var context))
            {
                LogUnloadPlugin(pluginType);
                context.Unload();
            }
        }
    }

    [LoggerMessage(LogLevel.Debug, "Unload plugin {PluginType}")]
    private partial void LogUnloadPlugin(Type pluginType);

    [LoggerMessage(LogLevel.Error, "Cannot create plugin {PluginType}.")]
    private partial void LogCreatePluginFailed(Exception exception, Type pluginType);

    [LoggerMessage(LogLevel.Error, "Plugin {PluginType} failed to unload.")]
    private partial void LogUnloadPluginFailed(Exception exception, Type pluginType);
}
