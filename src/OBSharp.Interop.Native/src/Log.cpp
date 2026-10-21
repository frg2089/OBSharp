#include "Log.h"

#include <atomic>
#include <cstdarg>
#include <cstdio>

namespace obsharp {
namespace {

// 托管侧注入的日志函数。nullptr = 还没注入。
//
// 单独抽出来是为了让下面几处调用点（Qt 主线程、CEF IO 线程、任意工作线程）都读到
// 同一个值，而且不需要加锁。
std::atomic<obsharp_log_fn> g_logCallback{nullptr};

// 没注入回调时的兜底输出。
//
// 只在「托管侧还没起来」和「有人单独跑原生库排障」两种情况下会走到，所以直接写
// stderr 就够了 —— 这时候往 OBS 日志里写反而看不到（还没人接管）。
void WriteFallback(obsharp_log_level level, const char* message) {
  const char* name = "INFO";
  switch (level) {
  case OBSHARP_LOG_ERROR:
    name = "ERROR";
    break;
  case OBSHARP_LOG_WARNING:
    name = "WARN";
    break;
  case OBSHARP_LOG_INFO:
    name = "INFO";
    break;
  case OBSHARP_LOG_DEBUG:
    name = "DEBUG";
    break;
  }

  std::fprintf(stderr, "[OBSharp.Interop.Native] %s: %s\n", name, message);
}

} // namespace

void SetLogCallback(obsharp_log_fn fn) {
  // 允许传 nullptr 取消注册（托管侧在卸载 / 测试里会这么做）。
  g_logCallback.store(fn, std::memory_order_release);
}

void Log(obsharp_log_level level, const char* message) {
  if (!message)
    return;

  // memory_order_acquire 与上面的 release 配对：拿到指针就一定看得到它指向的代码
  // 已经就绪。
  if (obsharp_log_fn callback = g_logCallback.load(std::memory_order_acquire)) {
    callback(level, message);
    return;
  }

  WriteFallback(level, message);
}

void LogF(obsharp_log_level level, const char* format, ...) {
  if (!format)
    return;

  // 2 KB 足够覆盖本库所有诊断行；超长部分截断不报错，因为日志本身不该成为失败点。
  char buffer[2048];

  va_list args;
  va_start(args, format);
  const int written = std::vsnprintf(buffer, sizeof(buffer), format, args);
  va_end(args);

  if (written < 0)
    return;

  Log(level, buffer);
}

} // namespace obsharp
