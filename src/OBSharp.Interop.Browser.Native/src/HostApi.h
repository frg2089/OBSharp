#pragma once

#include <QtWidgets/QWidget>

#include <string>

namespace obsharp {

// ---------------------------------------------------------------------------
// obs-browser 面板接口的独立声明（ABI 兼容）
// ---------------------------------------------------------------------------
//
// 这里刻意不 include obs-browser 的 panel/browser-panel.hpp：
//   1) 那是 GPL-2.0 的头，本项目是 MIT；
//   2) obs-browser 是 obs-studio 的子模块，未必被检出。
//
// 只要虚函数的声明顺序一致，vtable 布局就一致，把 QCefWidget* 当 QWidget* 用
// 也一样（单继承、无虚基类，地址相同）。Q_OBJECT 里的
// metaObject/qt_metacast/qt_metacall 覆盖的是 QObject 已有的槽位，不新增槽位，
// 所以这里不带 Q_OBJECT 也不影响布局。
//
// 版本不匹配时用 obs_browser_qcef_version() 判断（见 BrowserPanel::version）。

class QCefCookieManager; // 只当不透明指针用，具体方法不碰。

struct QCef {
	virtual ~QCef();

	virtual bool init_browser(void) = 0;
	virtual bool initialized(void) = 0;
	virtual bool wait_for_browser_init(void) = 0;

	// 真实签名返回 QCefWidget*；QCefWidget 单继承自 QWidget，地址相同，
	// 这里写成 QWidget* 只影响我们这边的静态类型，不影响 ABI。
	virtual QWidget *create_widget(QWidget *parent, const std::string &url,
				       QCefCookieManager *cookie_manager) = 0;

	// 下面几个用不到，但必须占住槽位，否则 create_widget 的索引会对不上。
	virtual QCefCookieManager *create_cookie_manager(const std::string &storage_path,
							 bool persist_session_cookies = false) = 0;
	virtual void *get_cookie_path(const std::string &storage_path) = 0;
	virtual void add_popup_whitelist_url(const std::string &url, QObject *obj) = 0;
	virtual void add_force_popup_url(const std::string &url, QObject *obj) = 0;
};

// QCefWidget 的额外虚函数（QWidget 的那些虚槽位由 Qt 头保证一致）。
class QCefWidgetApi : public QWidget {
public:
	virtual void setURL(const std::string &url) = 0;
	virtual void setStartupScript(const std::string &script) = 0;
	virtual void allowAllPopups(bool allow) = 0;
	virtual void closeBrowser() = 0;
	virtual void reloadPage() = 0;
	virtual bool zoomPage(int direction) = 0;
	virtual void executeJavaScript(const std::string &script) = 0;
};

// ---------------------------------------------------------------------------

struct BrowserPanel {
	QCef *cef = nullptr;
	int version = 0;

	bool IsValid() const { return cef != nullptr; }
};

// 在宿主进程里解析 obs-browser 模块并拿到 QCef 面板接口。
// 成功返回 true；否则返回 false 并把原因写进 error。
bool LoadBrowserPanel(BrowserPanel &panel, std::string &error);

// 判断当前宿主环境是否支持浏览器面板（Linux/Wayland 上 obs-browser 自己会关掉）。
bool IsBrowserPanelSupported(std::string &reason);

} // namespace obsharp
