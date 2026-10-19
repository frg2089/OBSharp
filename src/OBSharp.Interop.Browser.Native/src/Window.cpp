// Window.cpp: 公开 C ABI 里窗口部分的实现（obsharp_dock_window_* / obsharp_tool_window_*）。
//
// 设计
// ----
//   * 两个窗口类都只是 Qt 容器，里面的浏览器通过 obsharp_browser_create() 建出来
//     （CEF 初始化、host 与 URL 映射、scheme handler 注册都留在
//     OBSharp.Interop.Browser.Native.cpp 一处）。obsharp_browser 的真实定义也在那个
//     文件里，所以这里只拿不透明指针，绝不自己重新定义它。
//   * 浏览器 Widget 随窗口一起建：QDockWidget 需要能立刻 setWidget()，交给 OBS 的
//     obs_frontend_add_custom_qdock() 之后窗口就该是完整的，所以这里不做「第一次
//     show 才建 Widget」的懒加载。真正的 CefBrowser 实例由 obs-browser 的 QCefWidget
//     自己按需创建（不是这个文件管的）。
//   * 销毁顺序：hide 容器 → obsharp_browser_destroy()（它把内容 Widget 排进
//     deleteLater）→ 容器自己也 deleteLater。同一线程同一事件循环上的 FIFO 保证
//     Widget 先被删，容器析构时不会再碰到它。反过来（先删容器）的话 Qt 会连带删掉
//     Widget，BrowserWidget 析构时就成了悬空指针。
//
// 错误处理
// --------
//   * 失败时既写日志，也调 obsharp::RecordLastError()（实现在 Log.cpp）：托管侧
//     CefDockWindow / CefToolWindow 的 Create 会把它拼进异常消息，不然调用方
//     拿到的只有一句「返回了 NULL」。
//   * 所有导出函数都要求 Qt 主线程（destroy 例外：照 obsharp_browser_destroy 的写法
//     切到主线程上同步做完，返回 void 的函数没法报错，返回码的函数直接报
//     OBSHARP_ERR_WRONG_THREAD）。

#include "Window.h"

#include "Log.h"

#include <QtCore/QUrl>
#include <QtGui/QCloseEvent>
#include <QtWidgets/QDockWidget>
#include <QtWidgets/QLayout>
#include <QtWidgets/QVBoxLayout>
#include <QtWidgets/QWidget>

#include <cstring>
#include <memory>
#include <string>

namespace obsharp {

namespace {

constexpr int kDockDefaultWidth = 640;
constexpr int kDockDefaultHeight = 480;
constexpr int kToolDefaultWidth = 720;
constexpr int kToolDefaultHeight = 480;

// id / title 都没法从参数或 URL 推出来时的兜底。
constexpr const char *kFallbackId = "obsharp";
constexpr const char *kFallbackTitle = "OB#";

int32_t Fail(int32_t code, const std::string &message) {
	RecordLastError(message);
	LogError(message);
	return code;
}

// 从浏览器参数里猜窗口所属的 host：优先 host 字段，其次 URL 的 host。
//
// URL 可能已经被映射成 CEF 里的 https://<host>.obsharp/（比如调用方自己先算过），
// 所以顺带把 ".obsharp" 后缀摘掉，让 id / title 看起来还是 OB# 的 host 名。
std::string ResolveHostName(const obsharp_browser_options &browser) {
	if (browser.host && *browser.host)
		return browser.host;

	if (!browser.url || !*browser.url)
		return {};

	QString host = QUrl(QString::fromUtf8(browser.url)).host();
	if (host.endsWith(QLatin1String(".obsharp")))
		host.chop(static_cast<int>(std::strlen(".obsharp")));

	return host.toStdString();
}

// 窗口 id：NULL / 空串时用 host 名兜底（OBS 的停靠面板 id 不能为空）。
std::string ResolveId(const char *id, const obsharp_browser_options &browser) {
	if (id && *id)
		return id;

	const std::string host = ResolveHostName(browser);
	return host.empty() ? std::string(kFallbackId) : host;
}

// 窗口标题：NULL / 空串时用 host 名兜底。
std::string ResolveTitle(const char *title, const obsharp_browser_options &browser) {
	if (title && *title)
		return title;

	const std::string host = ResolveHostName(browser);
	return host.empty() ? std::string(kFallbackTitle) : host;
}

// 窗口把自己的浏览器 Widget 当内容，所以 parent_widget 一律忽略。
//
// size 允许是 0（公开头允许「全用默认值」），这时补成本次编译的 sizeof；比本
// 次编译小的 size 说明调用方拿的是旧头文件，直接拒绝。
bool PrepareBrowserOptions(const obsharp_browser_options &input, obsharp_browser_options &output, std::string &error) {
	output = input;
	if (output.size == 0)
		output.size = sizeof(obsharp_browser_options);
	else if (output.size < sizeof(obsharp_browser_options)) {
		error = "obsharp_browser_options.size is smaller than this build expects";
		return false;
	}

	output.parent_widget = nullptr;
	return true;
}

// ---- 工具窗口的顶层 QWidget --------------------------------------------

// 只为了拦 closeEvent：用户点关闭时隐藏窗口并通知托管侧，不销毁。
//
// 这里不用 Q_OBJECT：closeEvent 是 QWidget 的虚函数，覆写它不需要 moc；回调存成
// 普通的 C 函数指针 + user，也不经过信号槽。
class ToolWindowFrame : public QWidget {
public:
	ToolWindowFrame(Qt::WindowFlags flags, const obsharp_window_callbacks &callbacks)
		: QWidget(nullptr, flags), callbacks_(callbacks) {}

protected:
	void closeEvent(QCloseEvent *event) override {
		// 先隐藏再回调：回调里托管侧可能马上又 show 一次。
		hide();
		event->accept();

		if (callbacks_.on_close_requested)
			callbacks_.on_close_requested(callbacks_.user);
	}

private:
	obsharp_window_callbacks callbacks_{};
};

// 销毁窗口前把浏览器 Widget 从容器上摘下来。
//
// 容器（QDockWidget / ToolWindowFrame）和浏览器 Widget 是父子关系：容器析构时会
// 连带删掉子 Widget。但浏览器 Widget 归 BrowserWidget 所有，它的析构又会去
// deleteLater() 这个指针 —— 容器先被删掉的话那就是悬空指针。
//
// 所以统一先 hide、再从容器上摘掉（用容器自己的 API，别绕过它的登记表），最后
// setParent(nullptr) 把所有权交回浏览器。
QWidget *ContentWidget(obsharp_browser *browser) {
	if (!browser)
		return nullptr;

	return static_cast<QWidget *>(obsharp_browser_widget(browser));
}

} // namespace

// ---- DockWindow --------------------------------------------------------

std::unique_ptr<DockWindow> DockWindow::Create(const obsharp_dock_window_options &options, std::string &error) {
	obsharp_browser_options browserOptions{};
	if (!PrepareBrowserOptions(options.browser, browserOptions, error))
		return nullptr;

	const int width = options.width > 0 ? options.width : kDockDefaultWidth;
	const int height = options.height > 0 ? options.height : kDockDefaultHeight;
	const QString title = QString::fromStdString(ResolveTitle(options.title, browserOptions));
	const QString id = QString::fromStdString(ResolveId(options.id, browserOptions));

	// 停靠面板的开关由 OBS 的 dock 决定，公开头也没给停靠窗口定义关闭事件。
	if (options.callbacks.on_close_requested)
		LogWarning("dock windows ignore callbacks.on_close_requested; use the OBS dock menu instead");

	std::unique_ptr<DockWindow> self(new DockWindow());

	self->browser_ = obsharp_browser_create(&browserOptions);
	if (!self->browser_) {
		error = "obsharp_browser_create() returned null";
		return nullptr;
	}

	QWidget *content = static_cast<QWidget *>(obsharp_browser_widget(self->browser_));
	if (!content) {
		error = "obsharp_browser_widget() returned null";
		return nullptr;
	}

	QDockWidget *dock = new QDockWidget();
	dock->setObjectName(id);
	dock->setWindowTitle(title);
	dock->setWidget(content);
	dock->resize(width, height);
	self->dock_ = dock;

	// 还没挂进 OBS 时 show() 会让它变成一个浮动窗口；这是 options.visible 的语义。
	if (options.visible)
		self->Show();

	LogInfo("dock window created: id=" + id.toStdString() + ", title=" + title.toStdString());
	return self;
}

DockWindow::~DockWindow() {
	// 销毁顺序（见文件头）：
	//   1. hide 容器；
	//   2. 把内容 Widget 从 QDockWidget 上摘下来（setParent(nullptr)），所有权交回
	//      浏览器 —— 否则容器先析构会连带删掉 Widget，BrowserWidget 再 deleteLater
	//      它就是悬空指针；
	//   3. 销毁浏览器（它负责 hide + deleteLater 内容 Widget）；
	//   4. 容器自己也 deleteLater。
	if (dock_)
		dock_->hide();

	// 先让 QDockWidget 自己把内容 Widget 摘掉（它会去改自己的布局登记），再把
	// 父子关系断开，所有权交回浏览器。
	QWidget *content = ContentWidget(browser_);
	if (content) {
		content->hide();
		if (dock_)
			dock_->setWidget(nullptr);
		content->setParent(nullptr);
	}

	if (browser_) {
		obsharp_browser_destroy(browser_);
		browser_ = nullptr;
	}

	if (dock_) {
		// 跟 BrowserWidget 一样推迟到事件循环里删，避免在信号处理过程中直接销毁。
		dock_->deleteLater();
		dock_ = nullptr;
	}
}

void DockWindow::Show() {
	if (dock_)
		dock_->show();
}

void DockWindow::Hide() {
	if (dock_)
		dock_->hide();
}

bool DockWindow::IsVisible() const {
	return dock_ && dock_->isVisible();
}

void DockWindow::SetTitle(const QString &title) {
	if (dock_)
		dock_->setWindowTitle(title);
}

void DockWindow::SetSize(int width, int height) {
	if (dock_)
		dock_->resize(width, height);
}

// ---- ToolWindow --------------------------------------------------------

std::unique_ptr<ToolWindow> ToolWindow::Create(const obsharp_tool_window_options &options, std::string &error) {
	obsharp_browser_options browserOptions{};
	if (!PrepareBrowserOptions(options.browser, browserOptions, error))
		return nullptr;

	const int width = options.width > 0 ? options.width : kToolDefaultWidth;
	const int height = options.height > 0 ? options.height : kToolDefaultHeight;
	const bool resizable = options.resizable != 0;
	const QString title = QString::fromStdString(ResolveTitle(options.title, browserOptions));

	// 先建浏览器再建窗口：浏览器建失败时直接返回，不会留下一个空窗口要收拾。
	std::unique_ptr<ToolWindow> self(new ToolWindow());
	self->browser_ = obsharp_browser_create(&browserOptions);
	if (!self->browser_) {
		error = "obsharp_browser_create() returned null";
		return nullptr;
	}

	QWidget *content = static_cast<QWidget *>(obsharp_browser_widget(self->browser_));
	if (!content) {
		error = "obsharp_browser_widget() returned null";
		return nullptr;
	}

	Qt::WindowFlags flags = Qt::Window | Qt::Tool;
	if (options.topmost)
		flags |= Qt::WindowStaysOnTopHint;

	ToolWindowFrame *frame = new ToolWindowFrame(flags, options.callbacks);
	// 不设 WA_DeleteOnClose：关闭只隐藏，销毁由 obsharp_tool_window_destroy() 负责。
	frame->setWindowTitle(title);
	if (resizable)
		frame->resize(width, height);
	else
		frame->setFixedSize(width, height);

	auto *layout = new QVBoxLayout(frame);
	layout->setContentsMargins(0, 0, 0, 0);
	layout->setSpacing(0);
	layout->addWidget(content);

	self->frame_ = frame;
	self->fixedSize_ = !resizable;

	if (options.visible)
		self->Show();

	LogInfo("tool window created: title=" + title.toStdString() + ", topmost=" +
		(options.topmost ? "1" : "0") + ", resizable=" + (resizable ? "1" : "0"));
	return self;
}

ToolWindow::~ToolWindow() {
	// 和 DockWindow 同一套顺序：hide → 摘掉内容 Widget → 销毁浏览器 → 删顶层窗口。
	if (frame_)
		frame_->hide();

	// 顶层窗口的内容 Widget 挂在 QVBoxLayout 上：先从布局里摘掉，再断父子关系。
	QWidget *content = ContentWidget(browser_);
	if (content) {
		content->hide();
		if (frame_) {
			if (QLayout *layout = frame_->layout())
				layout->removeWidget(content);
		}
		content->setParent(nullptr);
	}

	if (browser_) {
		obsharp_browser_destroy(browser_);
		browser_ = nullptr;
	}

	if (frame_) {
		frame_->deleteLater();
		frame_ = nullptr;
	}
}

void ToolWindow::Show() {
	if (!frame_)
		return;

	frame_->show();
	frame_->raise();
}

void ToolWindow::Hide() {
	if (frame_)
		frame_->hide();
}

bool ToolWindow::IsVisible() const {
	return frame_ && frame_->isVisible();
}

void ToolWindow::SetTitle(const QString &title) {
	if (frame_)
		frame_->setWindowTitle(title);
}

void ToolWindow::SetSize(int width, int height) {
	if (!frame_)
		return;

	if (fixedSize_) {
		// resizable == 0 时窗口大小是固定的，setFixedSize 会同时改 min / max。
		frame_->setFixedSize(width, height);
		return;
	}

	frame_->resize(width, height);
}

} // namespace obsharp

// ---------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------

// 句柄：和 obsharp_browser 一样，C ABI 里只有不透明指针，真实定义在这里。
struct obsharp_dock_window {
	std::unique_ptr<obsharp::DockWindow> impl;
};

struct obsharp_tool_window {
	std::unique_ptr<obsharp::ToolWindow> impl;
};

namespace {

void OBSHARP_BROWSER_NATIVE_CALL DestroyDockWindowThunk(void *user);
void OBSHARP_BROWSER_NATIVE_CALL DestroyToolWindowThunk(void *user);

int32_t RequireUiThread(const char *what) {
	if (obsharp_browser_native_is_ui_thread() != 0)
		return OBSHARP_OK;

	return obsharp::Fail(OBSHARP_ERR_WRONG_THREAD,
			     std::string(what) + " must be called on the Qt main thread");
}

} // namespace

extern "C" {

// ---- 停靠窗口 ----------------------------------------------------------

obsharp_dock_window *OBSHARP_BROWSER_NATIVE_CALL
obsharp_dock_window_create(const obsharp_dock_window_options *options) {
	if (!options) {
		obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_dock_window_create: options must not be null");
		return nullptr;
	}

	if (options->size < sizeof(obsharp_dock_window_options)) {
		obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT,
			      "obsharp_dock_window_options.size is smaller than this build expects; recompile the "
			      "caller against a matching header");
		return nullptr;
	}

	if (obsharp_browser_native_is_ui_thread() == 0) {
		obsharp::Fail(OBSHARP_ERR_WRONG_THREAD, "obsharp_dock_window_create must be called on the Qt main thread");
		return nullptr;
	}

	std::string error;
	std::unique_ptr<obsharp::DockWindow> impl = obsharp::DockWindow::Create(*options, error);
	if (!impl) {
		std::string message = "obsharp_dock_window_create failed: " + error;
		obsharp::RecordLastError(message);
		obsharp::LogError(message);
		return nullptr;
	}

	auto *window = new obsharp_dock_window();
	window->impl = std::move(impl);
	return window;
}

void OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_destroy(obsharp_dock_window *window) {
	if (!window)
		return;

	if (obsharp_browser_native_is_ui_thread() == 0) {
		// 跟 obsharp_browser_destroy 一样：销毁不会「失败」，只能切到主线程上做完，
		// 否则调用方丢掉句柄就等于泄漏。
		obsharp::LogWarning("obsharp_dock_window_destroy was called off the Qt main thread; marshalling");
		obsharp_browser_native_invoke_ui(&DestroyDockWindowThunk, window);
		return;
	}

	delete window;
}

void *OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_widget(obsharp_dock_window *window) {
	if (!window || !window->impl)
		return nullptr;

	return window->impl->Widget();
}

obsharp_browser *OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_browser(obsharp_dock_window *window) {
	if (!window || !window->impl)
		return nullptr;

	return window->impl->Browser();
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_show(obsharp_dock_window *window) {
	if (const int32_t result = RequireUiThread("obsharp_dock_window_show"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_dock_window_show: window is null");

	window->impl->Show();
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_hide(obsharp_dock_window *window) {
	if (const int32_t result = RequireUiThread("obsharp_dock_window_hide"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_dock_window_hide: window is null");

	window->impl->Hide();
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_is_visible(obsharp_dock_window *window) {
	if (obsharp_browser_native_is_ui_thread() == 0) {
		obsharp::LogError("obsharp_dock_window_is_visible must be called on the Qt main thread");
		return 0;
	}

	if (!window || !window->impl)
		return 0;

	return window->impl->IsVisible() ? 1 : 0;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_set_title(obsharp_dock_window *window, const char *title) {
	if (const int32_t result = RequireUiThread("obsharp_dock_window_set_title"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_dock_window_set_title: window is null");

	if (!title)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_dock_window_set_title: title must not be null");

	window->impl->SetTitle(QString::fromUtf8(title));
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_dock_window_set_size(obsharp_dock_window *window, int32_t width,
								 int32_t height) {
	if (const int32_t result = RequireUiThread("obsharp_dock_window_set_size"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_dock_window_set_size: window is null");

	if (width <= 0 || height <= 0)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT,
				     "obsharp_dock_window_set_size: width and height must be positive");

	window->impl->SetSize(width, height);
	return OBSHARP_OK;
}

// ---- 工具窗口 ----------------------------------------------------------

obsharp_tool_window *OBSHARP_BROWSER_NATIVE_CALL
obsharp_tool_window_create(const obsharp_tool_window_options *options) {
	if (!options) {
		obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_tool_window_create: options must not be null");
		return nullptr;
	}

	if (options->size < sizeof(obsharp_tool_window_options)) {
		obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT,
			      "obsharp_tool_window_options.size is smaller than this build expects; recompile the "
			      "caller against a matching header");
		return nullptr;
	}

	if (obsharp_browser_native_is_ui_thread() == 0) {
		obsharp::Fail(OBSHARP_ERR_WRONG_THREAD, "obsharp_tool_window_create must be called on the Qt main thread");
		return nullptr;
	}

	std::string error;
	std::unique_ptr<obsharp::ToolWindow> impl = obsharp::ToolWindow::Create(*options, error);
	if (!impl) {
		std::string message = "obsharp_tool_window_create failed: " + error;
		obsharp::RecordLastError(message);
		obsharp::LogError(message);
		return nullptr;
	}

	auto *window = new obsharp_tool_window();
	window->impl = std::move(impl);
	return window;
}

void OBSHARP_BROWSER_NATIVE_CALL obsharp_tool_window_destroy(obsharp_tool_window *window) {
	if (!window)
		return;

	if (obsharp_browser_native_is_ui_thread() == 0) {
		obsharp::LogWarning("obsharp_tool_window_destroy was called off the Qt main thread; marshalling");
		obsharp_browser_native_invoke_ui(&DestroyToolWindowThunk, window);
		return;
	}

	delete window;
}

obsharp_browser *OBSHARP_BROWSER_NATIVE_CALL obsharp_tool_window_browser(obsharp_tool_window *window) {
	if (!window || !window->impl)
		return nullptr;

	return window->impl->Browser();
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_tool_window_show(obsharp_tool_window *window) {
	if (const int32_t result = RequireUiThread("obsharp_tool_window_show"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_tool_window_show: window is null");

	window->impl->Show();
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_tool_window_hide(obsharp_tool_window *window) {
	if (const int32_t result = RequireUiThread("obsharp_tool_window_hide"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_tool_window_hide: window is null");

	window->impl->Hide();
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_tool_window_is_visible(obsharp_tool_window *window) {
	if (obsharp_browser_native_is_ui_thread() == 0) {
		obsharp::LogError("obsharp_tool_window_is_visible must be called on the Qt main thread");
		return 0;
	}

	if (!window || !window->impl)
		return 0;

	return window->impl->IsVisible() ? 1 : 0;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_tool_window_set_title(obsharp_tool_window *window, const char *title) {
	if (const int32_t result = RequireUiThread("obsharp_tool_window_set_title"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_tool_window_set_title: window is null");

	if (!title)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_tool_window_set_title: title must not be null");

	window->impl->SetTitle(QString::fromUtf8(title));
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_tool_window_set_size(obsharp_tool_window *window, int32_t width,
								 int32_t height) {
	if (const int32_t result = RequireUiThread("obsharp_tool_window_set_size"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_tool_window_set_size: window is null");

	if (width <= 0 || height <= 0)
		return obsharp::Fail(OBSHARP_ERR_INVALID_ARGUMENT,
				     "obsharp_tool_window_set_size: width and height must be positive");

	window->impl->SetSize(width, height);
	return OBSHARP_OK;
}

} // extern "C"

// ---------------------------------------------------------------------------
// thunk
// ---------------------------------------------------------------------------

namespace {

void OBSHARP_BROWSER_NATIVE_CALL DestroyDockWindowThunk(void *user) {
	obsharp_dock_window_destroy(static_cast<obsharp_dock_window *>(user));
}

void OBSHARP_BROWSER_NATIVE_CALL DestroyToolWindowThunk(void *user) {
	obsharp_tool_window_destroy(static_cast<obsharp_tool_window *>(user));
}

} // namespace
