#pragma once

#include "obsharp.h"

#include <QtWidgets/QApplication>
#include <QtWidgets/QWidget>

namespace OBSharp::Application
{
  // ===== 事件循环 =====
  // exec 会阻塞直到 quit()/exit() 被调用；C# 侧通常在启动时调用一次
  OBSHARP_API int  OBSHARP_CALL obsharp_application_exec();
  OBSHARP_API void OBSHARP_CALL obsharp_application_exit(int return_code);
  OBSHARP_API void OBSHARP_CALL obsharp_application_quit();

  // ===== 窗口查询 =====
  // 返回的 QWidget* 不拥有所有权，C# wrapper 必须 ownsHandle: false
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_application_active_window();
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_application_active_modal_widget();
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_application_active_popup_widget();
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_application_focus_widget();
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_application_widget_at(int x, int y);
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_application_top_level_at(int x, int y);

  // ===== 窗口列表 =====
  // 返回的数组为"借用"缓冲，下次调用同名函数时内容会失效；
  // C# 侧必须在本次调用结束后立即复制指针
  OBSHARP_API QWidget** OBSHARP_CALL obsharp_application_all_widgets(size_t* size);
  OBSHARP_API QWidget** OBSHARP_CALL obsharp_application_top_level_widgets(size_t* size);

  // ===== 关闭 =====
  OBSHARP_API void OBSHARP_CALL obsharp_application_close_all_windows();

  // ===== 样式表 =====
  OBSHARP_API const char* OBSHARP_CALL obsharp_application_get_style_sheet();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_style_sheet(const char* sheet);

  // ===== 字体（拆成简单字段，避免让 C# 处理 QFont）=====
  OBSHARP_API const char* OBSHARP_CALL obsharp_application_get_font_family();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_font_family(const char* family);
  OBSHARP_API int  OBSHARP_CALL obsharp_application_get_font_point_size();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_font_point_size(int size);
  OBSHARP_API bool OBSHARP_CALL obsharp_application_get_font_bold();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_font_bold(bool bold);
  OBSHARP_API bool OBSHARP_CALL obsharp_application_get_font_italic();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_font_italic(bool italic);

  // ===== 交互参数 =====
  OBSHARP_API int  OBSHARP_CALL obsharp_application_get_double_click_interval();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_double_click_interval(int ms);
  OBSHARP_API int  OBSHARP_CALL obsharp_application_get_cursor_flash_time();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_cursor_flash_time(int ms);
  OBSHARP_API int  OBSHARP_CALL obsharp_application_get_start_drag_time();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_start_drag_time(int ms);
  OBSHARP_API int  OBSHARP_CALL obsharp_application_get_start_drag_distance();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_start_drag_distance(int px);
  OBSHARP_API int  OBSHARP_CALL obsharp_application_get_wheel_scroll_lines();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_wheel_scroll_lines(int lines);
  OBSHARP_API int  OBSHARP_CALL obsharp_application_get_keyboard_input_interval();
  OBSHARP_API void OBSHARP_CALL obsharp_application_set_keyboard_input_interval(int ms);

  // ===== 提示 =====
  OBSHARP_API void OBSHARP_CALL obsharp_application_beep();
  OBSHARP_API void OBSHARP_CALL obsharp_application_alert(QWidget* widget, int duration_ms);
  OBSHARP_API void OBSHARP_CALL obsharp_application_about_qt();
}