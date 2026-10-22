#pragma once

// qcef.h：obs-browser 面板接口（QCef / QCefWidget）的一行式转发。
//
// 类型直接引用 obs-browser 的 panel/browser-panel.hpp：QCef / QCefWidget /
// QCefCookieManager 都是原版声明，虚表、信号都是真的，不存在「自己抄一份声明
// 对不对得上」的问题。
//
// cef 是 obs-browser 的 QCef*（托管侧调 obs_browser_create_qcef 拿到），
// widget 是它 create_widget 出来的 QCefWidget*。这些函数只去掉 C++ 虚函数调用
// 的语法门槛，不含任何判断。全部要求 Qt 主线程。
//
// QCefWidget 本身就是 QWidget：它的窗口 / 几何 / 显示那一半由 QT/widget.h 的
// obsharp_widget_* 覆盖，这里只补 CEF 追加的那 7 个虚函数和 title / url 信号。

#include "obsharp.h"

#include <browser-panel.hpp>

namespace OBSharp::Cef
{
  // ===== 回调 =====

  // title / url 变化的通知。value 是借用指针，回调返回后失效；widget 是发出信号
  // 的那个 QCefWidget（同一个回调可以装在多个 widget 上，靠它区分）。
  typedef void(OBSHARP_CALL* obsharp_cef_widget_value_callback)(QCefWidget* widget, const char* value,
    void* user);

  // ===== QCef 面板接口 =====

  OBSHARP_API bool OBSHARP_CALL obsharp_qcef_init_browser(QCef* cef);
  OBSHARP_API bool OBSHARP_CALL obsharp_qcef_is_initialized(QCef* cef);
  OBSHARP_API bool OBSHARP_CALL obsharp_qcef_wait_for_init(QCef* cef);

  // 建 cookie 管理器（可一路传给 create_widget）。转发面只到「建出来」为止：它的
  // 操作方法没有一一转出（CheckForCookie 的 std::function 过不了 C ABI），需要时
  // 托管侧自己走 obs-browser 的入口。
  OBSHARP_API QCefCookieManager* OBSHARP_CALL obsharp_qcef_create_cookie_manager(QCef* cef,
    const char* storage_path, bool persist_session_cookies);

  OBSHARP_API void OBSHARP_CALL obsharp_qcef_add_popup_whitelist_url(QCef* cef, const char* url, QObject* obj);
  OBSHARP_API void OBSHARP_CALL obsharp_qcef_add_force_popup_url(QCef* cef, const char* url, QObject* obj);

  // ===== QCefWidget 创建 =====

  /*
   * 建一个 QCefWidget。用法语义与 QCef::create_widget 完全一致，返回的是借用指针
   * （widget 归调用方释放）。cookie_manager 可以为 NULL。
   *
   * url 由调用方拼好（含 host→domain 映射），本库不做任何改写。
   *
   * title / url 回调在**这里**一次性接管（本库不提供事后再改回调的接口）；两个
   * 都可以为 NULL，user 原样透传。创建时没传回调就根本没有垫片对象，之后也不用
   * detach。拆除顺序见 obsharp_cef_widget_detach_callbacks。
   */
  OBSHARP_API QCefWidget* OBSHARP_CALL obsharp_qcef_create_widget(QCef* cef, QWidget* parent, const char* url,
    QCefCookieManager* cookie_manager, obsharp_cef_widget_value_callback on_title_changed,
    obsharp_cef_widget_value_callback on_url_changed, void* user);

  // ===== QCefWidget 操作 =====

  OBSHARP_API void OBSHARP_CALL obsharp_cef_widget_set_url(QCefWidget* widget, const char* url);
  OBSHARP_API void OBSHARP_CALL obsharp_cef_widget_set_startup_script(QCefWidget* widget, const char* script);
  OBSHARP_API void OBSHARP_CALL obsharp_cef_widget_allow_all_popups(QCefWidget* widget, bool allow);
  OBSHARP_API void OBSHARP_CALL obsharp_cef_widget_close_browser(QCefWidget* widget);
  OBSHARP_API void OBSHARP_CALL obsharp_cef_widget_reload(QCefWidget* widget);
  // direction < 0 缩小，> 0 放大，== 0 复位。返回 true 表示成功。
  OBSHARP_API bool OBSHARP_CALL obsharp_cef_widget_zoom(QCefWidget* widget, int direction);
  OBSHARP_API void OBSHARP_CALL obsharp_cef_widget_execute_script(QCefWidget* widget, const char* script);

  /*
   * 断开创建时传入的 title / url 回调（清空回调表，不销毁 widget、不重连）。
   *
   * 这不是「事后改订阅」——重新订阅仍然只能在 create_widget 时做。它存在只是因为
   * 拆除顺序有要求：托管侧释放回调上下文（GCHandle）之前必须保证不会再有回调打进
   * 来，而 widget 可能已经被 OBS 的停靠面板收养、比托管对象活得久。没有它就只能
   * 靠 widget 销毁来兜底，收养那条路径会在上下文释放之后收到信号（野指针）。
   */
  OBSHARP_API void OBSHARP_CALL obsharp_cef_widget_detach_callbacks(QCefWidget* widget);
}
