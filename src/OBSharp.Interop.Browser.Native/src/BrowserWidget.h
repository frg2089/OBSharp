#pragma once

#include "Export.h"
#include "HostApi.h"
#include "KestrelClient.h"

#include <QObject>
#include <QString>

#include <functional>
#include <memory>
#include <string>

namespace obsharp {

// 把 QCefWidget 的 Qt 信号（titleChanged / urlChanged）转成普通 std::function。
class BrowserEventSink : public QObject {
	Q_OBJECT

public:
	using StringFn = std::function<void(const QString &)>;

	BrowserEventSink(StringFn onTitle, StringFn onUrl, QObject *parent = nullptr);

public slots:
	void OnTitleChanged(const QString &title);
	void OnUrlChanged(const QString &url);

private:
	StringFn onTitle_;
	StringFn onUrl_;
};

// 一个 CEF 浏览器 Widget。
class BrowserWidget {
public:
	// 必须在 Qt 主线程调用。
	static std::unique_ptr<BrowserWidget> Create(const obsharp_browser_options &options, BrowserPanel &panel,
						     std::string &error);

	~BrowserWidget();

	BrowserWidget(const BrowserWidget &) = delete;
	BrowserWidget &operator=(const BrowserWidget &) = delete;

	// 底层的 QWidget*；Widget 归本对象所有。
	QWidget *Widget() const { return widget_; }
	QCefWidgetApi *Cef() const { return cef_; }

	void Load(const std::string &url);
	void Reload();
	bool Zoom(int direction);
	void ExecuteScript(const std::string &script);
	void SetStartupScript(const std::string &script);
	void AllowPopups(bool allow);
	void Resize(int width, int height);
	void Move(int x, int y);
	void SetVisible(bool visible);
	void PostToPage(const std::string &json);

	const std::string &CurrentUrl() const { return currentUrl_; }

	// 页面通过 POST /__obsharp/message 发来的消息落到这里（在 CEF 的 UI 线程上
	// 调用，见 SchemeHandler.h 的说明）。
	void SetMessageHandler(std::function<void(const std::string &)> handler) { onMessage_ = std::move(handler); }
	void DeliverMessage(const std::string &json);

private:
	BrowserWidget() = default;

	QCefWidgetApi *cef_ = nullptr;
	QWidget *widget_ = nullptr;
	std::unique_ptr<BrowserEventSink> sink_;
	std::string currentUrl_;
	std::function<void(const std::string &)> onMessage_;
};

// 生成注入到页面的引导脚本：定义 window.obsharp.post()，再拼上用户脚本。
std::string BuildStartupScript(const std::string &userScript);

} // namespace obsharp
