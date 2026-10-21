#pragma once

// CefWidgetSignals.h：接收 QCefWidget 的 Qt 信号并原样转给 C# 函数指针。
//
// 为什么必须有这个类：Qt 的信号只能连到 QObject 的槽（或者 lambda 的接收方
// context）。C ABI 里传进来的是裸函数指针，没有 QObject 身份，所以中间要垫一个
// 真正的 QObject 子类 —— 这就是「零判断适配器」里最薄的那个：
//
//   * 它不缓存标题 / URL（拿到就转发，转完就丢）；
//   * 它不判断「值变了没有」（去重是 C# 的事）；
//   * 它不解释字符串内容（QString → UTF-8 是机械转换）。
//
// 回调在创建 widget 时一次性传入（见 obsharp_qcef_create_widget），所以这个类没有
// 公开的「改回调」入口。
//
// 用 Q_OBJECT 是因为要 moc 出槽函数，好让 SIGNAL/SLOT 按名字连接 —— 我们拿不到
// QCefWidget 的头文件，无法用「成员函数指针」的方式 connect，只能走字符串形式。

#include "Export.h"

#include <QtCore/QObject>
#include <QtCore/QString>

namespace obsharp {

class CefWidgetSignals : public QObject {
  Q_OBJECT

public:
  explicit CefWidgetSignals(QObject* parent = nullptr);

  // 拷贝一份回调表。只在构造之后、连信号之前调用一次，所以不需要加锁。
  void SetCallbacks(const obsharp_cef_widget_callbacks& callbacks);

  // 清空回调表（拆除用）。之后槽函数直接返回，不再回调 C#。
  void DetachCallbacks() { callbacks_ = obsharp_cef_widget_callbacks{}; }

public slots:
  void OnTitleChanged(const QString& title);
  void OnUrlChanged(const QString& url);

private:
  obsharp_cef_widget_callbacks callbacks_{};
};

} // namespace obsharp
