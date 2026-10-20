// Window.cpp: 公开 C ABI 里「普通顶层窗口」部分的实现（obsharp_window_*）。
//
// 设计边界
// --------
// 这个文件**不碰 OBS**。它只建一个 Qt::Window 顶层窗口、把内容 Widget 摆进去。
// 停靠面板由托管侧用 obsharp_browser_widget() + obs_frontend_add_dock_by_id() 完成，
// OBS 那边会自己建 QDockWidget、挂进主窗口、并加「停靠窗口」菜单项。
//
// 为什么窗口只用 Qt::Window
// ------------------------
// Qt::Tool 在 Windows 上映射成 WS_EX_TOOLWINDOW（细标题栏、不进任务栏、不进 Alt+Tab、
// 依附父窗口）。这里要的是普通窗口，所以只在需要置顶时叠 Qt::WindowStaysOnTopHint。
//
// 所有权
// ------
// 窗口**不拥有**内容 Widget：它只做 QVBoxLayout 的 addWidget / removeWidget。
// 销毁窗口时不 delete 内容；内容归 obsharp_browser（BrowserWidget）所有，
// 由托管侧决定谁先销毁。

#include "Window.h"

#include "Log.h"

#include <QtGui/QCloseEvent>
#include <QtWidgets/QBoxLayout>
#include <QtWidgets/QWidget>

#include <memory>
#include <string>

namespace obsharp {

namespace {

constexpr int kDefaultWidth = 720;
constexpr int kDefaultHeight = 480;

// 只为了拦 closeEvent：用户点关闭时隐藏窗口并通知托管侧，不销毁。
//
// 这里不用 Q_OBJECT：closeEvent 是 QWidget 的虚函数，覆写它不需要 moc；回调存成
// 普通的 C 函数指针 + user，也不经过信号槽。
class PlainWindowFrame : public QWidget {
public:
	PlainWindowFrame(Qt::WindowFlags flags, const obsharp_window_callbacks &callbacks)
		: QWidget(nullptr, flags), callbacks_(callbacks)
	{
		// 内容边距 0、间距 0：浏览器要铺满。
		auto *layout = new QVBoxLayout(this);
		layout->setContentsMargins(0, 0, 0, 0);
		layout->setSpacing(0);
		layout_ = layout;
	}

protected:
	void closeEvent(QCloseEvent *event) override {
		// 先隐藏再回调：回调里托管侧可能马上又 show 一次。
		hide();
		event->accept();

		LogDebug("plain window closeEvent: hidden (not destroyed)");

		if (callbacks_.on_close_requested)
			callbacks_.on_close_requested(callbacks_.user);
	}

	void showEvent(QShowEvent *event) override {
		QWidget::showEvent(event);
		LogDebug("plain window shown");
	}

private:
	obsharp_window_callbacks callbacks_{};
	QVBoxLayout *layout_ = nullptr;
};

} // namespace

std::unique_ptr<PlainWindow> PlainWindow::Create(const obsharp_window_options &options, std::string &error) {
	if (options.size != 0 && options.size < sizeof(obsharp_window_options)) {
		error = "obsharp_window_options.size is smaller than this build expects";
		return nullptr;
	}

	const int width = options.width > 0 ? options.width : kDefaultWidth;
	const int height = options.height > 0 ? options.height : kDefaultHeight;
	const bool resizable = options.resizable != 0;

	Qt::WindowFlags flags = Qt::Window;
	if (options.topmost)
		flags |= Qt::WindowStaysOnTopHint;

	auto frame = new PlainWindowFrame(flags, options.callbacks);

	// 内容边距 0、间距 0：浏览器要铺满。
	auto *layout = new QVBoxLayout(frame);
	layout->setContentsMargins(0, 0, 0, 0);
	layout->setSpacing(0);
	frame->setLayout(layout);

	if (options.title && *options.title)
		frame->setWindowTitle(QString::fromUtf8(options.title));

	if (resizable)
		frame->resize(width, height);
	else
		frame->setFixedSize(width, height);

	std::unique_ptr<PlainWindow> self(new PlainWindow());
	self->frame_ = frame;
	self->fixedSize_ = !resizable;

	if (options.visible)
		self->Show();

	LogInfo("plain window created: title=" + frame->windowTitle().toStdString() +
		", topmost=" + (options.topmost ? "1" : "0") + ", resizable=" + (resizable ? "1" : "0"));

	(void)error;
	return self;
}

PlainWindow::~PlainWindow() {
	if (!frame_)
		return;

	// 只把内容从布局里摘掉，**不**销毁它（归 BrowserWidget 所有）。
	if (content_) {
		if (auto *layout = frame_->layout())
			layout->removeWidget(content_);

		content_->hide();
		content_->setParent(nullptr);
		content_ = nullptr;
	}

	frame_->hide();
	frame_->deleteLater();
	frame_ = nullptr;
}

bool PlainWindow::SetContent(QWidget *content) {
	if (!frame_)
		return false;

	auto *layout = static_cast<QVBoxLayout *>(frame_->layout());
	if (!layout)
		return false;

	if (content_ == content)
		return true;

	// 摘掉旧内容（不销毁）。
	if (content_) {
		layout->removeWidget(content_);
		content_->hide();
		content_->setParent(nullptr);
		content_ = nullptr;
	}

	if (!content)
		return true;

	content_ = content;
	layout->addWidget(content);
	return true;
}

void PlainWindow::Show() {
	if (!frame_)
		return;

	frame_->show();
	frame_->raise();
}

void PlainWindow::Hide() {
	if (frame_)
		frame_->hide();
}

bool PlainWindow::IsVisible() const {
	return frame_ && frame_->isVisible();
}

void PlainWindow::SetTitle(const QString &title) {
	if (frame_)
		frame_->setWindowTitle(title);
}

void PlainWindow::SetSize(int width, int height) {
	if (!frame_)
		return;

	if (fixedSize_)
		frame_->setFixedSize(width, height);
	else
		frame_->resize(width, height);
}

} // namespace obsharp

// ---------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------

// 句柄：和 obsharp_browser 一样，C ABI 里只有不透明指针，真实定义在这里。
struct obsharp_window {
	std::unique_ptr<obsharp::PlainWindow> impl;
};

namespace {

int32_t RequireUiThread(const char *what) {
	if (obsharp_browser_native_is_ui_thread() != 0)
		return OBSHARP_OK;

	obsharp::RecordLastError(std::string(what) + " must be called on the Qt main thread");
	return OBSHARP_ERR_WRONG_THREAD;
}

int32_t Fail(int32_t code, const std::string &message) {
	obsharp::RecordLastError(message);
	obsharp::LogError(message);
	return code;
}

void OBSHARP_BROWSER_NATIVE_CALL DestroyWindowThunk(void *user);

} // namespace

extern "C" {

obsharp_window *OBSHARP_BROWSER_NATIVE_CALL obsharp_window_create(const obsharp_window_options *options) {
	if (RequireUiThread("obsharp_window_create") != OBSHARP_OK)
		return nullptr;

	if (!options) {
		Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_create: options must not be null");
		return nullptr;
	}

	std::string error;
	std::unique_ptr<obsharp::PlainWindow> impl = obsharp::PlainWindow::Create(*options, error);
	if (!impl) {
		std::string message = "obsharp_window_create failed: " + error;
		obsharp::RecordLastError(message);
		obsharp::LogError(message);
		return nullptr;
	}

	auto *window = new obsharp_window();
	window->impl = std::move(impl);
	return window;
}

void OBSHARP_BROWSER_NATIVE_CALL obsharp_window_destroy(obsharp_window *window) {
	if (!window)
		return;

	if (obsharp_browser_native_is_ui_thread() == 0) {
		// 非主线程：切过去同步做完再释放。切不过去就记一笔（进程多半在退出）。
		if (obsharp_browser_native_invoke_ui(&DestroyWindowThunk, window) != 0) {
			obsharp::LogWarning("obsharp_window_destroy could not reach the Qt main thread; leaking the window");
		}

		return;
	}

	delete window;
}

void *OBSHARP_BROWSER_NATIVE_CALL obsharp_window_widget(obsharp_window *window) {
	if (RequireUiThread("obsharp_window_widget") != OBSHARP_OK)
		return nullptr;

	if (!window || !window->impl)
		return nullptr;

	return window->impl->Widget();
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_set_content(obsharp_window *window, void *content) {
	if (const int32_t result = RequireUiThread("obsharp_window_set_content"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_set_content: window is null");

	if (!window->impl->SetContent(static_cast<QWidget *>(content)))
		return Fail(OBSHARP_ERR_INTERNAL, "obsharp_window_set_content: the window has no layout");

	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_show(obsharp_window *window) {
	if (const int32_t result = RequireUiThread("obsharp_window_show"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_show: window is null");

	window->impl->Show();
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_hide(obsharp_window *window) {
	if (const int32_t result = RequireUiThread("obsharp_window_hide"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_hide: window is null");

	window->impl->Hide();
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_is_visible(obsharp_window *window) {
	if (const int32_t result = RequireUiThread("obsharp_window_is_visible"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return 0;

	return window->impl->IsVisible() ? 1 : 0;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_set_title(obsharp_window *window, const char *title) {
	if (const int32_t result = RequireUiThread("obsharp_window_set_title"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_set_title: window is null");

	if (!title)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_set_title: title must not be null");

	window->impl->SetTitle(QString::fromUtf8(title));
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_set_size(obsharp_window *window, int32_t width, int32_t height) {
	if (const int32_t result = RequireUiThread("obsharp_window_set_size"); result != OBSHARP_OK)
		return result;

	if (!window || !window->impl)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_set_size: window is null");

	if (width <= 0 || height <= 0)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_window_set_size: width and height must be positive");

	window->impl->SetSize(width, height);
	return OBSHARP_OK;
}

} // extern "C"

// ---------------------------------------------------------------------------
// thunk
// ---------------------------------------------------------------------------

namespace {

void OBSHARP_BROWSER_NATIVE_CALL DestroyWindowThunk(void *user) {
	obsharp_window_destroy(static_cast<obsharp_window *>(user));
}

} // namespace
