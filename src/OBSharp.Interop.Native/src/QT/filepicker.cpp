#include "filepicker.h"

#include "qt.h"

#include <QtWidgets/QFileDialog>

namespace OBSharp::FilePicker
{
  QFileDialog* obsharp_filepicker_create(QWidget* parent) {
    return new QFileDialog(parent);
  }

  void obsharp_filepicker_show(QFileDialog* dialog, obsharp_filepicker_finished_callback callback, void* ptr) {
    QObject::disconnect(dialog, &QFileDialog::finished, nullptr, nullptr);
    QObject::connect(dialog, &QFileDialog::finished, dialog, [dialog, callback, ptr](int result) {
      callback(dialog, result, ptr);
      });
    dialog->show();
  }

  void obsharp_filepicker_reject(QFileDialog* dialog) {
    dialog->reject();
  }

  // Directory
  const char* obsharp_filepicker_get_directory(QFileDialog* dialog) {
    return BorrowedUtf8(dialog->directory().path());
  }

  void obsharp_filepicker_set_directory(QFileDialog* dialog, const char* directory) {
    dialog->setDirectory(ToQt(directory));
  }

  const char* obsharp_filepicker_get_directory_url(QFileDialog* dialog) {
    return BorrowedUtf8(dialog->directoryUrl().toString());
  }

  void obsharp_filepicker_set_directory_url(QFileDialog* dialog, const char* url) {
    dialog->setDirectoryUrl(QUrl(ToQt(url)));
  }

  // File selection
  void obsharp_filepicker_select_file(QFileDialog* dialog, const char* filename) {
    dialog->selectFile(ToQt(filename));
  }

  const char** obsharp_filepicker_get_selected_files(QFileDialog* dialog, size_t* size) {
    return BorrowedUtf8Array(dialog->selectedFiles(), size);
  }

  void obsharp_filepicker_select_url(QFileDialog* dialog, const char* url) {
    dialog->selectUrl(QUrl(ToQt(url)));
  }

  const char** obsharp_filepicker_get_selected_urls(QFileDialog* dialog, size_t* size) {
    QStringList urls;

    for (const QUrl& url : dialog->selectedUrls())
      urls.append(url.toString());

    return BorrowedUtf8Array(urls, size);
  }

  // Name filters
  void obsharp_filepicker_set_name_filter(QFileDialog* dialog, const char* filter) {
    dialog->setNameFilter(ToQt(filter));
  }

  void obsharp_filepicker_set_name_filters(QFileDialog* dialog, const char** filters, size_t size) {
    dialog->setNameFilters(ToQtStringList(filters, size));
  }

  const char** obsharp_filepicker_get_name_filters(QFileDialog* dialog, size_t* size) {
    return BorrowedUtf8Array(dialog->nameFilters(), size);
  }

  void obsharp_filepicker_select_name_filter(QFileDialog* dialog, const char* filter) {
    dialog->selectNameFilter(ToQt(filter));
  }

  const char* obsharp_filepicker_get_selected_name_filter(QFileDialog* dialog) {
    return BorrowedUtf8(dialog->selectedNameFilter());
  }

  const char* obsharp_filepicker_get_selected_mime_type_filter(QFileDialog* dialog) {
    return BorrowedUtf8(dialog->selectedMimeTypeFilter());
  }

  // MIME type filters
  void obsharp_filepicker_set_mime_type_filters(QFileDialog* dialog, const char** filters, size_t size) {
    dialog->setMimeTypeFilters(ToQtStringList(filters, size));
  }

  const char** obsharp_filepicker_get_mime_type_filters(QFileDialog* dialog, size_t* size) {
    return BorrowedUtf8Array(dialog->mimeTypeFilters(), size);
  }

  void obsharp_filepicker_select_mime_type_filter(QFileDialog* dialog, const char* filter) {
    dialog->selectMimeTypeFilter(ToQt(filter));
  }


  // QDir filters
  QDir::Filter obsharp_filepicker_get_filter(QFileDialog* dialog) {
    return static_cast<QDir::Filter>(dialog->filter().toInt());
  }

  void obsharp_filepicker_set_filter(QFileDialog* dialog, QDir::Filters filters) {
    dialog->setFilter(filters);
  }

  // View mode
  QFileDialog::ViewMode obsharp_filepicker_get_view_mode(QFileDialog* dialog) {
    return dialog->viewMode();
  }

  void obsharp_filepicker_set_view_mode(QFileDialog* dialog, QFileDialog::ViewMode mode) {
    dialog->setViewMode(mode);
  }

  // File mode
  QFileDialog::FileMode obsharp_filepicker_get_file_mode(QFileDialog* dialog) {
    return dialog->fileMode();
  }

  void obsharp_filepicker_set_file_mode(QFileDialog* dialog, QFileDialog::FileMode mode) {
    dialog->setFileMode(mode);
  }

  // Accept mode
  QFileDialog::AcceptMode obsharp_filepicker_get_accept_mode(QFileDialog* dialog) {
    return dialog->acceptMode();
  }

  void obsharp_filepicker_set_accept_mode(QFileDialog* dialog, QFileDialog::AcceptMode mode) {
    dialog->setAcceptMode(mode);
  }


  // Sidebar URLs
  void obsharp_filepicker_set_sidebar_urls(QFileDialog* dialog, const char** urls, size_t size) {
    QList<QUrl> result;
    const QStringList values = ToQtStringList(urls, size);

    for (const QString& url : values)
      result.append(QUrl(url));

    dialog->setSidebarUrls(result);
  }

  const char** obsharp_filepicker_get_sidebar_urls(QFileDialog* dialog, size_t* size) {
    QStringList urls;

    for (const QUrl& url : dialog->sidebarUrls())
      urls.append(url.toString());

    return BorrowedUtf8Array(urls, size);
  }


  // State
  const char* obsharp_filepicker_save_state(QFileDialog* dialog) {
    return BorrowedUtf8(QString::fromLatin1(dialog->saveState().toBase64()));
  }

  bool obsharp_filepicker_restore_state(QFileDialog* dialog, const char* state) {
    return dialog->restoreState(QByteArray::fromBase64(ToQt(state).toLatin1()));
  }

  // Default suffix
  void obsharp_filepicker_set_default_suffix(QFileDialog* dialog, const char* suffix) {
    dialog->setDefaultSuffix(ToQt(suffix));
  }

  const char* obsharp_filepicker_get_default_suffix(QFileDialog* dialog) {
    return BorrowedUtf8(dialog->defaultSuffix());
  }

  // History
  void obsharp_filepicker_set_history(QFileDialog* dialog, const char** paths, size_t size) {
    dialog->setHistory(ToQtStringList(paths, size));
  }

  const char** obsharp_filepicker_get_history(QFileDialog* dialog, size_t* size) {
    return BorrowedUtf8Array(dialog->history(), size);
  }

  // Labels
  void obsharp_filepicker_set_label_text(QFileDialog* dialog, int label, const char* text) {
    dialog->setLabelText(static_cast<QFileDialog::DialogLabel>(label), ToQt(text));
  }

  const char* obsharp_filepicker_get_label_text(QFileDialog* dialog, int label) {
    return BorrowedUtf8(dialog->labelText(static_cast<QFileDialog::DialogLabel>(label)));
  }

  // Supported schemes
  void obsharp_filepicker_set_supported_schemes(QFileDialog* dialog, const char** schemes, size_t size) {
    dialog->setSupportedSchemes(ToQtStringList(schemes, size));
  }

  const char** obsharp_filepicker_get_supported_schemes(QFileDialog* dialog, size_t* size) {
    return BorrowedUtf8Array(dialog->supportedSchemes(), size);
  }

  // Options
  void obsharp_filepicker_set_option(QFileDialog* dialog, QFileDialog::Option option, bool on) {
    dialog->setOption(option, on);
  }

  bool obsharp_filepicker_test_option(QFileDialog* dialog, QFileDialog::Option option) {
    return dialog->testOption(option);
  }

  void obsharp_filepicker_set_options(QFileDialog* dialog, QFileDialog::Option options) {
    dialog->setOptions(options);
  }

  QFileDialog::Option obsharp_filepicker_get_options(QFileDialog* dialog) {
    return static_cast<QFileDialog::Option>(dialog->options().toInt());
  }
}