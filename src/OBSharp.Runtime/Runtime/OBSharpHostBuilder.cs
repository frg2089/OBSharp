using System.Diagnostics;
using System.Text.Json;

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

            var options = new OBSharpHostOptions(ReadRuntimeFramework(dataPath, logger), dataPath);
            OBSharpPluginManager pluginManager = new(options, loggerFactory);

            return new OBSharpHost(pluginManager, loggerFactory, loggerFactory.CreateLogger<OBSharpHost>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Building OBSharpHost failed.");
            throw;
        }
    }

    // appsettings.json 里只有运行时框架这一项（Loader 在启动 CoreCLR 之前也读同一个键）。
    internal static RuntimeFramework ReadRuntimeFramework(string dataPath, ILogger logger)
    {
        string path = Path.Combine(dataPath, "appsettings.json");
        if (!File.Exists(path))
            return RuntimeFramework.Core;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.TryGetProperty("Runtime", out JsonElement runtime) &&
                runtime.TryGetProperty("RuntimeFramework", out JsonElement framework) &&
                framework.ValueKind is JsonValueKind.String &&
                Enum.TryParse(framework.GetString(), ignoreCase: true, out RuntimeFramework value))
                return value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot read {Path}.", path);
        }

        return RuntimeFramework.Core;
    }
}
