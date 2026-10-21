#include "Export.h"

#include "QtSupport.h"

#include <QtCore/QEvent>
#include <QtGui/QColor>
#include <QtGui/QPalette>
#include <QtWidgets/QBoxLayout>
#include <QtWidgets/QWidget>

#include <cstdint>

// Widget.cpp：QWidget 通用操作的一行式转发。
//
// 这个文件里**没有一个业务判断**：每个函数要么是 widget->xxx(...)，要么是
// 「空指针就直接返回」。哪些事件要拦、窗口该不该置顶、内容 Widget 归谁销毁，
// 全是 C# 的决定；这里只提供一个能被 P/Invoke 调到的语法入口。

namespace {

  // 把 C ABI 的 void* 句柄还原成 QWidget*。为 NULL 时返回 nullptr，调用方据此短路。
  //
  // 不校验「这个指针到底是不是 QWidget」：它由 C# 持有并负责生命周期，本库没有
  // 任何办法验证（起点是 obs-browser 的 QCefWidget，我们只见过它的 QWidget 部分）。
  QWidget* WidgetOrNull(void* widget) {
    return static_cast<QWidget*>(widget);
  }

  // 事件过滤器的零判断适配器。
  //
  // 它唯一的工作是把「事件类型」交给 C# 并把它返回的那一个整数原样转换成 bool：
  // 非 0 = 吃掉这个事件（eventFilter 返回 true 即阻止继续派发），0 = 交给
  // QObject::eventFilter 继续正常派发。
  //
  // 本类不判断事件类型、不给任何事件开绿灯、也不记录状态 —— 比如「OBS 关停靠面板
  // 时会发 QEvent::User + QEvent::Close，必须拦下来，否则 CEF 那块就废了」这条知识
  // 现在住在 C# 里（WebView 的过滤器回调），不在这里。
  class EventFilterAdapter : public QObject {
  public:
    EventFilterAdapter(QWidget* parent, obsharp_filter_event_fn fn, void* user)
      : QObject(parent), fn_(fn), user_(user) {
    }

  protected:
    bool eventFilter(QObject* watched, QEvent* event) override {
      if (!fn_)
        return QObject::eventFilter(watched, event);

      // 只传事件类型的数值和「哪个 widget」：C# 那边要按 widget 区分（同一个
      // 回调可能装在多个 widget 上），但不需要 QEvent 的其他内容。
      return fn_(user_, watched, static_cast<int32_t>(event->type()));
    }

  private:
    obsharp_filter_event_fn fn_ = nullptr;
    void* user_ = nullptr;
  };

  // 容器上取/建布局。
  //
  // 只在「容器还没有布局」时建一个：已经有布局就沿用（比如 C# 之前已经
  // set_content 过一次）。边距和间距都归零是硬要求 —— 浏览器要铺满容器，
  // 默认的 9 像素边距会在四周留一圈背景色。
  QBoxLayout* EnsureLayout(QWidget* container) {
    if (auto* existing = qobject_cast<QBoxLayout*>(container->layout()))
      return existing;

    if (container->layout())
      return nullptr; // 已有别的类型的布局：不替调用方拆掉，交给它自己处理。

    auto* layout = new QVBoxLayout(container);
    layout->setContentsMargins(0, 0, 0, 0);
    layout->setSpacing(0);
    return layout;
  }

} // namespace

extern "C" {

  void OBSHARP_NATIVE_CALL obsharp_widget_resize(void* widget, int32_t width, int32_t height) {
    if (QWidget* target = WidgetOrNull(widget))
      target->resize(width, height);
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_move(void* widget, int32_t x, int32_t y) {
    if (QWidget* target = WidgetOrNull(widget))
      target->move(x, y);
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_size(void* widget, int32_t* width, int32_t* height) {
    // 两个出参都允许为 NULL：C# 有时只关心宽度（或者只拿它当「这个 widget 活了没」
    // 的探针），没必要逼它传一个用不上的指针过来。
    QWidget* target = WidgetOrNull(widget);
    const QSize size = target ? target->size() : QSize();

    if (width)
      *width = size.width();
    if (height)
      *height = size.height();
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_set_visible(void* widget, bool visible) {
    if (QWidget* target = WidgetOrNull(widget))
      target->setVisible(visible);
  }

  bool OBSHARP_NATIVE_CALL obsharp_widget_is_visible(void* widget) {
    QWidget* target = WidgetOrNull(widget);
    return target && target->isVisible();
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_show(void* widget) {
    if (QWidget* target = WidgetOrNull(widget)) {
      target->show();
      // raise() 是「显示」语义的一部分：C# 建工具窗口时用同一个函数，靠它把
      // 已有窗口提到前台。分成两次调用没有意义，所以不单独导出 raise。
      target->raise();
    }
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_hide(void* widget) {
    if (QWidget* target = WidgetOrNull(widget))
      target->hide();
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_update(void* widget) {
    if (QWidget* target = WidgetOrNull(widget))
      target->update();
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_delete_later(void* widget) {
    // deleteLater 而不是 delete：这个函数可能从信号处理过程中被调到（比如窗口
    // 关闭回调里 C# 决定销毁），直接 delete 会把正在派发信号的那个对象从脚下抽走。
    if (QWidget* target = WidgetOrNull(widget))
      target->deleteLater();
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_set_parent(void* widget, void* parent) {
    if (QWidget* target = WidgetOrNull(widget))
      target->setParent(WidgetOrNull(parent));
  }

  void* OBSHARP_NATIVE_CALL obsharp_widget_parent(void* widget) {
    QWidget* target = WidgetOrNull(widget);
    return target ? target->parentWidget() : nullptr;
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_set_window_title(void* widget, const char* title) {
    if (QWidget* target = WidgetOrNull(widget))
      target->setWindowTitle(obsharp::ToQt(title));
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_set_topmost(void* widget, bool topmost) {
    if (QWidget* target = WidgetOrNull(widget))
      target->setWindowFlag(Qt::WindowStaysOnTopHint, topmost);
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_set_fixed_size(void* widget, int32_t width, int32_t height) {
    if (QWidget* target = WidgetOrNull(widget))
      target->setFixedSize(width, height);
  }

  int32_t OBSHARP_NATIVE_CALL obsharp_widget_set_translucent_background(void* widget) {
    QWidget* target = WidgetOrNull(widget);
    if (!target)
      return OBSHARP_ERR_INVALID_ARGUMENT;

    // WA_TranslucentBackground 必须关掉 autoFillBackground：开着的话 Qt 会先把
    // 窗口背景刷成 palette 的 Window 色，透明就没了。
    target->setAttribute(Qt::WA_TranslucentBackground, true);
    target->setAutoFillBackground(false);
    return OBSHARP_OK;
  }

  int32_t OBSHARP_NATIVE_CALL obsharp_widget_set_background_color(void* widget, uint32_t argb) {
    QWidget* target = WidgetOrNull(widget);
    if (!target)
      return OBSHARP_ERR_INVALID_ARGUMENT;

    // 尽力而为：给 Widget 铺个底色，页面自己画的内容会盖在上面。CEF 用无窗口
    // 渲染，真正的背景是页面自己画的，这里只是「还没出第一帧时不露黑底」（或者
    // 页面本身是透明的时给个衬底）。
    const QColor color = QColor::fromRgba(argb);
    QPalette palette = target->palette();
    palette.setColor(QPalette::Window, color);
    target->setPalette(palette);
    target->setAutoFillBackground(true);
    return OBSHARP_OK;
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_set_content(void* container, void* child) {
    QWidget* host = WidgetOrNull(container);
    if (!host)
      return;

    QBoxLayout* layout = EnsureLayout(host);
    if (!layout)
      return; // 有个不是 QBoxLayout 的布局：不替调用方拆，保持原样。

    // 摘掉旧内容。注意**不销毁**它：内容的生命在 C# 手上（可能是 browser widget，
    // 也可能下一秒被塞进另一个容器）。只把它从布局里拿出来、隐藏、断开父级，
    // 这样它不会再跟着这个容器一起被 Qt 销毁。
    if (QWidget* previous = layout->itemAt(0) ? layout->itemAt(0)->widget() : nullptr) {
      layout->removeWidget(previous);
      previous->hide();
      previous->setParent(nullptr);
    }

    if (QWidget* content = WidgetOrNull(child))
      layout->addWidget(content);
  }

  void* OBSHARP_NATIVE_CALL obsharp_widget_find(uintptr_t native_id) {
    // QWidget::find 按 WId（Windows 上是 HWND）找**已经存在**的 widget，找不到返回
    // nullptr。这里不做「先当 HWND 找、找不到再当 QWidget* 用」的判断 —— 那是 C#
    // 在解释 options 里的 parent 字段，属于调用方的策略。
    if (native_id == 0)
      return nullptr;

    return QWidget::find(static_cast<WId>(native_id));
  }

  void* OBSHARP_NATIVE_CALL
    obsharp_widget_install_event_filter(void* widget, obsharp_filter_event_fn fn, void* user) {
    QWidget* target = WidgetOrNull(widget);
    if (!target || !fn)
      return nullptr;

    // 一个 widget 可以装多个过滤器，每个都是独立对象（各自有独立的 C# 回调与
    // user）。父级设成 widget：widget 被销毁时它们跟着走，不会留下野指针。
    auto* adapter = new EventFilterAdapter(target, fn, user);
    target->installEventFilter(adapter);
    return adapter;
  }

  void OBSHARP_NATIVE_CALL obsharp_widget_remove_event_filter(void* widget, void* filter) {
    QWidget* target = WidgetOrNull(widget);
    auto* adapter = static_cast<QObject*>(filter);
    if (!target || !adapter)
      return;

    target->removeEventFilter(adapter);
    // 是我们 new 出来的（见 install），所以由我们归还。用 deleteLater 而不是
    // delete：可能正处在事件派发过程中，立刻删掉会把栈上的调用者抽空。
    adapter->deleteLater();
  }

} // extern "C"
