#include "widget.h"

#include "qt.h"

#include <QtCore/QEvent>
#include <QtGui/QColor>
#include <QtGui/QPalette>
#include <QtWidgets/QApplication>
#include <QtWidgets/QBoxLayout>

namespace OBSharp::Widget
{
  namespace
  {
    // 事件过滤器的零判断适配器。
    //
    // 唯一的工作是把「事件类型」交给托管侧，并把回调返回的 bool 原样转成
    // eventFilter 的返回值：true = 吃掉这个事件，false = 交给
    // QObject::eventFilter 继续正常派发。
    //
    // 本类不判断事件类型、不给任何事件开绿灯、也不记录状态：「哪些事件要拦」
    // 是调用方的判断。覆写 eventFilter 而不是连信号，所以不需要 moc。
    class EventFilterAdapter : public QObject
    {
    public:
      EventFilterAdapter(QWidget* parent, obsharp_widget_filter_event_callback callback, void* user)
        : QObject(parent), callback_(callback), user_(user) {
      }

    protected:
      bool eventFilter(QObject* watched, QEvent* event) override {
        if (!callback_)
          return QObject::eventFilter(watched, event);

        // 只传事件类型的数值和「哪个对象」：同一个回调可能装在多个 widget 上，
        // 托管侧要按 watched 区分，但不需要 QEvent 的其他内容。
        return callback_(watched, static_cast<int>(event->type()), user_);
      }

    private:
      obsharp_widget_filter_event_callback callback_ = nullptr;
      void* user_ = nullptr;
    };

    // 容器上取 / 建布局。
    //
    // 只在「容器还没有布局」时建一个：已经有布局就沿用（比如托管侧之前已经
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

  // ===== 生命周期 =====
  QWidget* obsharp_widget_create(QWidget* parent) {
    return new QWidget(parent);
  }

  void obsharp_widget_destroy(QWidget* widget) {
    if (!widget) return;
    QObject::disconnect(widget, nullptr, nullptr, nullptr);
    widget->deleteLater();
  }

  // ===== 窗口识别 =====
  bool obsharp_widget_is_window(QWidget* widget) {
    return widget->isWindow();
  }

  bool obsharp_widget_is_modal(QWidget* widget) {
    return widget->isModal();
  }

  Qt::WindowModality obsharp_widget_get_window_modality(QWidget* widget) {
    return widget->windowModality();
  }

  void obsharp_widget_set_window_modality(QWidget* widget, Qt::WindowModality modality) {
    widget->setWindowModality(modality);
  }

  // ===== 标题 / 透明度 =====
  const char* obsharp_widget_get_window_title(QWidget* widget) {
    return BorrowedUtf8(widget->windowTitle());
  }

  void obsharp_widget_set_window_title(QWidget* widget, const char* title) {
    widget->setWindowTitle(ToQt(title));
  }

  double obsharp_widget_get_window_opacity(QWidget* widget) {
    return widget->windowOpacity();
  }

  void obsharp_widget_set_window_opacity(QWidget* widget, double opacity) {
    widget->setWindowOpacity(opacity);
  }

  // ===== 启用 =====
  bool obsharp_widget_is_enabled(QWidget* widget) {
    return widget->isEnabled();
  }

  void obsharp_widget_set_enabled(QWidget* widget, bool enabled) {
    widget->setEnabled(enabled);
  }

  // ===== 可见性 / 显示状态 =====
  bool obsharp_widget_is_visible(QWidget* widget) {
    return widget->isVisible();
  }

  void obsharp_widget_show(QWidget* widget) {
    widget->show();
  }

  void obsharp_widget_hide(QWidget* widget) {
    widget->hide();
  }

  void obsharp_widget_set_visible(QWidget* widget, bool visible) {
    widget->setVisible(visible);
  }

  bool obsharp_widget_close(QWidget* widget) {
    return widget->close();
  }

  void obsharp_widget_show_minimized(QWidget* widget) { widget->showMinimized(); }
  void obsharp_widget_show_maximized(QWidget* widget) { widget->showMaximized(); }
  void obsharp_widget_show_full_screen(QWidget* widget) { widget->showFullScreen(); }
  void obsharp_widget_show_normal(QWidget* widget) { widget->showNormal(); }

  bool obsharp_widget_is_minimized(QWidget* widget) { return widget->isMinimized(); }
  bool obsharp_widget_is_maximized(QWidget* widget) { return widget->isMaximized(); }
  bool obsharp_widget_is_full_screen(QWidget* widget) { return widget->isFullScreen(); }

  Qt::WindowState obsharp_widget_get_window_state(QWidget* widget) {
    return static_cast<Qt::WindowState>(widget->windowState().toInt());
  }

  void obsharp_widget_set_window_state(QWidget* widget, Qt::WindowState state) {
    widget->setWindowState(state);
  }

  // ===== 几何 =====
  int obsharp_widget_get_x(QWidget* widget) { return widget->x(); }
  int obsharp_widget_get_y(QWidget* widget) { return widget->y(); }
  int obsharp_widget_get_width(QWidget* widget) { return widget->width(); }
  int obsharp_widget_get_height(QWidget* widget) { return widget->height(); }

  void obsharp_widget_get_geometry(QWidget* widget, int* x, int* y, int* w, int* h) {
    const QRect r = widget->geometry();
    if (x) *x = r.x();
    if (y) *y = r.y();
    if (w) *w = r.width();
    if (h) *h = r.height();
  }

  void obsharp_widget_get_frame_geometry(QWidget* widget, int* x, int* y, int* w, int* h) {
    const QRect r = widget->frameGeometry();
    if (x) *x = r.x();
    if (y) *y = r.y();
    if (w) *w = r.width();
    if (h) *h = r.height();
  }

  void obsharp_widget_move(QWidget* widget, int x, int y) { widget->move(x, y); }
  void obsharp_widget_resize(QWidget* widget, int w, int h) { widget->resize(w, h); }

  void obsharp_widget_set_geometry(QWidget* widget, int x, int y, int w, int h) {
    widget->setGeometry(x, y, w, h);
  }

  void obsharp_widget_set_fixed_size(QWidget* widget, int w, int h) {
    widget->setFixedSize(w, h);
  }

  void obsharp_widget_set_minimum_size(QWidget* widget, int w, int h) {
    widget->setMinimumSize(w, h);
  }

  void obsharp_widget_set_maximum_size(QWidget* widget, int w, int h) {
    widget->setMaximumSize(w, h);
  }

  void obsharp_widget_get_minimum_size(QWidget* widget, int* w, int* h) {
    const QSize s = widget->minimumSize();
    if (w) *w = s.width();
    if (h) *h = s.height();
  }

  void obsharp_widget_get_maximum_size(QWidget* widget, int* w, int* h) {
    const QSize s = widget->maximumSize();
    if (w) *w = s.width();
    if (h) *h = s.height();
  }

  void obsharp_widget_adjust_size(QWidget* widget) { widget->adjustSize(); }

  // ===== 焦点 / 激活 =====
  bool obsharp_widget_is_active_window(QWidget* widget) { return widget->isActiveWindow(); }
  void obsharp_widget_activate_window(QWidget* widget) { widget->activateWindow(); }
  bool obsharp_widget_has_focus(QWidget* widget) { return widget->hasFocus(); }
  void obsharp_widget_set_focus(QWidget* widget) { widget->setFocus(Qt::OtherFocusReason); }
  void obsharp_widget_clear_focus(QWidget* widget) { widget->clearFocus(); }

  Qt::FocusPolicy obsharp_widget_get_focus_policy(QWidget* widget) {
    return widget->focusPolicy();
  }

  void obsharp_widget_set_focus_policy(QWidget* widget, Qt::FocusPolicy policy) {
    widget->setFocusPolicy(policy);
  }

  // ===== Z 序 =====
  void obsharp_widget_raise(QWidget* widget) { widget->raise(); }
  void obsharp_widget_lower(QWidget* widget) { widget->lower(); }
  void obsharp_widget_stack_under(QWidget* widget, QWidget* other) { widget->stackUnder(other); }

  // ===== 父子关系 =====
  QWidget* obsharp_widget_get_parent(QWidget* widget) { return widget->parentWidget(); }
  QWidget* obsharp_widget_get_window(QWidget* widget) { return widget->window(); }

  void obsharp_widget_set_parent(QWidget* widget, QWidget* parent) {
    widget->setParent(parent);
  }

  // ===== 窗口标志 =====
  Qt::WindowType obsharp_widget_get_window_flags(QWidget* widget) {
    return static_cast<Qt::WindowType>(widget->windowFlags().toInt());
  }

  void obsharp_widget_set_window_flags(QWidget* widget, Qt::WindowType flags) {
    widget->setWindowFlags(flags);
  }

  Qt::WindowType obsharp_widget_get_window_type(QWidget* widget) {
    return widget->windowType();
  }

  void obsharp_widget_set_window_flag(QWidget* widget, Qt::WindowType type, bool on) {
    widget->setWindowFlag(type, on);
  }

  // ===== 工具提示 / 样式表 =====
  const char* obsharp_widget_get_tool_tip(QWidget* widget) {
    return BorrowedUtf8(widget->toolTip());
  }

  void obsharp_widget_set_tool_tip(QWidget* widget, const char* tip) {
    widget->setToolTip(ToQt(tip));
  }

  const char* obsharp_widget_get_style_sheet(QWidget* widget) {
    return BorrowedUtf8(widget->styleSheet());
  }

  void obsharp_widget_set_style_sheet(QWidget* widget, const char* sheet) {
    widget->setStyleSheet(ToQt(sheet));
  }

  // ===== 控件属性 =====
  void obsharp_widget_set_attribute(QWidget* widget, Qt::WidgetAttribute attribute, bool on) {
    widget->setAttribute(attribute, on);
  }

  bool obsharp_widget_test_attribute(QWidget* widget, Qt::WidgetAttribute attribute) {
    return widget->testAttribute(attribute);
  }

  // ===== 尺寸策略 =====
  QSizePolicy::Policy obsharp_widget_get_size_policy_horizontal(QWidget* widget) {
    return widget->sizePolicy().horizontalPolicy();
  }

  QSizePolicy::Policy obsharp_widget_get_size_policy_vertical(QWidget* widget) {
    return widget->sizePolicy().verticalPolicy();
  }

  void obsharp_widget_set_size_policy(QWidget* widget, QSizePolicy::Policy horizontal, QSizePolicy::Policy vertical) {
    widget->setSizePolicy(horizontal, vertical);
  }

  // ===== 重绘 =====
  void obsharp_widget_update(QWidget* widget) {
    widget->update();
  }

  // ===== 内容布局 =====
  void obsharp_widget_set_content(QWidget* container, QWidget* child) {
    QBoxLayout* layout = EnsureLayout(container);
    if (!layout)
      return; // 有个不是 QBoxLayout 的布局：不替调用方拆，保持原样。

    // 摘掉旧内容。注意**不销毁**它：内容的生命在托管侧手上（可能是浏览器 widget，
    // 也可能下一秒被塞进另一个容器）。只把它从布局里拿出来、隐藏、断开父级，
    // 这样它不会再跟着这个容器一起被 Qt 销毁。
    if (QWidget* previous = layout->itemAt(0) ? layout->itemAt(0)->widget() : nullptr) {
      layout->removeWidget(previous);
      previous->hide();
      previous->setParent(nullptr);
    }

    if (child)
      layout->addWidget(child);
  }

  // ===== 背景 =====
  void obsharp_widget_set_translucent_background(QWidget* widget) {
    // WA_TranslucentBackground 必须关掉 autoFillBackground：开着的话 Qt 会先把
    // 窗口背景刷成 palette 的 Window 色，透明就没了。
    widget->setAttribute(Qt::WA_TranslucentBackground, true);
    widget->setAutoFillBackground(false);
  }

  void obsharp_widget_set_background_color(QWidget* widget, unsigned int argb) {
    // 尽力而为：给 widget 铺个底色，页面自己画的内容会盖在上面。CEF 用无窗口
    // 渲染，真正的背景是页面自己画的，这里只是「还没出第一帧时不露黑底」（或者
    // 页面本身是透明的时给个衬底）。
    const QColor color = QColor::fromRgba(argb);
    QPalette palette = widget->palette();
    palette.setColor(QPalette::Window, color);
    widget->setPalette(palette);
    widget->setAutoFillBackground(true);
  }

  // ===== 查找 =====
  QWidget* obsharp_widget_find(uintptr_t native_id) {
    // QWidget::find 按 WId（Windows 上是 HWND）找**已经存在**的 widget，找不到
    // 返回 nullptr。这里不做「先当 HWND 找、找不到再当 QWidget* 用」的判断 ——
    // 那是托管侧在解释 parent 字段，属于调用方的策略。
    if (native_id == 0)
      return nullptr;

    return QWidget::find(static_cast<WId>(native_id));
  }

  // ===== 事件过滤 =====
  QObject* obsharp_widget_install_event_filter(QWidget* widget,
    obsharp_widget_filter_event_callback callback, void* user) {
    if (!callback)
      return nullptr;

    // 一个 widget 可以装多个过滤器，每个都是独立对象（各自有独立的回调与 user）。
    // 父级设成 widget：widget 被销毁时它们跟着走，不会留下野指针。
    auto* adapter = new EventFilterAdapter(widget, callback, user);
    widget->installEventFilter(adapter);
    return adapter;
  }

  void obsharp_widget_remove_event_filter(QWidget* widget, QObject* filter) {
    if (!widget || !filter)
      return;

    widget->removeEventFilter(filter);
    // 是我们 new 出来的（见 install），所以由我们归还。用 deleteLater 而不是
    // delete：可能正处在事件派发过程中，立刻删掉会把栈上的调用者抽空。
    filter->deleteLater();
  }
}