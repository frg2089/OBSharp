using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

using OBSharp.Plugins;

namespace OBSharp.Hosts;

public sealed class OBSModuleHostBuilder
{
    public IConfigurationBuilder Configuration { get; } = new ConfigurationBuilder();
    public IServiceCollection Service { get; } = new ServiceCollection();

    public OBSModuleHostBuilder(string dataPath)
    {
        Configuration.AddJsonFile(Path.Combine(dataPath, "appsettings.json"), true);

        string pluginList = Path.Combine(dataPath, "plugins.ini");
        if (File.Exists(pluginList))
            using (var fs = File.OpenText(pluginList))
                PluginManager.LoadPlugins(Service, fs);

        Service
            .AddSingleton<IDataFolderProvider>(new DataFolderProvider(dataPath));

        Service
            .AddSingleton<OBSModuleHost>()
            .AddOptions<OBSModuleHostOptions>()
            .BindConfiguration("Runtime")
            .PostConfigure(options => options.ContentPath = dataPath)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        Service.AddLogging(builder =>
        {
            builder.AddProvider(new OBSLoggerProvider());
            builder.AddConfiguration();
        });
    }

    public OBSModuleHost Build()
    {
        var config = Configuration.Build();
        Service.TryAddSingleton(config);
        Service.TryAddSingleton<IConfiguration>(config);
        var provider = Service.BuildServiceProvider();

        return provider.GetRequiredService<OBSModuleHost>();
    }
}
