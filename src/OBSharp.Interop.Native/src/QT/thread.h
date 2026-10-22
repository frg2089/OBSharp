#pragma once

#include "obsharp.h"

namespace OBSharp {
  // 判断当前线程是否为 UI 线程
  OBSHARP_API bool OBSHARP_CALL obsharp_is_ui_thread(void);

  typedef void(OBSHARP_CALL* obsharp_ui_invoke_fn)(void* ptr);

  /*
   * 在 UI 线程上执行指定的回调函数，并**等它跑完**再返回（阻塞语义）。
   *
   * 已经在 UI 线程上时直接同步调用。投递失败返回 false（此时回调不会被执行）；
   * 成功返回 true。QCoreApplication 没建起来时也返回 false。
   */
  OBSHARP_API bool OBSHARP_CALL obsharp_ui_invoke(obsharp_ui_invoke_fn fn, void* ptr);
}