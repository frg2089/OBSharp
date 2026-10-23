#include "qcef.h"

#include "logger.h"

#include <include/cef_browser.h>
#include <include/cef_task.h>

#include <browser-panel-internal.hpp>

#include <cstring>

#ifdef _WIN32
#include <Windows.h>
#endif

// devtools.cpp：调试设施 —— obsharp_cef_widget_show_dev_tools，给 widget 的浏览器
// 弹 DevTools（等价于 obs-browser 右键菜单的「检查」）。
//
// 为什么不能做成 qcef.cpp 那种一行转发：这条路要摸 obs-browser 的内部对象。
// CEF 没有「按 widget / 窗口找浏览器」的全局 API（CefBrowserHost 上只有实例方法），
// QCefWidget 的公开虚函数里也没有 DevTools 入口 —— 右键「检查」那条通路在
// QCefBrowserClient::OnContextMenuCommand 里，外面够不着。唯一的缝是
// create_widget 返回的对象确实是 QCefWidgetInternal（panel 的内部类），它的
// cefBrowser 是公开成员。但这是「跟 obs-browser 二进制对布局」的内部约定，所以：
//
//   * 读取全部包在 SEH 里（Windows）：版本错配导致的野读直接变安全失败，不炸进程；
//   * expected_url（create_widget 时的 url）是布局探针 —— url 成员紧跟 cefBrowser
//     之后，读出来的字符串对不上就说明布局变了，立刻放弃；
//   * 整条通路只在调试开关下走，正常功能完全不碰它。
//
// 线程约定同 qcef.cpp：入口要求 Qt 主线程（几何取样），CEF 调用一律投到 TID_UI
// （ShowDevTools 与右键菜单同线程语义）。浏览器就绪时机归 obs-browser 管（它自己的
// 500ms 定时器驱动 Init，在 CEF UI 线程上 CreateBrowserSync），所以就绪前重试等待。

namespace OBSharp::Cef
{
  namespace
  {
    // DevTools 弹窗几何：与 obs-browser 右键「检查」一致（900x700，往下让开标题栏）。
    constexpr int kDevToolsWidth = 900;
    constexpr int kDevToolsHeight = 700;
    constexpr int kDevToolsYOffset = 30;

    // 浏览器就绪前的重试节奏与上限（20s，慢机器也够）。
    constexpr int kRetryDelayMs = 200;
    constexpr int kMaxAttempts = 100;

    /*
     * 读 QCefWidgetInternal::cefBrowser。
     *
     * 返回 true = 布局可信：*out 可能仍是 null（浏览器还没建好）；非 null 时已经
     * AddRef 过（这个虚调用兼做野指针探测 —— 指针是垃圾就当场 AV，被 SEH 兜住）。
     * 返回 false = 布局对不上或野读 AV：*out 恒为 null，调用方放弃。
     *
     * __try 的函数里不能有需要析构的局部（C2712），这里只用裸指针。
     */
    bool TryReadBrowser(QCefWidget* widget, const char* expected_url, CefBrowser** out) {
      *out = nullptr;

#ifdef _WIN32
      __try {
#endif
        auto* internal = static_cast<QCefWidgetInternal*>(widget);

        // 布局探针：url 成员紧跟 cefBrowser 之后，值必须还是 create_widget 传的 URL。
        // strcmp 失败（含 AV）都当布局错配处理。
        if (std::strcmp(internal->url.c_str(), expected_url) != 0)
          return false;

        CefBrowser* raw = internal->cefBrowser.get();
        if (raw)
          raw->AddRef();
        *out = raw;
        return true;
#ifdef _WIN32
      } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
      }
#endif
    }

    // CEF UI 线程上的重试链：等到 cefBrowser 就绪就弹 DevTools。
    class ShowDevToolsTask : public CefTask
    {
    public:
      ShowDevToolsTask(QCefWidget* widget, std::string expected_url, CefRect bounds, int attempts_left)
        : widget_(widget), expected_url_(std::move(expected_url)), bounds_(bounds),
          attempts_left_(attempts_left) {}

      void Execute() override {
        CefBrowser* raw = nullptr;
        if (!TryReadBrowser(widget_, expected_url_.c_str(), &raw)) {
          Log(OBSHARP_LOG_WARNING,
            "[OBSharp.Cef] DevTools：obs-browser 内部布局校验失败，放弃弹出（只影响调试功能）");
          return;
        }

        if (!raw) {
          // 浏览器还没建好：obs-browser 的 Init 队列还没跑到 CreateBrowserSync。稍后重试。
          if (--attempts_left_ > 0) {
            CefPostDelayedTask(TID_UI, CefRefPtr<CefTask>(this), kRetryDelayMs);
          } else {
            Log(OBSHARP_LOG_WARNING, "[OBSharp.Cef] DevTools：等浏览器就绪超时，放弃弹出");
          }
          return;
        }

        // raw 在 TryReadBrowser 里 +1 过；CefRefPtr 构造再 +1，把手工那份放掉，
        // 从这里起由 browser 这一份管生命周期。
        CefRefPtr<CefBrowser> browser(raw);
        raw->Release();

        CefRefPtr<CefBrowserHost> host = browser->GetHost();
        CefWindowInfo window_info;
        window_info.SetAsPopup(host->GetWindowHandle(), "OB# DevTools");
        window_info.bounds = bounds_;

        // 与 obs-browser 右键「检查」完全同一条通路：client 用浏览器自己的
        // （QCefBrowserClient），DevTools 窗口的标题 / 菜单行为才跟手点一致。
        host->ShowDevTools(window_info, host->GetClient(), CefBrowserSettings(), CefPoint{0, 0});
        Log(OBSHARP_LOG_DEBUG, "[OBSharp.Cef] DevTools：已请求弹出");
      }

    private:
      QCefWidget* widget_;
      std::string expected_url_;
      CefRect bounds_;
      int attempts_left_;

      IMPLEMENT_REFCOUNTING(ShowDevToolsTask);
      DISALLOW_COPY_AND_ASSIGN(ShowDevToolsTask);
    };
  }

  bool obsharp_cef_widget_show_dev_tools(QCefWidget* widget, const char* expected_url) {
    const char* url = expected_url ? expected_url : "";

    // 立刻做一次布局探测：版本对不上就地放弃，连重试链都不启动。
    CefBrowser* probe = nullptr;
    if (!TryReadBrowser(widget, url, &probe)) {
      Log(OBSHARP_LOG_WARNING,
        "[OBSharp.Cef] DevTools：obs-browser 内部布局校验失败，跳过自动弹出"
        "（不影响其它功能；obs-browser 版本对齐后再试）");
      return false;
    }

    // 探测用的引用放开；重试链自己会重新拿。
    if (probe)
      probe->Release();

    // 几何在 Qt 主线程取（调用约定同 qcef.cpp 其它入口）：从 widget 左上角起、
    // 往下让开标题栏，900x700。
    const QPoint pos = widget->mapToGlobal(QPoint(0, 0));
    const CefRect bounds{pos.x(), pos.y() + kDevToolsYOffset, kDevToolsWidth, kDevToolsHeight};

    auto task = CefRefPtr<ShowDevToolsTask>(new ShowDevToolsTask(widget, url, bounds, kMaxAttempts));
    return CefPostTask(TID_UI, task);
  }
}
