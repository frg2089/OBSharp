#pragma once

// Window.h：一个**纯 Qt 容器**（普通顶层窗口），不含任何 OBS 概念。
//
// 这个库只做两件事：
//   1. 把 CEF 包成 Qt Widget（BrowserWidget，见 BrowserWidget.h）；
//   2. 可选地提供一个普通顶层窗口把那个 Widget 装进去（本文件）。
//
// 停靠面板不在这里：托管侧拿 obsharp_browser_widget() 直接交给
// obs_frontend_add_dock_by_id()，OBS 自己会建 QDockWidget、挂进主窗口、
// 并且自动加「停靠窗口」菜单项（它内部走 AddDockWidget）。

#include "Export.h"

#include <QtCore/QString>

#include <memory>
#include <string>

class QWidget;

namespace obsharp {

// 一个普通的顶层窗口：Qt::Window（**不是** Qt::Tool —— 那在 Windows 上会变成
// WS_EX_TOOLWINDOW：细标题栏、不进任务栏、依附父窗口）。
//
// 用户点关闭时只 hide() 并回调 on_close_requested；销毁由 obsharp_window_destroy() 负责。
// 窗口不拥有内容 Widget：它只负责摆好布局，销毁时不碰内容。
class PlainWindow {
public:
	// 必须在 Qt 主线程调用。失败返回 nullptr，原因写进 error。
	static std::unique_ptr<PlainWindow> Create(const obsharp_window_options &options, std::string &error);

	~PlainWindow();

	PlainWindow(const PlainWindow &) = delete;
	PlainWindow &operator=(const PlainWindow &) = delete;

	// 窗口本身（借用，归本对象所有）。
	QWidget *Widget() const { return frame_; }

	// 放内容：通常是 BrowserWidget::Widget()。传 nullptr 清空。
	// 不接管所有权；只会从布局/父级上摘掉旧内容。
	bool SetContent(QWidget *content);

	void Show();
	void Hide();
	bool IsVisible() const;
	void SetTitle(const QString &title);
	void SetSize(int width, int height);

private:
	PlainWindow() = default;

	// 顶层 QWidget 的真实类型（PlainWindowFrame）定义在 Window.cpp：它只多一个
	// closeEvent 覆写，外面用不到。
	QWidget *frame_ = nullptr;
	// 当前内容（不拥有）。
	QWidget *content_ = nullptr;
	bool fixedSize_ = false;
};

} // namespace obsharp
