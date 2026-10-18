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

} // namespace obsharp
