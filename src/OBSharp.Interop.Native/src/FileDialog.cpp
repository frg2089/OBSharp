#include "Export.h"

#include "Log.h"
#include "QtSupport.h"

#include <QtCore/QDir>
#include <QtCore/QPointer>
#include <QtCore/QStringList>
#include <QtWidgets/QApplication>
#include <QtWidgets/QDialog>
#include <QtWidgets/QFileDialog>
#include <QtWidgets/QWidget>

#include <cstdint>

// FileDialog.cpp：QFileDialog 的转发 + 一次性异步完成回调。
//
// 为什么是异步的：用 QFileDialog::open() 显示窗口模态对话框——它只把窗口 show 出来
// 就返回，用户交互发生在之后的事件循环里。所以启动函数立刻返回，用户选完 / 取消后
// 我们通过回调通知 C#。这跟 exec() 不同：exec() 会跑一个嵌套事件循环，从 C#
// 跨 P/Invoke 调进来时嵌套事件循环极容易踩到重入问题。
//
// 这个文件**不含**任何「对话框该显示什么模式 / 什么过滤器」的业务判断：accept mode、
// file mode、是否显示隐藏文件、过滤器字符串，全由 C# 逐个 setter 设好再 open。

namespace {

  // 一次对话框的完成状态。
  //
  // 用 QObject 是因为要连 QDialog::finished 和 QObject::destroyed 两个信号。父级设成
  // 对话框：正常情况下对话框析构时它跟着走。completed 标志保证回调**恰好一次**——
  // finished 和 destroyed 都可能触发（用户确认后对话框随后被销毁），谁先到谁负责，
  // 后到的那个看到 completed 直接返回。
  class PickerContext : public QObject {
  public:
    PickerContext(obsharp_file_dialog_done_fn done, void* user, QFileDialog* dialog)
      : QObject(dialog), done_(done), user_(user), dialog_(dialog) {
    }

    // 完成一次。accepted 为 true 表示用户按了确认。
    void Finish(bool accepted) {
      if (completed_)
        return;

      completed_ = true;

      QFileDialog* dialog = dialog_.data();
      dialog_ = nullptr;

      // 「按了确认但一个路径都没选」当取消处理：这不是错误，用户只是没给出有效
      // 选择（有些平台的目录对话框会出现这种组合）。
      bool cancelled = !accepted;
      if (accepted && dialog) {
        const QStringList selected = dialog->selectedFiles();
        if (selected.isEmpty())
          cancelled = true;
      }

      // 先把回调发出去再销毁对话框：公开头的约定是「回调里 dialog 仍然有效，
      // C# 可以在里面调 selected_count / selected_path 读结果」。
      done_(user_, dialog, cancelled);

      // 之后对话框归本库处置（open 成功时所有权已经转给我们），推迟到事件循环里删，
      // 避免在信号处理过程中直接 delete 掉正在派发的对象。
      if (dialog)
        dialog->deleteLater();
    }

    // 对话框被外部销毁（例如父窗口先没了）时 QFileDialog 不一定会发 finished。
    // 这里保证回调仍然只来一次；此时不再碰正在析构的 dialog（dialog_ 已经失效）。
    void Abort() {
      if (completed_)
        return;

      completed_ = true;
      dialog_ = nullptr;

      obsharp::LogF(OBSHARP_LOG_WARNING,
        "the file dialog was destroyed before it finished; reporting cancellation");

      done_(user_, nullptr, true);
    }

  private:
    obsharp_file_dialog_done_fn done_ = nullptr;
    void* user_ = nullptr;
    QPointer<QFileDialog> dialog_;
    bool completed_ = false;
  };

  QFileDialog* DialogOrNull(void* dialog) {
    return static_cast<QFileDialog*>(dialog);
  }

} // namespace

extern "C" {

  void* OBSHARP_NATIVE_CALL obsharp_file_dialog_create(void* parent) {
    // parent 可以为 NULL（没有模态父级）。C# 传什么由它决定——包括「先把 HWND
    // 转成 QWidget*」这种判断，也在 C# 那边（见它调 obsharp_widget_find）。
    return new QFileDialog(static_cast<QWidget*>(parent));
  }

  void* OBSHARP_NATIVE_CALL obsharp_active_window(void) {
    // 没有活动窗口时返回 nullptr，C# 据此决定不给对话框父级。
    return QApplication::activeWindow();
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_title(void* dialog, const char* title) {
    if (QFileDialog* target = DialogOrNull(dialog))
      target->setWindowTitle(obsharp::ToQt(title));
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_directory(void* dialog, const char* directory) {
    if (QFileDialog* target = DialogOrNull(dialog))
      target->setDirectory(obsharp::ToQt(directory));
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_name_filter(void* dialog, const char* filter) {
    // 过滤器字符串是 Qt 的写法（"图片 (*.png *.jpg);;所有文件 (*)"），本库不解析、
    // 不校验 —— 拼装它在 C# 的 FilePicker.BuildFilter 里。
    if (QFileDialog* target = DialogOrNull(dialog))
      target->setNameFilter(obsharp::ToQt(filter));
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_show_hidden(void* dialog, bool show) {
    QFileDialog* target = DialogOrNull(dialog);
    if (!target)
      return;

    if (show)
      target->setFilter(target->filter() | QDir::Hidden | QDir::System);
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_accept_mode(void* dialog,
    obsharp_file_dialog_accept_mode accept_mode) {
    QFileDialog* target = DialogOrNull(dialog);
    if (!target)
      return;

    switch (accept_mode) {
    case OBSHARP_FILE_DIALOG_ACCEPT_OPEN:
      target->setAcceptMode(QFileDialog::AcceptOpen);
      break;
    case OBSHARP_FILE_DIALOG_ACCEPT_SAVE:
      target->setAcceptMode(QFileDialog::AcceptSave);
      break;
    }
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_file_mode(void* dialog,
    obsharp_file_dialog_file_mode file_mode) {
    QFileDialog* target = DialogOrNull(dialog);
    if (!target)
      return;

    switch (file_mode) {
    case OBSHARP_FILE_DIALOG_FILE_EXISTING_FILE:
      target->setFileMode(QFileDialog::ExistingFile);
      break;
    case OBSHARP_FILE_DIALOG_FILE_EXISTING_FILES:
      target->setFileMode(QFileDialog::ExistingFiles);
      break;
    case OBSHARP_FILE_DIALOG_FILE_ANY_FILE:
      target->setFileMode(QFileDialog::AnyFile);
      break;
    case OBSHARP_FILE_DIALOG_FILE_DIRECTORY:
      target->setFileMode(QFileDialog::Directory);
      break;
    }
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_default_suffix(void* dialog, const char* suffix) {
    if (QFileDialog* target = DialogOrNull(dialog))
      target->setDefaultSuffix(obsharp::ToQt(suffix));
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_show_dirs_only(void* dialog, bool value) {
    if (QFileDialog* target = DialogOrNull(dialog))
      target->setOption(QFileDialog::ShowDirsOnly, value);
  }

  void OBSHARP_NATIVE_CALL obsharp_file_dialog_select_file(void* dialog, const char* filename) {
    if (QFileDialog* target = DialogOrNull(dialog))
      target->selectFile(obsharp::ToQt(filename));
  }

  int32_t OBSHARP_NATIVE_CALL
    obsharp_file_dialog_open(void* dialog, obsharp_file_dialog_done_fn done, void* user) {
    QFileDialog* target = DialogOrNull(dialog);
    if (!target)
      return OBSHARP_ERR_INVALID_ARGUMENT;

    if (!done)
      return OBSHARP_ERR_INVALID_ARGUMENT;

    // 上下文只建一次：重复 open 同一个对话框是调用方的问题，但也不该出现两个上下文
    // 各回调一次。
    auto* context = new PickerContext(done, user, target);

    // **先连接再显示**：open() 正常情况下只 show 就返回，但先连上能保证任何同步
    // 完成 / 立刻销毁的路径也不会漏掉回调。旧实现（FilePicker.cpp）的注释就是
    // 因为这个理由。
    QObject::connect(target, &QDialog::finished, context, [context](int result) {
      context->Finish(result == QDialog::Accepted);
      });

    QObject::connect(target, &QObject::destroyed, context, [context]() {
      context->Abort();
      });

    // open() 只负责 show，不跑嵌套事件循环；用户操作发生在之后的事件循环里。
    target->open();

    obsharp::LogF(OBSHARP_LOG_DEBUG, "file dialog opened (0x%p)", dialog);
    return OBSHARP_OK;
  }

  int32_t OBSHARP_NATIVE_CALL obsharp_file_dialog_selected_count(void* dialog) {
    QFileDialog* target = DialogOrNull(dialog);
    return target ? static_cast<int32_t>(target->selectedFiles().size()) : 0;
  }

  const char* OBSHARP_NATIVE_CALL obsharp_file_dialog_selected_path(void* dialog, int32_t index) {
    QFileDialog* target = DialogOrNull(dialog);
    if (!target || index < 0)
      return nullptr;

    const QStringList selected = target->selectedFiles();
    if (static_cast<int32_t>(selected.size()) <= index)
      return nullptr;

    // 借用指针：走 thread_local 缓冲（见 QtSupport.h 的 BorrowedUtf8）。C# 必须在
    // 同一次回调里读完——它确实是这么用的（读一条立刻转成托管字符串）。
    return obsharp::BorrowedUtf8(selected.at(index));
  }

} // extern "C"
