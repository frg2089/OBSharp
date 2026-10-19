#pragma once

// Window.h：两种浏览器窗口（停靠窗口 / 工具窗口）的 C++ 侧封装。
//
// 公开 C ABI（include/OBSharp.Interop.Browser.Native.h）里的 obsharp_dock_window_*
// / obsharp_tool_window_* 在 Window.cpp 里实现；这个头只给本库内部用。
//
// 两个类都不直接碰 QCef / BrowserWidget，而是走公开的 obsharp_browser_create()：
// CEF 初始化、host 与 URL 映射、scheme handler 注册这些逻辑都留在
// OBSharp.Interop.Browser.Native.cpp 一处，窗口这边只负责 Qt 容器。因此
// obsharp_*_window_browser() 拿到的就是一个货真价实的 obsharp_browser*，可以照常
// 传给其它 obsharp_browser_* 函数（借用，不要 destroy）。

#include "Export.h"

#include <QtCore/QString>

#include <memory>
#include <string>

class QDockWidget;
class QWidget;

namespace obsharp {

// ---- 停靠窗口 ---------------------------------------------------------

// 一个 QDockWidget（内含浏览器 Widget）。托管侧从 obsharp_dock_window_widget()
// 拿到它，交给 obs_frontend_add_custom_qdock() 挂进 OBS；挂进去之前 show / hide
// 只会得到一个浮动窗口。
class DockWindow {
public:
	// 必须在 Qt 主线程调用。失败返回 nullptr，原因写进 error。
	static std::unique_ptr<DockWindow> Create(const obsharp_dock_window_options &options, std::string &error);

	~DockWindow();

	DockWindow(const DockWindow &) = delete;
	DockWindow &operator=(const DockWindow &) = delete;

	// QDockWidget*（借用，归本对象所有）。
	QDockWidget *Widget() const { return dock_; }
	// 浏览器句柄（借用，归本对象所有，别 obsharp_browser_destroy()）。
	obsharp_browser *Browser() const { return browser_; }

	void Show();
	void Hide();
	bool IsVisible() const;
	void SetTitle(const QString &title);
	void SetSize(int width, int height);

private:
	DockWindow() = default;

	QDockWidget *dock_ = nullptr;
	obsharp_browser *browser_ = nullptr;
};

// ---- 工具窗口 ---------------------------------------------------------

// 一个独立的顶层窗口（Qt::Window | Qt::Tool，内含浏览器 Widget）。用户点关闭时
// 只会 hide() 并回调 on_close_requested；真正销毁走 obsharp_tool_window_destroy()。
class ToolWindow {
public:
	// 必须在 Qt 主线程调用。失败返回 nullptr，原因写进 error。
	static std::unique_ptr<ToolWindow> Create(const obsharp_tool_window_options &options, std::string &error);

	~ToolWindow();

	ToolWindow(const ToolWindow &) = delete;
	ToolWindow &operator=(const ToolWindow &) = delete;

	// 浏览器句柄（借用，归本对象所有，别 obsharp_browser_destroy()）。
	obsharp_browser *Browser() const { return browser_; }

	void Show();
	void Hide();
	bool IsVisible() const;
	void SetTitle(const QString &title);
	void SetSize(int width, int height);

private:
	ToolWindow() = default;

	// 顶层 QWidget 的真实类型（ToolWindowFrame）定义在 Window.cpp：它只多一个
	// closeEvent 覆写，外面用不到。
	QWidget *frame_ = nullptr;
	obsharp_browser *browser_ = nullptr;
	// resizable == 0：窗口大小固定，SetSize 要改最小/最大尺寸。
	bool fixedSize_ = false;
};

} // namespace obsharp
