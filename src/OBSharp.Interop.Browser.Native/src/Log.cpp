#include "Log.h"

#include "RuntimeHost.h"

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>

#if defined(_WIN32)
#include <windows.h>
#endif

namespace obsharp {
namespace {

// 最近一次错误的描述。按线程存，免得跨线程互相覆盖。
thread_local std::string t_lastError;

// libobs 的日志级别（util/base.h）：DEBUG=400 / INFO=300 / WARNING=200 / ERROR=500。
// 这里刻意不引入 libobs 头，所以把数值抄下来。
constexpr int kObsDebug = 400;
constexpr int kObsInfo = 300;
constexpr int kObsWarning = 200;
constexpr int kObsError = 500;

// blog(int level, const char *format, ...)
using BlogFn = void (*)(int, const char *, ...);

std::mutex g_logMutex;
std::string g_logTag = "OBSharp.Interop.Browser.Native";
BlogFn g_blog = nullptr;
bool g_blogResolved = false;

BlogFn ResolveBlog() {
	if (!g_blogResolved) {
		g_blogResolved = true;
		g_blog = reinterpret_cast<BlogFn>(FindLoadedSymbol("blog"));
	}

	return g_blog;
}

int ToObsLevel(LogLevel level) {
	switch (level) {
	case LogLevel::Debug:
		return kObsDebug;
	case LogLevel::Info:
		return kObsInfo;
	case LogLevel::Warning:
		return kObsWarning;
	case LogLevel::Error:
		return kObsError;
	}

	return kObsInfo;
}

} // namespace

void SetLogTag(const std::string &tag) {
	std::lock_guard<std::mutex> lock(g_logMutex);
	if (!tag.empty())
		g_logTag = tag;
}

void Log(LogLevel level, const std::string &message) {
	std::string line;
	{
		std::lock_guard<std::mutex> lock(g_logMutex);
		line = "[" + g_logTag + "] " + message;
	}

	if (BlogFn blog = ResolveBlog()) {
		// 宿主有 libobs：交给它，会写进 OBS 的日志文件。
		blog(ToObsLevel(level), "%s", line.c_str());
		return;
	}

#if defined(_WIN32)
	// 没有 libobs（比如单独跑测试）时落调试输出，VS 的输出窗口能看到。
	OutputDebugStringA(line.c_str());
	OutputDebugStringA("\n");
	fputs(line.c_str(), stderr);
	fputc('\n', stderr);
#else
	fputs(line.c_str(), stderr);
	fputc('\n', stderr);
#endif
}

void RecordLastError(const std::string &message) {
	t_lastError = message;
}

const char *LastErrorText() {
	return t_lastError.c_str();
}

} // namespace obsharp
