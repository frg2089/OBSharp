#pragma once

namespace obsharp {

// 在当前进程里找一个已经加载的符号（拿不到就返回 nullptr）。
//
// 用途：本库被 OB# 加载进 OBS 进程，宿主是 OBS 自己。我们不链接 libobs，而是
// 运行时从已经加载的模块里取符号：
//   * libobs 的 blog / obs_get_module / obs_get_module_lib（Windows 上在
//     obs.dll，Unix 上是 libobs.so / libobs.dylib，由 OBS 主程序加载）；
//   * obs-browser 的 obs_browser_create_qcef（这个走 obs_get_module 拿句柄，
//     见 HostApi.cpp）。
//
// Windows 下按模块名找（GetModuleHandleW），找不到再查主程序；其余平台先按
// soname RTLD_NOLOAD 找，最后兜底 dlsym(RTLD_DEFAULT)。
void *FindLoadedSymbol(const char *name);

// 在指定模块句柄里找符号；句柄由 obs_get_module_lib 之类的接口给出。
void *FindSymbolInModule(void *module, const char *name);

} // namespace obsharp
