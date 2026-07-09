using System.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

using OBSharp.Plugins;

namespace OBSharp.Hosts;

internal sealed class OBSModuleHostBuilder
{
    public IConfigurationBuilder Configuration { get; } = new ConfigurationBuilder();
    public IServiceCollection Service { get; } = new ServiceCollection();

    public OBSModuleHostBuilder(string dataPath)
    {
        Configuration.AddJsonFile(Path.Combine(dataPath, "appsettings.json"), true);

        string pluginList = Path.Combine(dataPath, "plugins.ini");
        using (TextReader fs = File.Exists(pluginList)
                ? File.OpenText(pluginList)
                : new StringReader(string.Empty))
            PluginManager.LoadPlugins(Service, fs);

        Service
            .AddSingleton<OBSModuleHost>()
            .AddOptions<OBSModuleHostOptions>()
            .BindConfiguration("Runtime")
            .PostConfigure(options => options.ContentPath = dataPath)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        Service
            .AddSingleton<IDataFolderProvider>(provider => provider.GetRequiredService<OBSModuleHost>())
            .AddSingleton<IRuntimeInfoProvider>(provider => provider.GetRequiredService<OBSModuleHost>());

        Service.AddLogging(builder =>
        {
            builder.AddProvider(new OBSLoggerProvider());
            builder.AddConfiguration();
        });
    }

    public OBSModuleHost Build()
    {
        var config = Configuration.Build();
        Service.AddSingleton(config);
        Service.AddSingleton<IConfiguration>(config);
        var provider = Service.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<OBSModuleHostBuilder>>();
        AppDomain.CurrentDomain.UnhandledException += (o, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                logger.LogCritical(ex, "Unhandled exception.");
            else if (logger.IsEnabled(LogLevel.Critical))
                logger.LogCritical("Unhandled exception. {ex}", e.ExceptionObject);

            if (e.IsTerminating)
                logger.LogCritical("Exited.");

            if (Debugger.IsAttached)
                Debugger.Break();
        };
        try
        {
            logger.LogInformation("Building OBSModuleHost.");
            return provider.GetRequiredService<OBSModuleHost>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Building OBSModuleHost failed.");
            throw;
        }
    }
}
