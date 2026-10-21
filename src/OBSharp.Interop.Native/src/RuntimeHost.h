#pragma once

// RuntimeHost.h：模块与符号查找。
//
// 本库被 OB# 加载进 OBS 进程，宿主是 OBS 自己，所以**不链接 libobs**：需要宿主里的
// 符号（obs_get_module 之类）时在运行期从已经加载的模块里取。
//
// 这里的函数都是纯机制：给一个名字就查一个名字，不替调用方猜平台、不猜 soname。
// 公开 ABI 的 obsharp_find_module / obsharp_find_symbol 直接转发到这两个函数，
// 「Windows 上该试 obs.dll 还是 libobs.dll」这类判断在 C# 侧。

namespace obsharp {

// 找**已经加载**的模块（Windows HMODULE / Unix dlopen 句柄）。
//
// 从不因为查找而加载新模块：
//   * Windows 用 GetModuleHandleW —— 它本身就只查已加载的模块；
//   * Unix 用 dlopen(RTLD_NOW | RTLD_NOLOAD) —— RTLD_NOLOAD 是关键，去掉它
//     dlopen 会顺手把 .so 拉起来，那是「查找」变成「加载」，语义完全变了。
//
// 找不到返回 nullptr。
void* FindLoadedModule(const char* name);

// 在指定模块里查符号（GetProcAddress / dlsym）。
//
// module 为 nullptr 时按进程全局查（Windows 主程序 / dlsym(RTLD_DEFAULT)）。
void* FindSymbolInModule(void* module, const char* name);

} // namespace obsharp
