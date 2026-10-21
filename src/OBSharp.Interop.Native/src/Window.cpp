#include "Export.h"

#include "Log.h"

#include <QtGui/QCloseEvent>
#include <QtWidgets/QBoxLayout>
#include <QtWidgets/QWidget>

// Window.cpp：C# 用来托管浏览器的「普通顶层窗口」。
//
// 这里只有**一个**导出，因为它只是个容器。标题、尺寸、是否置顶、是否可缩放都不在
// 这里定：C# 建完窗口之后用 obsharp_widget_* 一套转发逐个摆布。把参数搬进 C ABI
// 会多出一份 options 结构体、多一处「结构体版本不一致要不要兼容」的问题，而收益
// 只是省掉几个 P/Invoke 调用 —— 不值得。
//
// 为什么用 Qt::Window 而不是 Qt::Tool：Qt::Tool 在 Windows 上映射成
// WS_EX_TOOLWINDOW（细标题栏、不进任务栏、不进 Alt+Tab、依附父窗口）。这里要的是
// 普通窗口，所以基类只给 Qt::Window，置顶由 C# 需要时再加 WindowStaysOnTopHint。

namespace {

  // 「点关闭时隐藏并回调，但不销毁」的顶层窗口。
  //
  // 覆写 closeEvent 而不是连信号：closeEvent 是 QWidget 的虚函数，不需要 moc，
  // 也不会跟 C# 那边已经装上的事件过滤器打架。
  class PlainWindow : public QWidget {
  public:
    explicit PlainWindow(const obsharp_window_callbacks& callbacks)
      : QWidget(nullptr, Qt::Window), callbacks_(callbacks) {
      // 内容边距 0、间距 0：浏览器要铺满整个窗口。
      auto* layout = new QVBoxLayout(this);
      layout->setContentsMargins(0, 0, 0, 0);
      layout->setSpacing(0);
    }

  protected:
    void closeEvent(QCloseEvent* event) override {
      // 顺序很重要：**先隐藏，再回调**。
      //
      // C# 的 on_close_requested 里常见的做法是「销毁窗口和浏览器」，但也可能
      // 只是「藏起来，下次再 show」。先 hide() 保证回调里看到的窗口已经是隐藏
      // 状态 —— 如果反过来，回调里 show() 之后我们又 hide()，用户会看到窗口闪一下
      // 然后消失。
      hide();
      event->accept();

      if (callbacks_.on_close_requested)
        callbacks_.on_close_requested(callbacks_.user);
    }

  private:
    obsharp_window_callbacks callbacks_{};
  };

} // namespace

extern "C" {

  void* OBSHARP_NATIVE_CALL obsharp_window_create(const obsharp_window_callbacks* callbacks) {
    // callbacks 可以为 NULL：C# 有时建窗口但暂时不关心关闭事件（比如批量建窗口时
    // 先建后连）。按全零处理，不要崩。
    const obsharp_window_callbacks safe = callbacks ? *callbacks : obsharp_window_callbacks{};

    auto* window = new PlainWindow(safe);

    obsharp::LogF(OBSHARP_LOG_DEBUG, "plain window created (0x%p)", static_cast<void*>(window));
    return window;
  }

} // extern "C"
