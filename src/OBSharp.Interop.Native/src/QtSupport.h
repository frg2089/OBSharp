#pragma once

// QtSupport.h：Qt ↔ ABI 之间的零判断转换帮手。
//
// 这里只有「类型转换」和「空指针检查」，没有任何业务判断：
//   * UTF-8（C 侧的约定）↔ QString / std::string（Qt 侧的约定）；
//   * void*（C ABI 里的不透明句柄）↔ QWidget*；
//   * 借用字符串（返回 const char* 时用的那块线程局部缓冲）。
//
// 之所以单独抽一个头：这些转换在六七个编译单元里都要用，散着写迟早会出现
// 一个地方忘了按 UTF-8 而按本地代码页解释（中文标题 / 路径立刻乱码）。

#include <QtCore/QByteArray>
#include <QtCore/QString>
#include <QtWidgets/QWidget>

#include <string>

namespace obsharp {

// UTF-8 → QString。空指针当空串（C# 那边 null 和 "" 在窗口标题 / 脚本这类参数上
// 是同一个意思，没必要分开表达）。
inline QString ToQt(const char* utf8) {
  return utf8 ? QString::fromUtf8(utf8) : QString();
}

inline QString ToQt(const std::string& utf8) {
  return QString::fromUtf8(utf8.data(), static_cast<int>(utf8.size()));
}

// QString → UTF-8 std::string。
inline std::string ToUtf8(const QString& value) {
  const QByteArray bytes = value.toUtf8();
  return std::string(bytes.constData(), static_cast<size_t>(bytes.size()));
}

// 把 QString 变成「借用指针」返回给 C#。
//
// 公开头的约定是：返回的 const char* 归库所有，下一次同类调用后失效。用一块
// thread_local 缓冲实现这一点 —— 按线程分开是因为 C# 可能从多个线程同时读
// （比如主线程取标题、日志线程取路径），共用一块全局缓冲会互相踩。
//
// 注意：**不要**把它和别的借用指针混着用（同一个线程上连续调两次，前一个就失效了）。
inline const char* BorrowedUtf8(const QString& value) {
  thread_local std::string buffer;
  buffer = ToUtf8(value);
  return buffer.c_str();
}

// void*（C ABI 的 QWidget 句柄）→ QWidget*。
inline QWidget* AsWidget(void* widget) {
  return static_cast<QWidget*>(widget);
}

} // namespace obsharp
