#pragma once

#include "obsharp.h"

#include <QtWidgets/QFileDialog>

namespace OBSharp::FilePicker
{
  typedef void(*obsharp_filepicker_finished_callback)(QFileDialog*, int, void*);

  // 创建文件选择器
  OBSHARP_API QFileDialog* OBSHARP_CALL obsharp_filepicker_create(QWidget* parent);

  // 显示文件选择器
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_show(QFileDialog* dialog, obsharp_filepicker_finished_callback callback, void* ptr);
  // 关闭文件选择器
  // 触发 finished(Rejected)
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_reject(QFileDialog* dialog);

  // Directory
  OBSHARP_API const char* OBSHARP_CALL obsharp_filepicker_get_directory(QFileDialog* dialog);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_directory(QFileDialog* dialog, const char* directory);
  OBSHARP_API const char* OBSHARP_CALL obsharp_filepicker_get_directory_url(QFileDialog* dialog);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_directory_url(QFileDialog* dialog, const char* url);

  // File selection
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_select_file(QFileDialog* dialog, const char* filename);
  OBSHARP_API const char** OBSHARP_CALL obsharp_filepicker_get_selected_files(QFileDialog* dialog, size_t* size);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_select_url(QFileDialog* dialog, const char* url);
  OBSHARP_API const char** OBSHARP_CALL obsharp_filepicker_get_selected_urls(QFileDialog* dialog, size_t* size);

  // Name filters
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_name_filter(QFileDialog* dialog, const char* filter);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_name_filters(QFileDialog* dialog, const char** filters, size_t size);
  OBSHARP_API const char** OBSHARP_CALL obsharp_filepicker_get_name_filters(QFileDialog* dialog, size_t* size);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_select_name_filter(QFileDialog* dialog, const char* filter);
  OBSHARP_API const char* OBSHARP_CALL obsharp_filepicker_get_selected_name_filter(QFileDialog* dialog);
  OBSHARP_API const char* OBSHARP_CALL obsharp_filepicker_get_selected_mime_type_filter(QFileDialog* dialog);

  // MIME type filters
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_mime_type_filters(QFileDialog* dialog, const char** filters, size_t size);
  OBSHARP_API const char** OBSHARP_CALL obsharp_filepicker_get_mime_type_filters(QFileDialog* dialog, size_t* size);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_select_mime_type_filter(QFileDialog* dialog, const char* filter);

  // QDir filters
  OBSHARP_API QDir::Filter OBSHARP_CALL obsharp_filepicker_get_filter(QFileDialog*);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_filter(QFileDialog*, QDir::Filter);

  // View mode
  OBSHARP_API QFileDialog::ViewMode OBSHARP_CALL obsharp_filepicker_get_view_mode(QFileDialog*);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_view_mode(QFileDialog*, QFileDialog::ViewMode);

  // File mode
  OBSHARP_API QFileDialog::FileMode OBSHARP_CALL obsharp_filepicker_get_file_mode(QFileDialog*);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_file_mode(QFileDialog*, QFileDialog::FileMode);

  // Accept mode
  OBSHARP_API QFileDialog::AcceptMode OBSHARP_CALL obsharp_filepicker_get_accept_mode(QFileDialog*);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_accept_mode(QFileDialog*, QFileDialog::AcceptMode);

  // Sidebar URLs
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_sidebar_urls(QFileDialog* dialog, const char** urls, size_t size);
  OBSHARP_API const char** OBSHARP_CALL obsharp_filepicker_get_sidebar_urls(QFileDialog* dialog, size_t* size);

  // State
  OBSHARP_API const char* OBSHARP_CALL obsharp_filepicker_save_state(QFileDialog* dialog);
  OBSHARP_API bool OBSHARP_CALL obsharp_filepicker_restore_state(QFileDialog* dialog, const char* state);

  // Default suffix
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_default_suffix(QFileDialog* dialog, const char* suffix);
  OBSHARP_API const char* OBSHARP_CALL obsharp_filepicker_get_default_suffix(QFileDialog* dialog);

  // History
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_history(QFileDialog* dialog, const char** paths, size_t size);
  OBSHARP_API const char** OBSHARP_CALL obsharp_filepicker_get_history(QFileDialog* dialog, size_t* size);

  // Labels
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_label_text(QFileDialog*, QFileDialog::DialogLabel, const char*);
  OBSHARP_API const char* OBSHARP_CALL obsharp_filepicker_get_label_text(QFileDialog*, QFileDialog::DialogLabel);

  // Supported schemes
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_supported_schemes(QFileDialog* dialog, const char** schemes, size_t size);
  OBSHARP_API const char** OBSHARP_CALL obsharp_filepicker_get_supported_schemes(QFileDialog* dialog, size_t* size);

  // Options
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_option(QFileDialog*, QFileDialog::Option, bool);
  OBSHARP_API bool OBSHARP_CALL obsharp_filepicker_test_option(QFileDialog*, QFileDialog::Option);
  OBSHARP_API void OBSHARP_CALL obsharp_filepicker_set_options(QFileDialog*, QFileDialog::Option);
  OBSHARP_API QFileDialog::Option OBSHARP_CALL obsharp_filepicker_get_options(QFileDialog*);
}
