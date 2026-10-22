#include "host.h"

#include <string>

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace OBSharp
{
  namespace
  {
#if defined(_WIN32)
    // UTF-8 → UTF-16。空字符串返回空（调用方按「没找到」处理）。
    std::wstring ToWide(const char* name) {
      const int needed = MultiByteToWideChar(CP_UTF8, 0, name, -1, nullptr, 0);
      if (needed <= 0)
        return {};

      std::wstring wide(static_cast<size_t>(needed), L'\0');
      if (MultiByteToWideChar(CP_UTF8, 0, name, -1, wide.data(), needed) <= 0)
        return {};

      wide.resize(static_cast<size_t>(needed - 1)); // 去掉 MultiByteToWideChar 补的 NUL
      return wide;
    }
#endif
  } // namespace

  void* obsharp_find_module(const char* name) {
    if (!name || !*name)
      return nullptr;

#if defined(_WIN32)
    // GetModuleHandleW 只查已加载的模块，天然满足「绝不因为查找而加载」。
    //
    // 名字是 UTF-8，这里转成宽字符再查：Windows 的模块名是 UTF-16，直接用窄字符
    // 版本会在非 ANSI 代码页下对不上（中文路径、非拉丁模块名都会踩）。
    const std::wstring wide = ToWide(name);
    if (wide.empty())
      return nullptr;

    return reinterpret_cast<void*>(GetModuleHandleW(wide.c_str()));
#else
    // RTLD_NOLOAD：只找已经加载的，绝不因为查找而把模块拉进来。去掉它 dlopen 会
    // 顺手加载，那就把「查找」变成了「加载」，语义完全变了。
    return dlopen(name, RTLD_NOW | RTLD_NOLOAD);
#endif
  }

  void* obsharp_find_symbol(void* module, const char* name) {
    if (!name || !*name)
      return nullptr;

#if defined(_WIN32)
    // module 为 NULL 时按「主程序」查：GetModuleHandleW(nullptr) 就是宿主进程自己，
    // 对应 Unix 上 dlsym(RTLD_DEFAULT) 的语义。
    HMODULE handle = module ? reinterpret_cast<HMODULE>(module) : GetModuleHandleW(nullptr);
    if (!handle)
      return nullptr;

    return reinterpret_cast<void*>(GetProcAddress(handle, name));
#else
    return dlsym(module ? module : RTLD_DEFAULT, name);
#endif
  }
}
