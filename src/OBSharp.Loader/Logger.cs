using OBSharp.PInvoke;

namespace OBSharp.Loader;

public static class Logger
{
    public const int LOG_ERROR = 100;
    public const int LOG_WARNING = 200;
    public const int LOG_INFO = 300;
    public const int LOG_DEBUG = 400;

    public static void Debug(string text) => Log(LOG_DEBUG, text);
    public static void Info(string text) => Log(LOG_INFO, text);
    public static void Warn(string text) => Log(LOG_WARNING, text);
    public static void Error(string text) => Log(LOG_ERROR, text);

    private static void Log(int logLevel, string text) => ObsExtensions.Log(logLevel, $"[OBSharp.Loader]: {text}");
}
