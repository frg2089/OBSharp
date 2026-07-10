using System.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

namespace OBSharp.Runtime;

internal sealed class OBSharpHostBuilder
{
    public IConfigurationBuilder Configuration { get; } = new ConfigurationBuilder();
    public IServiceCollection Service { get; } = new ServiceCollection();

    public OBSharpHostBuilder(string dataPath)
    {
        Configuration.AddJsonFile(Path.Combine(dataPath, "appsettings.json"), true);

        string pluginList = Path.Combine(dataPath, "plugins.ini");
        using (TextReader fs = File.Exists(pluginList)
                ? File.OpenText(pluginList)
                : new StringReader(string.Empty))
            OBSharpPluginManager.LoadPlugins(Service, fs);

        Service
            .AddSingleton<OBSharpHost>()
            .AddOptions<OBSharpHostOptions>()
            .BindConfiguration("Runtime")
            .PostConfigure(options => options.ContentPath = dataPath)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        Service
            .AddSingleton<IDataFolderProvider>(provider => provider.GetRequiredService<OBSharpHost>())
            .AddSingleton<IRuntimeInfoProvider>(provider => provider.GetRequiredService<OBSharpHost>());

        Service.AddLogging(builder =>
        {
            builder.AddProvider(new OBSharpLoggerProvider());
            builder.AddConfiguration();
        });
    }

    public OBSharpHost Build()
    {
        var config = Configuration.Build();
        Service.AddSingleton(config);
        Service.AddSingleton<IConfiguration>(config);
        var provider = Service.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<OBSharpHostBuilder>>();
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
            logger.LogInformation("Building OBSharpHost.");
            return provider.GetRequiredService<OBSharpHost>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Building OBSharpHost failed.");
            throw;
        }
    }
}
