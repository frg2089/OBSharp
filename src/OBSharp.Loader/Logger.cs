using System.Runtime.InteropServices;

namespace OBSharp.Loader;

public static partial class Logger
{
    public const int LOG_ERROR = 100;
    public const int LOG_WARNING = 200;
    public const int LOG_INFO = 300;
    public const int LOG_DEBUG = 400;

    public static void Debug(string text) => Log(LOG_DEBUG, text);
    public static void Info(string text) => Log(LOG_INFO, text);
    public static void Warn(string text) => Log(LOG_WARNING, text);
    public static void Error(string text) => Log(LOG_ERROR, text);

    private static void Log(int log_level, string text) => blog(log_level, $"[OBSharp.Loader]: {text}");

    [LibraryImport("obs", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial void blog(int log_level, string format);
}
