using OBSharp.Interop;

namespace OBSharp.Loader;

public static class Logger
{
    public static void Debug(string text) => Log(ObsLogLevel.LOG_DEBUG, text);
    public static void Info(string text) => Log(ObsLogLevel.LOG_INFO, text);
    public static void Warn(string text) => Log(ObsLogLevel.LOG_WARNING, text);
    public static void Error(string text) => Log(ObsLogLevel.LOG_ERROR, text);

    private static void Log(ObsLogLevel logLevel, string text) => Obs.Log(logLevel, $"[OBSharp.Loader]: {text}");
}
