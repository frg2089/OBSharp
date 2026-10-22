#pragma once

// window.h：托管侧用来装浏览器的「普通顶层窗口」。
//
// 这里只有**一个**导出，因为它只是个容器。标题、尺寸、是否置顶、是否可缩放都不在
// 这里定：建完窗口之后用 obsharp_widget_* 一套转发逐个摆布。把参数搬进 C ABI 会
// 多出一份 options 结构体、多一处「结构体版本不一致要不要兼容」的问题，而收益只是
// 省掉几个 P/Invoke 调用 —— 不值得。

#include "obsharp.h"

#include <QtWidgets/QWidget>

namespace OBSharp::Window
{
  // 用户点了窗口的关闭按钮。此时窗口已经被隐藏（不是销毁）。
  typedef void(OBSHARP_CALL* obsharp_window_close_callback)(QWidget* window, void* user);

  /*
   * 建一个空的顶层窗口（Qt::Window），返回它的 QWidget*。
   *
   * 回调在这里一次性接管，可以为 NULL；user 原样透传。
   *
   * 销毁：先 obsharp_widget_set_content(window, NULL) 摘掉内容，再
   * obsharp_widget_hide + obsharp_widget_destroy。
   */
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_window_create(obsharp_window_close_callback on_close, void* user);
}
