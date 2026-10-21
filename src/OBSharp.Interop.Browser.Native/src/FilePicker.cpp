// FilePicker.cpp: 公开 C ABI 里「文件选择对话框」部分的实现
// （obsharp_file_picker_*）。
//
// 三种对话框都走 QFileDialog，用 open() 显示窗口模态对话框：启动函数只把窗口
// show 出来就返回，用户选完 / 取消后由 Qt 在事件循环里发 finished 信号。因此
// 不会像 exec() 那样跑嵌套事件循环，也不会阻塞调用方；但 QFileDialog 本身是
// Qt Widget，启动和完成回调都必须在 Qt 主线程上。
//
// 所有权
// ------
// 回调拿到的 obsharp_file_picker_result 归回调方所有，必须用
// obsharp_file_picker_result_destroy() 释放。里面的路径字符串跟着 result
// 一起失效，是借用指针。

#include "FilePicker.h"

#include "Log.h"

#include <QtCore/QDir>
#include <QtCore/QPointer>
#include <QtCore/QString>
#include <QtCore/QStringList>
#include <QtWidgets/QApplication>
#include <QtWidgets/QDialog>
#include <QtWidgets/QFileDialog>
#include <QtWidgets/QWidget>

#include <cstddef>
#include <cstdint>
#include <exception>
#include <memory>
#include <string>
#include <utility>
#include <vector>

// 句柄：跟 obsharp_browser / obsharp_window 一样，C ABI 里只有不透明指针，
// 真实定义放在这里，回调实现也能直接用。
struct obsharp_file_picker_result {
	bool cancelled = false;
	std::vector<std::string> paths;
};

namespace obsharp {

QWidget *ResolveFilePickerParent(void *parent) {
	if (!parent)
		return nullptr;

#ifdef Q_OS_WIN
	// Windows 上允许直接传 HWND（OBS 的 obs_frontend_get_main_window_handle()
	// 给的就是它）。QWidget::find 能找到对应的顶层窗口；传 QWidget* 时这里
	// 通常找不到，下面会原样当成 QWidget* 用。
	if (QWidget *found = QWidget::find(reinterpret_cast<WId>(parent)))
		return found;
#endif

	return reinterpret_cast<QWidget *>(parent);
}

namespace {

QString ToQt(const std::string &value) {
	return QString::fromUtf8(value.data(), static_cast<int>(value.size()));
}

// 一次对话框的完整状态。用 shared_ptr 让 finished / destroyed 两个信号都能安全
// 拿到它：先到的那个负责回调，后到的看到 completed 直接返回。
struct PickerContext {
	obsharp_file_picker_done_fn done = nullptr;
	void *user = nullptr;
	QPointer<QFileDialog> dialog;
	bool completed = false;
	std::string what;
};

void FinishPicker(const std::shared_ptr<PickerContext> &context, bool accepted) {
	if (context->completed)
		return;

	context->completed = true;

	QFileDialog *dialog = context->dialog.data();
	std::vector<std::string> paths;
	bool cancelled = !accepted;

	if (accepted && dialog) {
		try {
			const QStringList selected = dialog->selectedFiles();
			for (const QString &item : selected) {
				if (!item.isEmpty())
					paths.push_back(item.toUtf8().toStdString());
			}
		} catch (const std::exception &exception) {
			LogError(context->what + ": failed to read selected paths: " + exception.what());
			paths.clear();
			cancelled = true;
		} catch (...) {
			LogError(context->what + ": failed to read selected paths: unknown exception");
			paths.clear();
			cancelled = true;
		}

		// 用户按了「打开 / 保存」但没给出任何路径：当成取消，别报错。
		if (paths.empty())
			cancelled = true;
	}

	context->dialog = nullptr;
	if (dialog)
		dialog->deleteLater();

	auto *result = new obsharp_file_picker_result();
	result->cancelled = cancelled;
	result->paths = std::move(paths);

	if (cancelled)
		LogDebug(context->what + ": cancelled");
	else
		LogDebug(context->what + ": picked " + std::to_string(result->paths.size()) + " path(s)");

	context->done(context->user, result);
}

// 对话框被外部销毁（例如父窗口先没了）时 QFileDialog 不一定会发 finished。
// 这里保证回调仍然只来一次；此时不再碰正在析构的 dialog。
void AbortPicker(const std::shared_ptr<PickerContext> &context) {
	if (context->completed)
		return;

	context->completed = true;
	context->dialog = nullptr;

	auto *result = new obsharp_file_picker_result();
	result->cancelled = true;

	LogWarning(context->what + ": dialog was destroyed before it finished; reporting cancellation");

	context->done(context->user, result);
}

} // namespace

int32_t StartFilePicker(const char *what, FilePickerKind kind, const FilePickerRequest &request,
			obsharp_file_picker_done_fn done, void *user) {
	if (!what)
		what = "file picker";

	if (!done) {
		RecordLastError(std::string(what) + ": done callback is NULL");
		return OBSHARP_ERR_INVALID_ARGUMENT;
	}

	QWidget *parent = ResolveFilePickerParent(request.parent);
	if (!parent)
		parent = QApplication::activeWindow();

	try {
		std::unique_ptr<QFileDialog> dialog(new QFileDialog(parent));

		if (!request.title.empty())
			dialog->setWindowTitle(ToQt(request.title));
		if (!request.directory.empty())
			dialog->setDirectory(ToQt(request.directory));
		if (!request.filter.empty())
			dialog->setNameFilter(ToQt(request.filter));
		if (request.showHidden)
			dialog->setFilter(dialog->filter() | QDir::Hidden | QDir::System);

		switch (kind) {
		case FilePickerKind::OpenFile:
			dialog->setAcceptMode(QFileDialog::AcceptOpen);
			dialog->setFileMode(request.allowMultiple ? QFileDialog::ExistingFiles
								 : QFileDialog::ExistingFile);
			break;

		case FilePickerKind::SaveFile:
			dialog->setAcceptMode(QFileDialog::AcceptSave);
			dialog->setFileMode(QFileDialog::AnyFile);
			if (!request.defaultSuffix.empty())
				dialog->setDefaultSuffix(ToQt(request.defaultSuffix));
			break;

		case FilePickerKind::SelectFolder:
			dialog->setAcceptMode(QFileDialog::AcceptOpen);
			dialog->setFileMode(QFileDialog::Directory);
			dialog->setOption(QFileDialog::ShowDirsOnly, true);
			break;
		}

		// 选择文件夹时不预填文件名：Qt 会把 filename 当成「要选中的子项」，
		// 在纯目录对话框里没有意义。
		if (kind != FilePickerKind::SelectFolder && !request.filename.empty())
			dialog->selectFile(ToQt(request.filename));

		// 先接好 finished / destroyed，再显示对话框：open() 虽然正常情况下
		// 只 show 就返回，但先连接能保证任何同步完成 / 销毁的路径也不会漏回调。
		auto context = std::make_shared<PickerContext>();
		context->done = done;
		context->user = user;
		context->what = what;

		QFileDialog *raw = dialog.release();
		context->dialog = raw;

		QObject::connect(raw, &QDialog::finished, raw,
				 [context](int result) { FinishPicker(context, result == QDialog::Accepted); });
		QObject::connect(raw, &QObject::destroyed, [context]() { AbortPicker(context); });

		// open() 只负责 show，不跑嵌套事件循环；用户操作发生在之后的事件循环里。
		raw->open();

		return OBSHARP_OK;
	} catch (const std::exception &exception) {
		const std::string message = std::string(what) + " failed: " + exception.what();
		RecordLastError(message);
		LogError(message);
		return OBSHARP_ERR_INTERNAL;
	} catch (...) {
		const std::string message = std::string(what) + " failed: unknown exception";
		RecordLastError(message);
		LogError(message);
		return OBSHARP_ERR_INTERNAL;
	}
}

} // namespace obsharp

// ---------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------

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

obsharp::FilePickerRequest ToRequest(const obsharp_file_picker_options &options) {
	obsharp::FilePickerRequest request;
	if (options.title)
		request.title = options.title;
	if (options.directory)
		request.directory = options.directory;
	if (options.filename)
		request.filename = options.filename;
	if (options.filter)
		request.filter = options.filter;
	if (options.default_suffix)
		request.defaultSuffix = options.default_suffix;
	request.allowMultiple = options.allow_multiple != 0;
	request.showHidden = options.show_hidden != 0;
	request.parent = options.parent_widget;
	return request;
}

int32_t StartPicker(const char *what, obsharp::FilePickerKind kind,
		    const obsharp_file_picker_options *options, obsharp_file_picker_done_fn done, void *user) {
	if (!done)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, std::string(what) + ": done callback is NULL");

	if (const int32_t result = RequireUiThread(what); result != OBSHARP_OK)
		return result;

	if (options && options->size < sizeof(obsharp_file_picker_options)) {
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT,
			    std::string(what) +
				    ": obsharp_file_picker_options.size is smaller than this build expects; "
				    "recompile the caller against a matching header");
	}

	try {
		const obsharp::FilePickerRequest request =
			options ? ToRequest(*options) : obsharp::FilePickerRequest();

		return obsharp::StartFilePicker(what, kind, request, done, user);
	} catch (const std::exception &exception) {
		return Fail(OBSHARP_ERR_INTERNAL, std::string(what) + " failed: " + exception.what());
	} catch (...) {
		return Fail(OBSHARP_ERR_INTERNAL, std::string(what) + " failed: unknown exception");
	}
}

} // namespace

extern "C" {

int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_file_picker_open(const obsharp_file_picker_options *options, obsharp_file_picker_done_fn done, void *user) {
	return StartPicker("obsharp_file_picker_open", obsharp::FilePickerKind::OpenFile, options, done, user);
}

int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_file_picker_save(const obsharp_file_picker_options *options, obsharp_file_picker_done_fn done, void *user) {
	return StartPicker("obsharp_file_picker_save", obsharp::FilePickerKind::SaveFile, options, done, user);
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_file_picker_select_folder(
	const obsharp_file_picker_options *options, obsharp_file_picker_done_fn done, void *user) {
	return StartPicker("obsharp_file_picker_select_folder", obsharp::FilePickerKind::SelectFolder, options, done, user);
}

int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_file_picker_result_cancelled(const obsharp_file_picker_result *result) {
	// NULL 当成取消：调用方拿到空结果时不用再单独处理无效指针。
	if (!result)
		return 1;

	return result->cancelled ? 1 : 0;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_file_picker_result_count(const obsharp_file_picker_result *result) {
	if (!result)
		return 0;

	return static_cast<int32_t>(result->paths.size());
}

const char *OBSHARP_BROWSER_NATIVE_CALL
obsharp_file_picker_result_path(const obsharp_file_picker_result *result, int32_t index) {
	if (!result || index < 0 || static_cast<std::size_t>(index) >= result->paths.size())
		return nullptr;

	return result->paths[static_cast<std::size_t>(index)].c_str();
}

void OBSHARP_BROWSER_NATIVE_CALL obsharp_file_picker_result_destroy(obsharp_file_picker_result *result) {
	delete result;
}

} // extern "C"
