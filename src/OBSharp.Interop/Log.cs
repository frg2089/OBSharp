using System.Runtime.InteropServices;

namespace OBSharp.Interop;

/// <summary>
/// libobs 的日志入口 <c>blog()</c>。它是变参函数，过不了 C ABI 封送，
/// 所以绑定生成器跳过它 —— 这里手写一个「只传格式串」的入口（和旧生成物的壳一致）。
/// 注意 <paramref name="message"/> 直接当格式串用，里面的 % 会被 printf 解释。
/// </summary>
public static partial class LogExtension
{
    extension(Obs)
    {
        /// <summary>
        /// 写一行 libobs 日志（数值级别见 libobs 的 util/base.h）。
        /// </summary>
        public static void Log(ObsLogLevel logLevel, string message) => blog(logLevel, message);
    }

    [LibraryImport("obs", EntryPoint = "blog")]
    private static partial void blog(ObsLogLevel logLevel, [MarshalAs(UnmanagedType.LPUTF8Str)] string format);
}

public enum ObsLogLevel : int
{
    LOG_ERROR = 100,
    LOG_WARNING = 200,
    LOG_INFO = 300,
    LOG_DEBUG = 400
}
