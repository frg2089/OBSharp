#pragma once

// widgetsignals.h：接收 QCefWidget 的 title / url 信号并原样转给托管回调。
//
// 为什么必须有这个类：Qt 的信号只能连到 QObject 的接收方，而 C ABI 里传进来的是
// 裸函数指针，没有 QObject 身份，所以中间要垫一个真正的 QObject 子类 —— 这是
// 「零判断适配器」里最薄的那个：
//
//   * 它不缓存标题 / URL（拿到就转发，转完就丢）；
//   * 它不判断「值变了没有」（去重是调用方的事）；
//   * 它不解释字符串内容（QString → UTF-8 是机械转换）。
//
// 回调在创建 widget 时一次性传入（见 obsharp_qcef_create_widget），所以这个类
// 没有公开的「改回调」入口，只有拆除用的 DetachCallbacks。
//
// 连信号只能用 SIGNAL / SLOT 字符串形式（见 qcef.cpp 里的说明）：字符串槽要 moc
// 出 metaobject，所以这里带 Q_OBJECT、成员函数标成 slots。

#include "obsharp.h"

#include <QtCore/QObject>
#include <QtCore/QString>

#include "qcef.h"

namespace OBSharp::Cef
{
  class WidgetSignals : public QObject
  {
    Q_OBJECT

  public:
    // widget 同时是 QObject 父级（生命周期绑死）和回调里要带回去的信号来源。
    explicit WidgetSignals(QCefWidget* widget);

    // 拷贝一份回调。只在构造之后、连信号之前调用一次，所以不需要加锁。
    void SetCallbacks(obsharp_cef_widget_value_callback on_title_changed,
      obsharp_cef_widget_value_callback on_url_changed, void* user);

    // 清空回调（拆除用）。之后收到信号直接返回，不再回调托管侧。
    void DetachCallbacks();

  public slots:
    void OnTitleChanged(const QString& title);
    void OnUrlChanged(const QString& url);

  private:
    QCefWidget* widget_ = nullptr;
    obsharp_cef_widget_value_callback on_title_changed_ = nullptr;
    obsharp_cef_widget_value_callback on_url_changed_ = nullptr;
    void* user_ = nullptr;
  };
}
