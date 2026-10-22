#include "qcef.h"

#include "logger.h"
#include "QT/qt.h"
#include "widgetsignals.h"

#include <QtCore/QVariant>

namespace OBSharp::Cef
{
  // ---------------------------------------------------------------------------
  // WidgetSignals：QCefWidget 信号 → 托管函数指针的零判断垫片
  // ---------------------------------------------------------------------------

  WidgetSignals::WidgetSignals(QCefWidget* widget) : QObject(widget), widget_(widget) {}

  void WidgetSignals::SetCallbacks(obsharp_cef_widget_value_callback on_title_changed,
    obsharp_cef_widget_value_callback on_url_changed, void* user) {
    on_title_changed_ = on_title_changed;
    on_url_changed_ = on_url_changed;
    user_ = user;
  }

  void WidgetSignals::DetachCallbacks() {
    on_title_changed_ = nullptr;
    on_url_changed_ = nullptr;
    user_ = nullptr;
  }

  void WidgetSignals::OnTitleChanged(const QString& title) {
    if (!on_title_changed_)
      return;

    // BorrowedUtf8 用一块 thread_local 缓冲，只在本次回调内有效 —— 正好符合公开头
    // 「value 是借用指针，回调返回后失效」的约定，也省掉一次堆分配。不要在这里再取
    // 第二个借用字符串（会把这个冲掉）。
    on_title_changed_(widget_, BorrowedUtf8(title), user_);
  }

  void WidgetSignals::OnUrlChanged(const QString& url) {
    if (!on_url_changed_)
      return;

    on_url_changed_(widget_, BorrowedUtf8(url), user_);
  }

  // ---------------------------------------------------------------------------
  // QCef：一行式转发
  // ---------------------------------------------------------------------------
  //
  // 全部要求 Qt 主线程（托管侧自己会先 obsharp_ui_invoke 切过去）。这里不做空指针
  // 判断：句柄有效性是调用方的事。

  bool obsharp_qcef_init_browser(QCef* cef) {
    return cef->init_browser();
  }

  bool obsharp_qcef_is_initialized(QCef* cef) {
    return cef->initialized();
  }

  bool obsharp_qcef_wait_for_init(QCef* cef) {
    return cef->wait_for_browser_init();
  }

  QCefCookieManager* obsharp_qcef_create_cookie_manager(QCef* cef, const char* storage_path,
    bool persist_session_cookies) {
    return cef->create_cookie_manager(storage_path ? storage_path : "", persist_session_cookies);
  }

  void obsharp_qcef_add_popup_whitelist_url(QCef* cef, const char* url, QObject* obj) {
    cef->add_popup_whitelist_url(url ? url : "", obj);
  }

  void obsharp_qcef_add_force_popup_url(QCef* cef, const char* url, QObject* obj) {
    cef->add_force_popup_url(url ? url : "", obj);
  }

  // create_widget 返回的是**借用**指针：widget 归调用方释放，本库不插手。
  //
  // 回调在这里一次性接管：
  //   * 先建 widget —— 没有 widget 就没有对象可挂；
  //   * 再建垫片（parent 是 widget，所以生命周期跟它完全一致，调用方不需要也无法
  //     显式释放）；
  //   * 按信号名 connect（不能用成员函数指针，原因见下面的注释）；
  //   * 两个回调都是 NULL 时干脆不建垫片：没有回调要转，建了也是空转。
  //
  // 之所以用动态属性把垫片指针存回 widget：widget 可能在任意线程、任意时刻销毁，
  // 挂在它下面的子对象和动态属性都随它一起走，不需要谁去清理，也不会漏。全局 map
  // 要为每次查找加锁、销毁时还得 hook 析构 —— 平白多一堆状态和竞态。
  //
  // 存成 qulonglong（指针宽度）而不是 QObject*：QVariant 里塞裸指针要注册元类型，
  // 存整数在任何 Qt 版本上都能原样取回。
  QCefWidget* obsharp_qcef_create_widget(QCef* cef, QWidget* parent, const char* url,
    QCefCookieManager* cookie_manager, obsharp_cef_widget_value_callback on_title_changed,
    obsharp_cef_widget_value_callback on_url_changed, void* user) {
    QCefWidget* widget = cef->create_widget(parent, url ? url : "", cookie_manager);

    if (!widget || (!on_title_changed && !on_url_changed))
      return widget;

    const char* const kProperty = "obsharp_cef_widget_signals";

    auto* bridge = new WidgetSignals(widget);
    bridge->SetCallbacks(on_title_changed, on_url_changed, user);

    // SIGNAL / SLOT 字符串形式，不用成员函数指针：PMF connect 会引用 QCefWidget 的
    // staticMetaObject 和信号 thunk，而那份 moc 产出在 obs-browser 的 DLL 里，我们
    // 不链它（链接期直接 LNK2019）。字符串形式只在运行时按名字查 metaobject，不需
    // 要任何符号 —— 代价是连错了只有运行时才知道。
    //
    // 这两个信号是 obs-browser 的面板接口 promise 过的（BROWSER_PANEL_VERSION 3
    // 只暴露这两个）。
    QObject::connect(widget, SIGNAL(titleChanged(QString)), bridge, SLOT(OnTitleChanged(QString)));
    QObject::connect(widget, SIGNAL(urlChanged(QString)), bridge, SLOT(OnUrlChanged(QString)));

    widget->setProperty(kProperty, QVariant::fromValue<qulonglong>(reinterpret_cast<qulonglong>(bridge)));

    return widget;
  }

  // ---------------------------------------------------------------------------
  // QCefWidget：一行式转发
  // ---------------------------------------------------------------------------

  void obsharp_cef_widget_set_url(QCefWidget* widget, const char* url) {
    widget->setURL(url ? url : "");
  }

  void obsharp_cef_widget_set_startup_script(QCefWidget* widget, const char* script) {
    widget->setStartupScript(script ? script : "");
  }

  void obsharp_cef_widget_allow_all_popups(QCefWidget* widget, bool allow) {
    widget->allowAllPopups(allow);
  }

  void obsharp_cef_widget_close_browser(QCefWidget* widget) {
    widget->closeBrowser();
  }

  void obsharp_cef_widget_reload(QCefWidget* widget) {
    widget->reloadPage();
  }

  // direction < 0 缩小、> 0 放大、== 0 复位。方向值的含义由 QCefWidget 决定，本库
  // 原样透传（不解释，也不夹在合法区间里）。
  bool obsharp_cef_widget_zoom(QCefWidget* widget, int direction) {
    return widget->zoomPage(direction);
  }

  void obsharp_cef_widget_execute_script(QCefWidget* widget, const char* script) {
    widget->executeJavaScript(script ? script : "");
  }

  // 断开创建时传入的 title / url 回调。
  //
  // 只清回调表，不 disconnect、不删垫片：垫片是 widget 的子对象，会跟 widget 一起
  // 走，而 disconnect 之后再连回来没有意义（重新订阅本来就只能在 create_widget 时做）。
  void obsharp_cef_widget_detach_callbacks(QCefWidget* widget) {
    if (!widget)
      return;

    const QVariant stored = widget->property("obsharp_cef_widget_signals");
    if (!stored.isValid())
      return; // 创建时没传回调，没有垫片可清

    auto* bridge = reinterpret_cast<WidgetSignals*>(stored.value<qulonglong>());
    if (bridge)
      bridge->DetachCallbacks();
  }
}
