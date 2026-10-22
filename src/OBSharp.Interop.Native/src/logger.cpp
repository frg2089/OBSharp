#include "logger.h"

#include <cstdarg>
#include <cstdio>

namespace OBSharp
{
  obsharp_log_fn g_logger = nullptr;

  void obsharp_set_logger(obsharp_log_fn fn) {
    g_logger = fn;
  }

  void LogCore(obsharp_log_level level, const char* message) {
    if (!message)
      return;

    if (g_logger) {
      g_logger(level, message);
      return;
    }

    const char* name;
    switch (level) {
    case OBSHARP_LOG_TRACE:
      name = "TRACE";
      break;
    case OBSHARP_LOG_DEBUG:
      name = "DEBUG";
      break;
    case OBSHARP_LOG_INFOMATION:
      name = "INFOMATION";
      break;
    case OBSHARP_LOG_WARNING:
      name = "WARNING";
      break;
    case OBSHARP_LOG_ERROR:
      name = "ERROR";
      break;
    case OBSHARP_LOG_CRITICAL:
      name = "CRITICAL";
      break;
    case OBSHARP_LOG_NONE:
      return;
    }

    std::fprintf(stderr, "[OBSharp.Interop.Native] %s: %s\n", name, message);
  }

  void Log(obsharp_log_level level, const char* format, ...) {
    if (!format)
      return;

    char buffer[1024];

    va_list args;
    va_start(args, format);
    const int written = std::vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);

    if (written < 0)
      return;

    LogCore(level, buffer);
  }
}