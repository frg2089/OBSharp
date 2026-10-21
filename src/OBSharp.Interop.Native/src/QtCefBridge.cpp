// QtCefBridge.cpp：obs-browser 的 QCef / QCefWidget 虚接口 ↔ C ABI 的唯一桥。
//
// 这个文件里除 create_widget 之外**全是一行式转发**。之所以要专门存在：
//   * C# 不能调用 C++ 虚函数（它只知道一个 void*）；
//   * QCef 的真实头文件是 GPL-2.0，本库不能 include（见 QtCefBridge.h）。
//
// 于是这里做两件事：
//   1) 定义 QCef 的析构函数 —— 头里声明成 virtual ~QCef(); 但没有实现，不定义的话
//      链接期会缺符号（哪怕我们从不 delete 它，虚表的类外析构条目仍然要有人提供）；
//   2) 把「this + 第 N 个槽位」这件事封进 AsCef / AsCefWidget 里，调用点看起来就
//      是普通的成员函数调用。
//
// 唯一的例外是 create_widget：Qt 的信号只能连到 QObject 的槽上，而 C ABI 给的是
// 裸函数指针，所以需要一个真正的 QObject 垫片。回调在**创建 widget 的那一刻**一次性
// 传入 —— 本库不提供事后再改的 setter。CefWidgetSignals 的实现也放在本文件
// （项目里没有独立的 CefWidgetSignals.cpp，CMakeLists 里也没有）。
//
// 槽位顺序的硬约束写在 QtCefBridge.h 的文件头，改动前务必读一遍。

#include "Export.h"

#include "CefWidgetSignals.h"
#include "QtCefBridge.h"
#include "QtSupport.h"

#include <QtCore/QVariant>

namespace obsharp {

// QCef 的类外析构：空实现，本库不拥有 QCef（它归 obs-browser，由 C# 决定何时不
// 再用）。定义它只是为了补齐虚表引用。
QCef::~QCef() = default;

// ---------------------------------------------------------------------------
// CefWidgetSignals：QCefWidget 信号 → C# 函数指针的零判断垫片
// ---------------------------------------------------------------------------

CefWidgetSignals::CefWidgetSignals(QObject* parent) : QObject(parent) {}

void CefWidgetSignals::SetCallbacks(const obsharp_cef_widget_callbacks& callbacks) {
  // 按值收、按值存：调用它的那次 P/Invoke 返回后，C# 传进来的结构体随时可能被回收，
  // 所以必须当场拷一份。
  callbacks_ = callbacks;
}

void CefWidgetSignals::OnTitleChanged(const QString& title) {
  if (!callbacks_.on_title_changed)
    return;

  // BorrowedUtf8 用一块 thread_local 缓冲，只在本次回调内有效 —— 正好符合公开头
  // 「value 是借用指针，回调返回后失效」的约定，也省掉一次堆分配。不要在这里再取
  // 第二个借用字符串（会把这个冲掉）。
  callbacks_.on_title_changed(callbacks_.user, BorrowedUtf8(title));
}

void CefWidgetSignals::OnUrlChanged(const QString& url) {
  if (!callbacks_.on_url_changed)
    return;

  callbacks_.on_url_changed(callbacks_.user, BorrowedUtf8(url));
}

} // namespace obsharp

extern "C" {

// ---- QCef：一行式转发 ------------------------------------------------------
//
// 全部要求 Qt 主线程（托管侧自己会先 obsharp_invoke_ui 切过去）。这里不做空指针
// 判断：句柄有效性是 C# 的事，本库连「要不要拦住一个坏句柄」这种判断都不做 ——
// 公开头写明了这些函数「不含任何判断」。

OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_qcef_init_browser(void* cef) {
  return obsharp::AsCef(cef)->init_browser();
}

OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_qcef_is_initialized(void* cef) {
  return obsharp::AsCef(cef)->initialized();
}

OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_qcef_wait_for_init(void* cef) {
  return obsharp::AsCef(cef)->wait_for_browser_init();
}

// create_widget 返回的是**借用**指针：widget 归调用方（C#）释放，本库不插手。
// url 为 NULL 时按空串处理（QCef 内部会当成 "about:blank" 之类的默认页）；
// cookie_manager 可以为 NULL，那是最常见的用法。
//
// 回调在这里一次性接管：
//   * 先建 widget —— 没有 widget 就没有对象可挂；
//   * 再建垫片（parent 是 widget，所以生命周期跟它完全一致，C# 不需要也无法显式释放）；
//   * 按信号名 connect（拿不到 QCefWidget 的头文件，只能用字符串形式）；
//   * callbacks 为 NULL 时干脆不建垫片：没有回调要转，建了也是空转。
//
// 之所以用动态属性把垫片指针存回 widget：widget 可能在任意线程、任意时刻销毁，挂在
// 它下面的子对象和动态属性都随它一起走，不需要谁去清理，也不会漏。全局 map 要为每次
// 查找加锁、销毁时还得 hook 析构 —— 平白多一堆状态和竞态。
//
// 存成 qulonglong（指针宽度）而不是 QObject*：QVariant 里塞裸指针要注册元类型，
// 存整数在任何 Qt 版本上都能原样取回。
OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_qcef_create_widget(void* cef, void* parent, const char* url,
  void* cookie_manager, const obsharp_cef_widget_callbacks* callbacks) {
  QWidget* widget = obsharp::AsCef(cef)->create_widget(obsharp::AsWidget(parent), url ? url : "",
    static_cast<QCefCookieManager*>(cookie_manager));

  if (!widget || !callbacks)
    return widget;

  const char* const kProperty = "obsharp_cef_widget_signals";

  // 局部变量**不能**叫 signals：Qt 在非 QT_NO_KEYWORDS 模式下把 signals 宏定义成
  // public（见 qobjectdefs.h），那样这里会被展开成语法垃圾。
  auto* bridge = new obsharp::CefWidgetSignals(widget);
  bridge->SetCallbacks(*callbacks);

  // 字符串形式的 connect：拿不到 QCefWidget 的头文件，只能按信号名连。这两个信号
  // 是 obs-browser 的面板接口 promise 过的（BROWSER_PANEL_VERSION 3 只暴露这两个）。
  QObject::connect(widget, SIGNAL(titleChanged(QString)), bridge, SLOT(OnTitleChanged(QString)));
  QObject::connect(widget, SIGNAL(urlChanged(QString)), bridge, SLOT(OnUrlChanged(QString)));

  widget->setProperty(kProperty, QVariant::fromValue<qulonglong>(reinterpret_cast<qulonglong>(bridge)));

  return widget;
}

// ---- QCefWidget：一行式转发 ------------------------------------------------

OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_set_url(void* widget, const char* url) {
  obsharp::AsCefWidget(widget)->setURL(url ? url : "");
}

OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_set_startup_script(void* widget, const char* script) {
  obsharp::AsCefWidget(widget)->setStartupScript(script ? script : "");
}

OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_allow_all_popups(void* widget, bool allow) {
  obsharp::AsCefWidget(widget)->allowAllPopups(allow);
}

OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_reload(void* widget) {
  obsharp::AsCefWidget(widget)->reloadPage();
}

// direction < 0 缩小、> 0 放大、== 0 复位。方向值的含义由 QCefWidget 决定，本库
// 原样透传（不解释，也不夹在合法区间里）。
OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_cef_widget_zoom(void* widget, int32_t direction) {
  return obsharp::AsCefWidget(widget)->zoomPage(direction);
}

OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_execute_script(void* widget, const char* script) {
  obsharp::AsCefWidget(widget)->executeJavaScript(script ? script : "");
}

OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_close_browser(void* widget) {
  obsharp::AsCefWidget(widget)->closeBrowser();
}

// 断开创建时传入的 title / url 回调。
//
// 只清回调表，不 disconnect、不删垫片：垫片是 widget 的子对象，会跟 widget 一起走，
// 而 disconnect 之后再连回来没有意义（重新订阅本来就只能在 create_widget 时做）。
void OBSHARP_NATIVE_CALL obsharp_cef_widget_detach_callbacks(void* widget) {
  if (!widget)
    return;

  QWidget* qwidget = obsharp::AsWidget(widget);

  const QVariant stored = qwidget->property("obsharp_cef_widget_signals");
  if (!stored.isValid())
    return; // 创建时没传回调，没有垫片可清

  auto* bridge = reinterpret_cast<obsharp::CefWidgetSignals*>(stored.value<qulonglong>());
  if (bridge)
    bridge->DetachCallbacks();
}

} // extern "C"
