#pragma once

#include <string>

namespace obsharp {

enum class LogLevel {
	Debug = 0,
	Info,
	Warning,
	Error,
};

// 日志前缀，默认 "OBSharp.Interop.Browser.Native"。
void SetLogTag(const std::string &tag);

// 写一条日志：优先转给宿主进程里的 libobs blog()（这样会出现在 OBS 日志里），
// 解析不到就落到 stderr / 调试输出。
void Log(LogLevel level, const std::string &message);

inline void LogDebug(const std::string &message) { Log(LogLevel::Debug, message); }
inline void LogInfo(const std::string &message) { Log(LogLevel::Info, message); }
inline void LogWarning(const std::string &message) { Log(LogLevel::Warning, message); }
inline void LogError(const std::string &message) { Log(LogLevel::Error, message); }

// 「最近一次错误」的存放点：按线程（thread_local），定义在 Log.cpp 里，公开 ABI 的
// obsharp_browser_native_last_error() 就是读它。
//
// 之所以单独开放这两个函数，是因为窗口实现在另一个编译单元（Window.cpp）：
// 它失败时也得把原因写进来，否则托管侧拿到的 exception 消息是空的或者上一次的残留。
//
// 名字刻意不叫 GetLastError / SetLastError：Windows 上那两个是 Win32 API 的宏名，
// 放在 namespace obsharp 里会把 KestrelClient.cpp 等文件里的 Win32 调用劫持掉。
void RecordLastError(const std::string &message);

// 当前线程最近一次错误；没有错误时是空串。借用指针，下一次 RecordLastError 后失效。
const char *LastErrorText();

} // namespace obsharp
