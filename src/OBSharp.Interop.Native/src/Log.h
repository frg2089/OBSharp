#pragma once

// Log.h：把日志输出到**托管侧注入的回调**。
//
// 为什么不再自己找 libobs 的 blog()：那是「日志往哪里写」的决定，属于托管侧。
// 本库曾经在启用时按平台名去猜 obs.dll / libobs.so 再 dlsym("blog")，等于把宿主
// 发现逻辑藏进了原生层，而且没法跟着 .NET 的日志过滤 / 结构化一起走。
//
// 现在的形状：
//   * 托管侧在 OBSharp.Interop 程序集加载时调 obsharp_set_log_callback() 传进函数指针；
//   * 本库所有日志都走那个指针；
//   * 还没注入时（托管侧尚未起来，或者正在单独跑原生库）退回 stderr，保证不静默丢日志。
//
// 级别数值沿用 libobs 的那一套（见公开头 obsharp_log_level），托管侧映射到
// ILogger 时不用再查一次表。

#include "Export.h"

#include <cstdint>

namespace obsharp {

// 由 obsharp_set_log_callback() 写入；nullptr 表示还没注入。
//
// 用原子指针而不是互斥量：日志可能在 CEF 的 IO 线程上打，取锁会跟别处的锁形成
// 环。日志注入是一个「一次性发布、之后只读」的场景，原子读足够。
void SetLogCallback(obsharp_log_fn fn);

// 写一行日志。任意线程可调用。
//
// message 为 NULL 时是空操作；level 取 obsharp_log_level 的数值。
void Log(obsharp_log_level level, const char* message);

// 便捷重载：内部逻辑出错时打一行，省得每个调用点都拼一次字符串。
//
// 用 snprintf 自己格式化，不把格式串交给宿主：格式串一旦从外面来就是格式化字符串
// 漏洞（%n 直接写内存）。
void LogF(obsharp_log_level level, const char* format, ...);

} // namespace obsharp
