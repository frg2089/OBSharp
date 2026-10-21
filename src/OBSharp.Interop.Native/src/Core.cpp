// Core.cpp：版本、日志注入、线程判定、模块符号查找的 ABI 出口。
//
// 这几个函数是「跨语言地基」：托管侧在程序集加载时（[ModuleInitializer]）第一件事
// 就是把日志函数指针交进来，之后才可能用到别的东西。它们不能依赖任何尚未初始化的
// 对象（Qt、CEF、obs-browser 都还没影），所以这里全是薄转发，唯一例外是版本串的
// 静态存储（见下）。

#include "Export.h"

#include "Log.h"
#include "RuntimeHost.h"
#include "Threading.h"

// 构建时由 CMakeLists 的 OBSHARP_NATIVE_VERSION 注入（值来自仓库根的
// Directory.Version.cmake）。本地没有版本文件时注入的就是 "0.0.0-local"。
//
// 这里的兜底分支是给「只拿这一个文件去编」的场景（IDE 里单文件语法检查、
// 别人把源码拷进别的构建系统）—— 没有它就得给每个构建系统都补一份定义。
#ifndef OBSHARP_NATIVE_VERSION
#define OBSHARP_NATIVE_VERSION "0.0.0-local"
#endif

namespace {

// 版本串用**静态存储**。
//
// 公开头承诺 obsharp_version() 返回的指针「静态存储，永远有效」。这一点跟别的
// 返回 const char* 的函数不同：那些走 BorrowedUtf8 的 thread_local 缓冲，会被同
// 线程的下一次调用覆盖。版本串是只读常量，放静态区最省事也最安全。
const char kVersion[] = OBSHARP_NATIVE_VERSION;

} // namespace

extern "C" {

OBSHARP_NATIVE_API const char *OBSHARP_NATIVE_CALL obsharp_version(void) {
  return kVersion;
}

// 托管侧注入日志函数。任意线程可调用；重复调用以最后一次为准；传 NULL 表示取消
// 注册（之后退回 stderr）。
//
// 这是本库唯一的日志来源：原生层不再自己去 host 里解析 libobs 的 blog()。
// 「日志往哪里写」是托管侧的决定，这里只保存指针。
OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_set_log_callback(obsharp_log_fn fn) {
  obsharp::SetLogCallback(fn);
}

// Qt 主线程判定。托管侧的 NativeApi.InvokeUI 靠它决定「直调还是封送」，
// 所以这个函数本身必须能在任意线程调用（它只读 QCoreApplication::instance()）。
OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_is_ui_thread(void) {
  return obsharp::IsUiThread();
}

// 同步切到 Qt 主线程。失败只有两种可能：fn 为空（参数错误），或者从非主线程调
// 且 Qt 应用/事件循环已经不在了（这时投递永远等不到，属于线程错误）。
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_invoke_ui(obsharp_ui_action_fn fn, void *user) {
  if (!fn)
    return OBSHARP_ERR_INVALID_ARGUMENT;

  return obsharp::RunOnUiThread(fn, user) ? OBSHARP_OK : OBSHARP_ERR_WRONG_THREAD;
}

// 模块/符号查找：本库刻意不替调用方猜平台（试 obs.dll 还是 libobs.so.0 是 C# 的
// 判断），只按给的名字查。名字为 NULL / 空串时下面两个实现直接返回 nullptr。
OBSHARP_NATIVE_API void *OBSHARP_NATIVE_CALL obsharp_find_module(const char *name) {
  return obsharp::FindLoadedModule(name);
}

OBSHARP_NATIVE_API void *OBSHARP_NATIVE_CALL obsharp_find_symbol(void *module, const char *name) {
  return obsharp::FindSymbolInModule(module, name);
}

} // extern "C"
