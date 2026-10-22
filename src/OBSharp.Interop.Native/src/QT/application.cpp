#include "application.h"

#include "qt.h"

#include <QtWidgets/QApplication>
#include <QtGui/QFont>

#include <vector>

namespace OBSharp::Application
{
  // ===== 事件循环 =====
  int obsharp_application_exec() {
    return QApplication::exec();
  }

  void obsharp_application_exit(int return_code) {
    QApplication::exit(return_code);
  }

  void obsharp_application_quit() {
    QApplication::quit();
  }

  // ===== 窗口查询 =====
  QWidget* obsharp_application_active_window() {
    return QApplication::activeWindow();
  }

  QWidget* obsharp_application_active_modal_widget() {
    return QApplication::activeModalWidget();
  }

  QWidget* obsharp_application_active_popup_widget() {
    return QApplication::activePopupWidget();
  }

  QWidget* obsharp_application_focus_widget() {
    return QApplication::focusWidget();
  }

  QWidget* obsharp_application_widget_at(int x, int y) {
    return QApplication::widgetAt(x, y);
  }

  QWidget* obsharp_application_top_level_at(int x, int y) {
    return QApplication::topLevelAt(x, y);
  }

  // ===== 窗口列表 =====
  QWidget** obsharp_application_all_widgets(size_t* size) {
    return BorrowedWidgetArray(QApplication::allWidgets(), size);
  }

  QWidget** obsharp_application_top_level_widgets(size_t* size) {
    return BorrowedWidgetArray(QApplication::topLevelWidgets(), size);
  }

  // ===== 关闭 =====
  void obsharp_application_close_all_windows() {
    QApplication::closeAllWindows();
  }

  // ===== 样式表（实例方法，需走 qApp）=====
  const char* obsharp_application_get_style_sheet() {
    return BorrowedUtf8(qApp->styleSheet());
  }

  void obsharp_application_set_style_sheet(const char* sheet) {
    qApp->setStyleSheet(ToQt(sheet));
  }

  // ===== 字体 =====
  const char* obsharp_application_get_font_family() {
    return BorrowedUtf8(QApplication::font().family());
  }

  void obsharp_application_set_font_family(const char* family) {
    QFont font = QApplication::font();
    font.setFamily(ToQt(family));
    QApplication::setFont(font);
  }

  int obsharp_application_get_font_point_size() {
    return QApplication::font().pointSize();
  }

  void obsharp_application_set_font_point_size(int size) {
    QFont font = QApplication::font();
    font.setPointSize(size);
    QApplication::setFont(font);
  }

  bool obsharp_application_get_font_bold() {
    return QApplication::font().bold();
  }

  void obsharp_application_set_font_bold(bool bold) {
    QFont font = QApplication::font();
    font.setBold(bold);
    QApplication::setFont(font);
  }

  bool obsharp_application_get_font_italic() {
    return QApplication::font().italic();
  }

  void obsharp_application_set_font_italic(bool italic) {
    QFont font = QApplication::font();
    font.setItalic(italic);
    QApplication::setFont(font);
  }

  // ===== 交互参数 =====
  int obsharp_application_get_double_click_interval() {
    return QApplication::doubleClickInterval();
  }

  void obsharp_application_set_double_click_interval(int ms) {
    QApplication::setDoubleClickInterval(ms);
  }

  int obsharp_application_get_cursor_flash_time() {
    return QApplication::cursorFlashTime();
  }

  void obsharp_application_set_cursor_flash_time(int ms) {
    QApplication::setCursorFlashTime(ms);
  }

  int obsharp_application_get_start_drag_time() {
    return QApplication::startDragTime();
  }

  void obsharp_application_set_start_drag_time(int ms) {
    QApplication::setStartDragTime(ms);
  }

  int obsharp_application_get_start_drag_distance() {
    return QApplication::startDragDistance();
  }

  void obsharp_application_set_start_drag_distance(int px) {
    QApplication::setStartDragDistance(px);
  }

#if QT_CONFIG(wheelevent)
  int obsharp_application_get_wheel_scroll_lines() {
    return QApplication::wheelScrollLines();
  }

  void obsharp_application_set_wheel_scroll_lines(int lines) {
    QApplication::setWheelScrollLines(lines);
  }
#else
  int obsharp_application_get_wheel_scroll_lines() { return 0; }
  void obsharp_application_set_wheel_scroll_lines(int) {}
#endif

  int obsharp_application_get_keyboard_input_interval() {
    return QApplication::keyboardInputInterval();
  }

  void obsharp_application_set_keyboard_input_interval(int ms) {
    QApplication::setKeyboardInputInterval(ms);
  }

  // ===== 提示 =====
  void obsharp_application_beep() {
    QApplication::beep();
  }

  void obsharp_application_alert(QWidget* widget, int duration_ms) {
    if (!widget) return;
    QApplication::alert(widget, duration_ms);
  }

  void obsharp_application_about_qt() {
    QApplication::aboutQt();
  }
}