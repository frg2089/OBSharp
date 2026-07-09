using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

namespace OBSharp.Hosts;

public sealed class OBSModuleHostBuilder
{
    public IConfigurationBuilder Configuration { get; } = new ConfigurationBuilder();
    public IServiceCollection Service { get; } = new ServiceCollection();

    public OBSModuleHostBuilder(string dataPath)
    {
        Configuration.AddJsonFile(Path.Combine(dataPath, "appsettings.json"), true);
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
        return ActivatorUtilities.CreateInstance<OBSModuleHost>(provider);
    }
}
