#include "window.h"

#include "logger.h"

#include <QtGui/QCloseEvent>
#include <QtWidgets/QBoxLayout>

namespace OBSharp::Window
{
  namespace
  {
    // 「点关闭时隐藏并回调，但不销毁」的顶层窗口。
    //
    // 覆写 closeEvent 而不是连信号：closeEvent 是 QWidget 的虚函数，不需要 moc，
    // 也不会跟托管侧已经装上的事件过滤器打架。
    class PlainWindow : public QWidget
    {
    public:
      PlainWindow(obsharp_window_close_callback on_close, void* user)
        : QWidget(nullptr, Qt::Window), on_close_(on_close), user_(user) {
        // 内容边距 0、间距 0：浏览器要铺满整个窗口。
        auto* layout = new QVBoxLayout(this);
        layout->setContentsMargins(0, 0, 0, 0);
        layout->setSpacing(0);
      }

    protected:
      void closeEvent(QCloseEvent* event) override {
        // 顺序很重要：**先隐藏，再回调**。
        //
        // on_close 里常见的做法是「销毁窗口和浏览器」，但也可能只是「藏起来，下次
        // 再 show」。先 hide() 保证回调里看到的窗口已经是隐藏状态 —— 如果反过来，
        // 回调里 show() 之后我们又 hide()，用户会看到窗口闪一下然后消失。
        hide();
        event->accept();

        if (on_close_)
          on_close_(this, user_);
      }

    private:
      obsharp_window_close_callback on_close_ = nullptr;
      void* user_ = nullptr;
    };
  } // namespace

  QWidget* obsharp_window_create(obsharp_window_close_callback on_close, void* user) {
    auto* window = new PlainWindow(on_close, user);

    Log(OBSHARP_LOG_DEBUG, "plain window created (%p)", static_cast<void*>(window));
    return window;
  }
}
