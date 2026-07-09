using System.Text;

using ObsInterop;

namespace OBSharp;

public static class Logger
{
    public static void Debug(string text) => Log(ObsBase.LOG_DEBUG, text);
    public static void Info(string text) => Log(ObsBase.LOG_INFO, text);
    public static void Warn(string text) => Log(ObsBase.LOG_WARNING, text);
    public static void Error(string text) => Log(ObsBase.LOG_ERROR, text);

    private static unsafe void Log(int log_level, string text)
    {
        byte[] asciiBytes = Encoding.UTF8.GetBytes($"[OBSharp.Loader]: {text}");
        fixed (byte* logMessagePtr = asciiBytes)
            ObsBase.blog(log_level, (sbyte*)logMessagePtr);
    }
}
