#pragma once

// FilePicker.h：把 Qt 的 QFileDialog 包成「异步返回路径」的三种对话框
// （打开文件 / 保存文件 / 选择文件夹）。
//
// 跟 Window.h 一样，这一层不碰 OBS / CEF，只用 Qt。C ABI 的实现在
// FilePicker.cpp 里，跟内部逻辑放在同一个编译单元。
//
// 线程：QFileDialog 是 Qt Widget，启动函数必须在 Qt 主线程上调用。它用 open()
// 把窗口 show 出来后立即返回；done 会在同一个线程上、对话框结束时被调用一次。

#include "Export.h"

#include <cstdint>
#include <string>

namespace obsharp {

class QWidget;

enum class FilePickerKind {
	OpenFile,
	SaveFile,
	SelectFolder,
};

// C ABI 参数翻译之后的内部形式：字符串已经拷好，不依赖调用方的缓冲区。
struct FilePickerRequest {
	std::string title;
	std::string directory;
	std::string filename;
	std::string filter;
	std::string defaultSuffix;
	bool allowMultiple = false;
	bool showHidden = false;
	// QWidget* 或（Windows 上）HWND，见 ResolveFilePickerParent()。
	void *parent = nullptr;
};

// 显示对话框并立即返回。必须在 Qt 主线程上调用。
//
// 返回 OBSHARP_OK 表示对话框已经 show 出来，之后 done(user, result) 会在 Qt
// 主线程上被调用恰好一次；返回其它错误码时 done 不会被调用，原因写进
// obsharp::RecordLastError()。
//
// done 必须非 NULL；user 原样传给回调，可以是 NULL。result 归回调方所有，用
// obsharp_file_picker_result_destroy() 释放。
int32_t StartFilePicker(const char *what, FilePickerKind kind, const FilePickerRequest &request,
			obsharp_file_picker_done_fn done, void *user);

// 把 parent 解释成 QWidget*：Windows 上先按 HWND 找，找不到再当成 QWidget*；
// 其它平台直接当成 QWidget*。parent 为 NULL 时返回 nullptr。
QWidget *ResolveFilePickerParent(void *parent);

} // namespace obsharp
