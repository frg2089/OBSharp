using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

namespace OBSharp.Runtime;

internal sealed class OBSharpHostBuilder(string dataPath)
{
    private ILogger<OBSharpHostBuilder> _logger = default!;
    public OBSharpHost Build()
    {
        LoggerFactory loggerFactory = new();
        loggerFactory.AddProvider(new ObsLoggerProvider("OBSharp"));
        _logger = loggerFactory.CreateLogger<OBSharpHostBuilder>();

        NativeLibrary.SetDllImportResolver(typeof(Interop.Browser.WebView).Assembly, NativeLibraryResolver);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            _logger.LogInformation("Building OBSharpHost.");
            var options = new OBSharpHostOptions(dataPath);
            OBSharpPluginManager pluginManager = new(options, loggerFactory);

            return new OBSharpHost(pluginManager, loggerFactory, loggerFactory.CreateLogger<OBSharpHost>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Building OBSharpHost failed.");
            throw;
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        {
            if (e.ExceptionObject is Exception ex)
                _logger.LogCritical(ex, "Unhandled exception.");
            else if (_logger.IsEnabled(LogLevel.Critical))
                _logger.LogCritical("Unhandled exception. {ex}", e.ExceptionObject);

            if (e.IsTerminating)
                _logger.LogCritical("Exited.");

            if (Debugger.IsAttached)
                Debugger.Break();
        }
    }

    private nint NativeLibraryResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string prefix = string.Empty;
        string surfix = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            surfix = ".dll";
        }
        else if (OperatingSystem.IsLinux())
        {
            prefix = "lib";
            surfix = ".so";
        }
        else if (OperatingSystem.IsMacOS())
        {
            prefix = "lib";
            surfix = ".dylib";
        }

        var libpath = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", $"{prefix}{libraryName}{surfix}");
        if (!File.Exists(libpath))
            libpath = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", $"{libraryName}{surfix}");
        if (!File.Exists(libpath))
            libpath = Path.Combine(AppContext.BaseDirectory, $"{prefix}{libraryName}{surfix}");
        if (!File.Exists(libpath))
            libpath = Path.Combine(AppContext.BaseDirectory, $"{libraryName}{surfix}");

        if (File.Exists(libpath))
            return NativeLibrary.Load(libpath);

        return 0;

    }
}
