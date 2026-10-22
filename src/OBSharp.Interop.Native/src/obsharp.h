#pragma once

// OBSharp 版本号
#ifndef OBSHARP_VERSION
#define OBSHARP_VERSION "0.0.0-local"
#endif

#ifndef OBSHARP_API
#if defined(_WIN32)
#define OBSHARP_API extern "C" __declspec(dllexport)
#elif defined(__GNUC__) || defined(__clang__)
#define OBSHARP_API extern "C" __attribute__((visibility("default")))
#else
#define OBSHARP_API extern "C"
#endif
#endif

#ifndef OBSHARP_CALL
#if defined(_MSC_VER)
#define OBSHARP_CALL __cdecl
#else
#define OBSHARP_CALL
#endif
#endif

namespace OBSharp
{
  // 获取 OBSharp 版本号
  OBSHARP_API const char* OBSHARP_CALL obsharp_get_version(void);
}