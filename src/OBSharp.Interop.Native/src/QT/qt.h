#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QString>
#include <QtWidgets/QWidget>

#include <string>

namespace OBSharp {


  inline QString ToQt(const char* utf8)
  {
    return utf8 ? QString::fromUtf8(utf8) : QString{};
  }

  inline QString ToQt(const std::string& utf8)
  {
    return QString::fromUtf8(utf8.data(), static_cast<qsizetype>(utf8.size()));
  }

  inline QString ToQt(const QByteArray& utf8)
  {
    return QString::fromUtf8(utf8);
  }

  inline std::string ToUtf8(const QString& value)
  {
    const QByteArray bytes = value.toUtf8();
    return { bytes.constData(), static_cast<size_t>(bytes.size()) };
  }

  inline const char* BorrowedUtf8(const QString& value) {
    thread_local std::string buffer;
    buffer = ToUtf8(value);
    return buffer.c_str();
  }

  inline QWidget* AsWidget(void* widget) {
    return static_cast<QWidget*>(widget);
  }

  inline const char** BorrowedUtf8Array(const QStringList& values, size_t* size) {
    thread_local std::vector<std::string> strings;
    thread_local std::vector<const char*> pointers;

    strings.clear();
    pointers.clear();

    strings.reserve(static_cast<size_t>(values.size()));
    pointers.reserve(static_cast<size_t>(values.size()));

    for (const QString& value : values)
      strings.push_back(ToUtf8(value));

    for (const std::string& value : strings)
      pointers.push_back(value.c_str());

    if (size)
      *size = pointers.size();

    return pointers.data();
  }

  inline QStringList ToQtStringList(const char** values, size_t size)
  {
    QStringList result;

    if (!values)
      return result;

    result.reserve(static_cast<qsizetype>(size));

    for (size_t i = 0; i < size; ++i) {
      if (values[i])
        result.append(ToQt(values[i]));
    }

    return result;
  }
  
  // QWidgetList → 借用数组（thread_local 缓冲，与 qt.h 里的 BorrowedUtf8Array 同语义）
  inline QWidget** BorrowedWidgetArray(const QWidgetList& widgets, size_t* size)
  {
    thread_local std::vector<QWidget*> pointers;
    pointers.clear();
    pointers.reserve(static_cast<size_t>(widgets.size()));

    for (QWidget* w : widgets)
      pointers.push_back(w);

    if (size)
      *size = pointers.size();

    return pointers.data();
  }
}