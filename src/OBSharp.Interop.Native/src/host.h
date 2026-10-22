#pragma once

// host.h：宿主进程里「已加载模块」的符号查找。
//
// 本库刻意不自己找宿主（不猜 obs.dll / libobs.so.0、不猜 obs-browser 在哪）：那属于
// 「宿主怎么发现」的业务判断，由托管侧按平台决定。这里只提供机械的查找原语，
// 且**绝不因为查找而加载新模块**。

#include "obsharp.h"

namespace OBSharp
{
  // 找**已经加载**的模块，返回 OS 模块句柄（Windows HMODULE / Unix 的 dlopen 句柄）。
  // 找不到返回 NULL。任意线程可调用。
  OBSHARP_API void* OBSHARP_CALL obsharp_find_module(const char* name);

  // 在模块里查符号（GetProcAddress / dlsym）。module 为 NULL 时按进程全局查
  // （dlsym(RTLD_DEFAULT) / 主程序）。找不到返回 NULL。任意线程可调用。
  OBSHARP_API void* OBSHARP_CALL obsharp_find_symbol(void* module, const char* name);
}
