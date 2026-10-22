#pragma once

#include "obsharp.h"

#include <QtWidgets/QWidget>

#include <cstdint>

namespace OBSharp::Widget
{
  // ===== 生命周期 =====
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_widget_create(QWidget* parent);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_destroy(QWidget* widget);

  // ===== 窗口识别 =====
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_window(QWidget* widget);
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_modal(QWidget* widget);
  OBSHARP_API Qt::WindowModality OBSHARP_CALL obsharp_widget_get_window_modality(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_window_modality(QWidget* widget, Qt::WindowModality modality);

  // ===== 标题 / 透明度 =====
  OBSHARP_API const char* OBSHARP_CALL obsharp_widget_get_window_title(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_window_title(QWidget* widget, const char* title);
  OBSHARP_API double OBSHARP_CALL obsharp_widget_get_window_opacity(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_window_opacity(QWidget* widget, double opacity);

  // ===== 启用 =====
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_enabled(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_enabled(QWidget* widget, bool enabled);

  // ===== 可见性 / 显示状态 =====
  // 注意：hide() / set_visible(false) 不会触发 QFileDialog 的 finished；
  // 要程序化关闭对话框，请用 obsharp_widget_close 或 filepicker 的 reject。
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_visible(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_show(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_hide(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_visible(QWidget* widget, bool visible);
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_close(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_show_minimized(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_show_maximized(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_show_full_screen(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_show_normal(QWidget* widget);

  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_minimized(QWidget* widget);
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_maximized(QWidget* widget);
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_full_screen(QWidget* widget);
  OBSHARP_API Qt::WindowState OBSHARP_CALL obsharp_widget_get_window_state(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_window_state(QWidget* widget, Qt::WindowState state);

  // ===== 几何 =====
  OBSHARP_API int  OBSHARP_CALL obsharp_widget_get_x(QWidget* widget);
  OBSHARP_API int  OBSHARP_CALL obsharp_widget_get_y(QWidget* widget);
  OBSHARP_API int  OBSHARP_CALL obsharp_widget_get_width(QWidget* widget);
  OBSHARP_API int  OBSHARP_CALL obsharp_widget_get_height(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_get_geometry(QWidget* widget, int* x, int* y, int* w, int* h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_get_frame_geometry(QWidget* widget, int* x, int* y, int* w, int* h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_move(QWidget* widget, int x, int y);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_resize(QWidget* widget, int w, int h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_geometry(QWidget* widget, int x, int y, int w, int h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_fixed_size(QWidget* widget, int w, int h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_minimum_size(QWidget* widget, int w, int h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_maximum_size(QWidget* widget, int w, int h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_get_minimum_size(QWidget* widget, int* w, int* h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_get_maximum_size(QWidget* widget, int* w, int* h);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_adjust_size(QWidget* widget);

  // ===== 焦点 / 激活 =====
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_is_active_window(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_activate_window(QWidget* widget);
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_has_focus(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_focus(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_clear_focus(QWidget* widget);
  OBSHARP_API Qt::FocusPolicy OBSHARP_CALL obsharp_widget_get_focus_policy(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_focus_policy(QWidget* widget, Qt::FocusPolicy policy);

  // ===== Z 序 =====
  OBSHARP_API void OBSHARP_CALL obsharp_widget_raise(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_lower(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_stack_under(QWidget* widget, QWidget* other);

  // ===== 父子关系 =====
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_widget_get_parent(QWidget* widget);
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_widget_get_window(QWidget* widget);
  // setParent(parent)；parent 为 NULL 时脱离父级。注意：setParent 会隐藏 widget。
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_parent(QWidget* widget, QWidget* parent);

  // ===== 窗口标志 =====
  OBSHARP_API Qt::WindowType OBSHARP_CALL obsharp_widget_get_window_flags(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_window_flags(QWidget* widget, Qt::WindowType flags);
  OBSHARP_API Qt::WindowType  OBSHARP_CALL obsharp_widget_get_window_type(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_window_flag(QWidget* widget, Qt::WindowType type, bool on);

  // ===== 工具提示 / 样式表 =====
  OBSHARP_API const char* OBSHARP_CALL obsharp_widget_get_tool_tip(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_tool_tip(QWidget* widget, const char* tip);
  OBSHARP_API const char* OBSHARP_CALL obsharp_widget_get_style_sheet(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_style_sheet(QWidget* widget, const char* sheet);

  // ===== 控件属性 =====
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_attribute(QWidget* widget, Qt::WidgetAttribute attribute, bool on);
  OBSHARP_API bool OBSHARP_CALL obsharp_widget_test_attribute(QWidget* widget, Qt::WidgetAttribute attribute);

  // ===== 尺寸策略 =====
  OBSHARP_API QSizePolicy::Policy OBSHARP_CALL obsharp_widget_get_size_policy_horizontal(QWidget* widget);
  OBSHARP_API QSizePolicy::Policy OBSHARP_CALL obsharp_widget_get_size_policy_vertical(QWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_size_policy(QWidget* widget, QSizePolicy::Policy horizontal, QSizePolicy::Policy vertical);

  // ===== 重绘 =====
  // update()：调度一次重绘。CEF 无窗口渲染停了之后重新显示时可以「踢一下」。
  OBSHARP_API void OBSHARP_CALL obsharp_widget_update(QWidget* widget);

  // ===== 内容布局 =====
  /*
   * 把一个 widget 摆进容器：容器上没有布局就建一个边距 / 间距全 0 的 QVBoxLayout，
   * 然后把旧内容摘掉、把 child 加进去。child 为 NULL 时只清空。
   *
   * 本库**不拥有** child，也不会在容器销毁时删它：旧内容只是从布局里拿出来、隐藏、
   * 断开父级，生命归调用方管（可能是浏览器 widget，也可能下一秒被塞进另一个容器）。
   */
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_content(QWidget* container, QWidget* child);

  // ===== 背景 =====
  // Qt::WA_TranslucentBackground + setAutoFillBackground(false)。
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_translucent_background(QWidget* widget);
  // 用 0xAARRGGBB 铺底色（palette + setAutoFillBackground(true)）。
  OBSHARP_API void OBSHARP_CALL obsharp_widget_set_background_color(QWidget* widget, unsigned int argb);

  // ===== 查找 =====
  // 按 OS 窗口 id 找已存在的 QWidget（Windows 上是 HWND → QWidget::find）。找不到返回 NULL。
  OBSHARP_API QWidget* OBSHARP_CALL obsharp_widget_find(uintptr_t native_id);

  // ===== 事件过滤 =====
  /*
   * 事件过滤回调。在 Qt 主线程上被调用，watched 是被监视对象，event_type 是
   * QEvent::Type 的数值。返回 true 表示**吃掉**这个事件，false 表示继续正常派发。
   *
   * 「哪些事件要拦、要不要顺便踢一下重绘」全是调用方的判断，本库不做。
   */
  typedef bool(OBSHARP_CALL* obsharp_widget_filter_event_callback)(QObject* watched, int event_type, void* user);

  /*
   * 装一个事件过滤器，返回它的句柄（借用）用于 obsharp_widget_remove_event_filter。
   * callback 为 NULL 时返回 NULL。
   *
   * 一个 widget 可以装多个过滤器，各自独立，同一个回调也可以装在多个 widget 上。
   */
  OBSHARP_API QObject* OBSHARP_CALL obsharp_widget_install_event_filter(QWidget* widget,
    obsharp_widget_filter_event_callback callback, void* user);
  OBSHARP_API void OBSHARP_CALL obsharp_widget_remove_event_filter(QWidget* widget, QObject* filter);
}