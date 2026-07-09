using System.Runtime.InteropServices;

namespace OBSharp;

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

    private static void Log(int log_level, string text)
    {
        nint msg = Marshal.StringToCoTaskMemUTF8($"[OBSharp.Loader]: {text}");
        blog(log_level, msg);
        Marshal.FreeCoTaskMem(msg);
    }

    [LibraryImport("obs")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial void blog(int log_level, nint format);
}
