using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OBSharp.Hosts;

public sealed class OBSModuleHost(
    IConfigurationRoot configuration,
    IServiceProvider serviceProvider,
    ILogger<OBSModuleHost> logger)
{
    public IConfigurationRoot Configuration => configuration;
    public IServiceProvider Service => serviceProvider;
    public ILogger Logger => logger;
}
