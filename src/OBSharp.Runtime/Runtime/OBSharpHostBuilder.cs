using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

internal sealed class OBSharpHostBuilder(string dataPath)
{
    public OBSharpHost Build()
    {
        OBSharpLoggerFactory loggerFactory = new();
        ILogger<OBSharpHostBuilder> logger = loggerFactory.CreateLogger<OBSharpHostBuilder>();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
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

            var options = new OBSharpHostOptions(dataPath);
            OBSharpPluginManager pluginManager = new(options, loggerFactory);

            return new OBSharpHost(pluginManager, loggerFactory, loggerFactory.CreateLogger<OBSharpHost>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Building OBSharpHost failed.");
            throw;
        }
    }
}
