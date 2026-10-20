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

// 创建浏览器前算好的 host 相关参数。
struct BrowserHostConfig {
	// OB# host 名；空表示没有 host（v1 行为）。
	std::string host;
	// scheme handler 用的 scheme，默认 "https"（页面因此在安全上下文里，但不做 TLS）。
	std::string scheme;
	// scheme handler 的 domain：有 host 时是 "<host>.obsharp"，否则是 options->domain。
	std::string domain;
	// Kestrel 端点（尚未归一化，交给 NormalizeEndpoint 处理）；没有端点时
	// address 为空、kind 为 Auto。
	Endpoint endpoint;
	bool hasEndpoint = false;
	// 交给 CEF 的初始 URL（有 host 时已经映射成 https://<host>.obsharp/...）。
	std::string url;
};

// 分析 obsharp_browser_options：校验 host、推导端点与 domain、映射初始 URL。
// options 为 NULL 时全部取默认值（没有 host、没有端点）。失败返回 false 并写 error。
bool ResolveBrowserHostConfig(const obsharp_browser_options *options, BrowserHostConfig &config,
			      std::string &error);

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

	// url 以 obsharp:// 开头时按本浏览器的 host 映射成 CEF 形式（见 SchemeHandler.h）。
	// host 对不上返回 false 并写 error。
	bool Load(const std::string &url, std::string &error);
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
	const std::string &Host() const { return host_; }
	const std::string &Scheme() const { return scheme_; }

	// 页面通过 POST /__obsharp/message 发来的消息落到这里（在 CEF 的 UI 线程上
	// 调用，见 SchemeHandler.h 的说明）。
	void SetMessageHandler(std::function<void(const std::string &)> handler) { onMessage_ = std::move(handler); }
	void DeliverMessage(const std::string &json);

private:
	BrowserWidget() = default;

	QCefWidgetApi *cef_ = nullptr;
	QWidget *widget_ = nullptr;
	// 吃掉 QEvent::Close 的过滤器（见 BrowserWidget.cpp）。
	QObject *closeGuard_ = nullptr;
	std::unique_ptr<BrowserEventSink> sink_;
	std::string currentUrl_; // 当前（映射后的）文档地址
	std::string host_;
	std::string scheme_ = "https";
	std::function<void(const std::string &)> onMessage_;
};

// 生成注入到页面的引导脚本：定义 window.obsharp.post()，有 host 时再补上
// window.obsharp.host / window.obsharp.url，最后拼上用户脚本。
std::string BuildStartupScript(const std::string &userScript, const std::string &host = std::string());

} // namespace obsharp
