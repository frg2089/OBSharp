#pragma once

#include "obsharp.h"

namespace OBSharp {
  // 日志等级
  typedef enum obsharp_log_level
  {
    OBSHARP_LOG_TRACE = 0,
    OBSHARP_LOG_DEBUG = 1,
    OBSHARP_LOG_INFOMATION = 2,
    OBSHARP_LOG_WARNING = 3,
    OBSHARP_LOG_ERROR = 4,
    OBSHARP_LOG_CRITICAL = 5,
    OBSHARP_LOG_NONE = 6,
  } obsharp_log_level;

  // 日志回调函数类型
  typedef void(OBSHARP_CALL* obsharp_log_fn)(obsharp_log_level level, const char* message);

  // 设置日志回调函数
  OBSHARP_API void OBSHARP_CALL obsharp_set_logger(obsharp_log_fn fn);

  void LogCore(obsharp_log_level level, const char* message);
  void Log(obsharp_log_level level, const char* format, ...);
}