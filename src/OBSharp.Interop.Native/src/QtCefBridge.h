#pragma once

// QtCefBridge.h：obs-browser 面板接口（QCef / QCefWidget）的**独立声明** + 一行式转发。
//
// 为什么自己声明一遍
// ------------------
// 这里刻意不 include obs-browser 的 panel/browser-panel.hpp：
//   1) 那是 GPL-2.0 的头，本项目是 MIT；
//   2) obs-browser 是 obs-studio 的子模块，构建时未必被检出。
//
// 只要虚函数的声明顺序一致，vtable 布局就一致。C++ 的多态调用本质上就是
// 「this + 第 N 个槽位」，编译器不知道也不关心基类的真实名字。
//
// 槽位对齐的两条硬约束（写错任何一条都是「调错函数」或「调进垃圾地址」）：
//   * QCef：析构函数之后有 8 个虚函数，顺序必须是
//       init_browser / initialized / wait_for_browser_init / create_widget /
//       create_cookie_manager / get_cookie_path / add_popup_whitelist_url /
//       add_force_popup_url
//     后四个我们不用，但**必须占住槽位**，否则 create_widget 的索引就错位了。
//   * QCefWidgetApi：QWidget 的虚槽位由 Qt 头保证一致，它自己只**追加** 7 个：
//       setURL / setStartupScript / allowAllPopups / closeBrowser /
//       reloadPage / zoomPage / executeJavaScript
//
// Q_OBJECT 里的 metaObject / qt_metacast / qt_metacall 覆盖的是 QObject 已有的
// 槽位，不新增槽位，所以这里不带 Q_OBJECT 也不影响布局。
//
// 版本不匹配时由 C# 用 obs_browser_qcef_version_export() 判断（属于业务判断，
// 不在本库做）。

#include <QtWidgets/QWidget>

#include <string>

// QCefCookieManager 只当不透明指针用：我们从不解引用它，也不知道它里面有什么。
class QCefCookieManager;

namespace obsharp {

// obs-browser 的 QCef 面板接口。
//
// 真实签名里 create_widget 返回 QCefWidget*；QCefWidget 单继承自 QWidget，
// 地址相同，这里写成 QWidget* 只影响我们这边的静态类型，不影响 ABI。
struct QCef {
  virtual ~QCef();

  virtual bool init_browser(void) = 0;
  virtual bool initialized(void) = 0;
  virtual bool wait_for_browser_init(void) = 0;

  virtual QWidget* create_widget(QWidget* parent, const std::string& url,
    QCefCookieManager* cookie_manager) = 0;

  // 下面四个本库用不到，但必须占住槽位（见文件头的说明）。
  virtual QCefCookieManager* create_cookie_manager(const std::string& storage_path,
    bool persist_session_cookies) = 0;
  virtual void* get_cookie_path(const std::string& storage_path) = 0;
  virtual void add_popup_whitelist_url(const std::string& url, QObject* obj) = 0;
  virtual void add_force_popup_url(const std::string& url, QObject* obj) = 0;
};

// QCefWidget 在 QWidget 之后追加的虚函数。
//
// 注意 zoomPage 的真实签名是 bool zoomPage(int direction)：< 0 缩小、> 0 放大、
// == 0 复位。我们只转发，不解释方向值。
class QCefWidgetApi : public QWidget {
public:
  virtual void setURL(const std::string& url) = 0;
  virtual void setStartupScript(const std::string& script) = 0;
  virtual void allowAllPopups(bool allow) = 0;
  virtual void closeBrowser() = 0;
  virtual void reloadPage() = 0;
  virtual bool zoomPage(int direction) = 0;
  virtual void executeJavaScript(const std::string& script) = 0;
};

// 把 C ABI 的 void* 句柄解释成上述接口。
//
// 这两个转换本身就只是 static_cast —— 句柄在 C ABI 里是 void*，这里恢复静态类型，
// 不做任何合法性判断（对象到底是不是 QCef，只有 C# 知道）。
inline QCef* AsCef(void* cef) {
  return static_cast<QCef*>(cef);
}

inline QCefWidgetApi* AsCefWidget(void* widget) {
  return static_cast<QCefWidgetApi*>(widget);
}

} // namespace obsharp
