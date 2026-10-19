#include "BrowserWidget.h"

#include "Log.h"
#include "SchemeHandler.h"

#include <QtCore/QObject>
#include <QtGui/QColor>
#include <QtGui/QPalette>
#include <QtWidgets/QWidget>

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <utility>

namespace obsharp {
namespace {

// 引导脚本：定义 window.obsharp.post()，把消息发到我们自己的 scheme handler
// （POST /__obsharp/message，见 SchemeHandler.cpp 的 kMessageTarget）。
//
// obs-browser 的面板接口（BROWSER_PANEL_VERSION 3）只暴露 titleChanged /
// urlChanged 两个信号，拿不到 CefLoadHandler / CefDisplayHandler 的回调，所以
// 「加载结束」和「console 消息」这两类事件也走同一条通道：带 "$obsharp" 键的
// JSON 是控制消息（native 侧解析后派发），其余原样交给 on_message。
//
// 有 host 时（OB# 的 obsharp:// 协议）CEF 里实际是 https://<host>.obsharp/，
// 所以脚本里另外给出 window.obsharp.host 与 window.obsharp.url（规范地址），
// 让页面知道插件 API 那一侧的写法。__host__ 这一行由 BuildStartupScript() 替换。
constexpr const char *kBootstrapScript = R"JS(
(function () {
  if (window.obsharp && window.obsharp.__bootstrap) { return; }
  var api = window.obsharp || (window.obsharp = {});
  api.__bootstrap = true;
  __host__
  function send(value) {
    try {
      return fetch('/__obsharp/message', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(value === undefined ? null : value)
      });
    } catch (error) {
      return Promise.reject(error);
    }
  }

  function control(kind, extra) {
    var payload = { $obsharp: kind };
    if (extra) {
      for (var key in extra) {
        if (Object.prototype.hasOwnProperty.call(extra, key)) { payload[key] = extra[key]; }
      }
    }
    return send(payload);
  }

  function stringify(value) {
    if (typeof value === 'string') { return value; }
    try { return JSON.stringify(value); } catch (error) { return String(value); }
  }

  api.post = function (data) { return send(data); };
  api.control = control;

  if (!api.__consolePatched && window.console) {
    api.__consolePatched = true;
    var levels = { debug: 0, log: 1, info: 1, warn: 2, error: 3, assert: 3 };
    Object.keys(levels).forEach(function (name) {
      var original = console[name];
      if (typeof original !== 'function') { return; }
      console[name] = function () {
        try {
          var parts = [];
          for (var i = 0; i < arguments.length; i++) { parts.push(stringify(arguments[i])); }
          control('console', { level: levels[name], message: parts.join(' '), source: String(location.href), line: 0 });
        } catch (error) { /* 忽略 */ }
        return original.apply(console, arguments);
      };
    });
  }

  function reportLoad() {
    window.removeEventListener('load', reportLoad);
    try { control('load-end', { url: String(location.href) }); } catch (error) { /* 忽略 */ }
  }

  if (document.readyState === 'complete') {
    reportLoad();
  } else {
    window.addEventListener('load', reportLoad);
  }
})();
)JS";

QWidget *ResolveParent(void *parent) {
	if (!parent)
		return nullptr;

#ifdef Q_OS_WIN
	// Windows 上允许直接传 HWND：OBS 的 obs_frontend_get_main_window_handle()
	// 给的就是它。QWidget::find 能找到对应的顶层窗口。
	if (QWidget *found = QWidget::find(reinterpret_cast<WId>(parent)))
		return found;
#endif

	return reinterpret_cast<QWidget *>(parent);
}

} // namespace

// ---- host 参数解析 ---------------------------------------------------------

namespace {

// 安全读一个可选字段：只有整个字段落在调用方声明的 size 里才读，否则当 NULL /
// 0。老调用方用旧头编译出来时 size 更小（host 这个字段根本不存在），窗口 options
// 里嵌的 browser 还允许 size = 0 表示「全用默认值」——这两种情况都不能真去读。
template <typename T> T ReadOptionField(const obsharp_browser_options *options, size_t offset, T fallback) {
	if (!options || options->size < offset + sizeof(T))
		return fallback;

	const char *base = reinterpret_cast<const char *>(options);
	T value;
	std::memcpy(&value, base + offset, sizeof(T));
	return value;
}

const char *ReadOptionString(const obsharp_browser_options *options, size_t offset) {
	return ReadOptionField<const char *>(options, offset, nullptr);
}

const char *NonEmpty(const char *value) {
	return (value && *value) ? value : nullptr;
}

} // namespace

bool ResolveBrowserHostConfig(const obsharp_browser_options *options, BrowserHostConfig &config,
			      std::string &error) {
	config = BrowserHostConfig();
	// 默认 https：页面因此落在安全上下文里（crypto.subtle / serviceWorker /
	// clipboard 这些只在安全上下文开放的 API 才能用），但**并不做 TLS** ——
	// https 的请求由本库的 scheme handler 接住，直接转给 IPC 端点，不走网络栈、
	// 也不握手。调用方仍可以用 options->scheme 覆盖。
	config.scheme = "https";
	config.domain = "obsharp";

	if (!options) {
		config.url = "https://obsharp/";
		return true;
	}

	if (const char *scheme = NonEmpty(ReadOptionString(options, offsetof(obsharp_browser_options, scheme))))
		config.scheme = scheme;

	const char *hostField = NonEmpty(ReadOptionString(options, offsetof(obsharp_browser_options, host)));
	const std::string declaredHost = hostField ? hostField : std::string();
	const char *urlField = NonEmpty(ReadOptionString(options, offsetof(obsharp_browser_options, url)));

	// host 从两处来：options->host，或者 url 里的 obsharp://<host>/...
	// 两个都给了就必须一致（spec §2.1 第 1 条），否则是参数错误。
	std::string urlHost;
	std::string urlRest;
	if (urlField && HasObsharpScheme(urlField) && !SplitObsharpUrl(urlField, urlHost, urlRest)) {
		error = "malformed obsharp URL: " + std::string(urlField);
		return false;
	}

	std::string host = declaredHost;
	if (host.empty())
		host = urlHost;
	else if (!urlHost.empty() && urlHost != declaredHost) {
		error = "url host \"" + urlHost + "\" does not match options->host \"" + declaredHost + "\"";
		return false;
	}

	if (!host.empty()) {
		if (!IsValidHostName(host)) {
			error = "invalid host name \"" + host +
				"\": expected ^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$";
			return false;
		}

		config.host = host;
		config.domain = HostDomain(host);
	} else if (const char *domain =
			   NonEmpty(ReadOptionString(options, offsetof(obsharp_browser_options, domain)))) {
		// 没有 host 时沿用 v1 的 scheme + domain（domain 默认 "obsharp"）。
		config.domain = domain;
	}

	// 端点：调用方显式给了就用它，否则有 host 时按平台从 host 推导；两者都没有
	// 就不注册 scheme handler（浏览器按普通网络访问 url，也就是 v1 行为）。
	const char *endpointField =
		NonEmpty(ReadOptionString(options, offsetof(obsharp_browser_options, endpoint)));
	if (endpointField) {
		config.endpoint.address = endpointField;
		config.endpoint.kind = static_cast<EndpointKind>(
			ReadOptionField<int32_t>(options, offsetof(obsharp_browser_options, endpoint_kind), 0));
		config.hasEndpoint = true;
	} else if (!config.host.empty()) {
		config.endpoint.address = DefaultEndpointForHost(config.host);
		// 从 host 推导出来的端点本来就是平台默认的那种，所以强制 Auto，
		// 免得调用方顺手填了个 UnixSocket 把它带偏。
		config.endpoint.kind = EndpointKind::Auto;
		config.hasEndpoint = true;
	}

	// 交给 CEF 的初始 URL：url 为空时按 host / scheme+domain 推默认地址；给了
	// url 就把 obsharp://<host>/... 映射成 https://<host>.obsharp/...
	if (!urlField) {
		config.url = config.host.empty() ? config.scheme + "://" + config.domain + "/"
						 : MapToCefUrl(config.scheme, config.host, std::string());
		return true;
	}

	if (HasObsharpScheme(urlField)) {
		config.url = MapToCefUrl(config.scheme, config.host, urlRest);
		return true;
	}

	// 普通地址：原样交给 CEF（v1 行为）。
	config.url = urlField;
	return true;
}

BrowserEventSink::BrowserEventSink(StringFn onTitle, StringFn onUrl, QObject *parent)
	: QObject(parent), onTitle_(std::move(onTitle)), onUrl_(std::move(onUrl)) {}

void BrowserEventSink::OnTitleChanged(const QString &title) {
	if (onTitle_)
		onTitle_(title);
}

void BrowserEventSink::OnUrlChanged(const QString &url) {
	if (onUrl_)
		onUrl_(url);
}

std::string BuildStartupScript(const std::string &userScript, const std::string &host) {
	std::string script = kBootstrapScript;

	// __host__ 换成 host 相关的几行。没 host 时整行删掉（v1 行为）。
	std::string hostBlock;
	if (!host.empty()) {
		hostBlock += "\n  api.host = \"";
		hostBlock += host;
		hostBlock += "\";\n  api.url = \"obsharp://";
		hostBlock += host;
		hostBlock += "/\";";
	}

	const std::string placeholder = "  __host__";
	size_t position = script.find(placeholder);
	if (position != std::string::npos) {
		if (hostBlock.empty())
			script.erase(position, placeholder.size());
		else
			script.replace(position, placeholder.size(), hostBlock);
	}

	if (!userScript.empty()) {
		script += "\n";
		script += userScript;
	}

	return script;
}

// 把调用方给的结构体裁成「本库读得到的部分」：size 为 0（窗口 options 里嵌的
// browser 允许这样写，表示全用默认值）或者比本库的头小时，先把整份清零，再只
// 拷进落在 size 里的字段。之后 Create() 内部就可以放心当完整结构体用。
obsharp_browser_options SanitizeBrowserOptions(const obsharp_browser_options &options) {
	obsharp_browser_options safe{};
	safe.size = sizeof(obsharp_browser_options);

	const size_t declared = options.size;
	if (declared >= sizeof(obsharp_browser_options)) {
		safe = options;
		return safe;
	}

	std::memcpy(&safe, &options, declared);
	safe.size = sizeof(obsharp_browser_options);
	return safe;
}

std::unique_ptr<BrowserWidget> BrowserWidget::Create(const obsharp_browser_options &options, BrowserPanel &panel,
						     std::string &error) {
	if (!panel.IsValid()) {
		error = "the obs-browser panel interface is not available";
		return nullptr;
	}

	// 窗口 options 里嵌的 browser.size 可以是 0（全用默认值），老调用方的 size
	// 也可能比现在小；先裁一份安全的再往下用。
	const obsharp_browser_options safe = SanitizeBrowserOptions(options);

	BrowserHostConfig config;
	if (!ResolveBrowserHostConfig(&safe, config, error))
		return nullptr;

	auto self = std::unique_ptr<BrowserWidget>(new BrowserWidget());

	QWidget *parent = ResolveParent(safe.parent_widget);
	const std::string &url = config.url;

	// 只有注册了 scheme handler（也就是端点可用）才注入引导脚本：脚本里的
	// fetch('/__obsharp/message') 得有 handler 接。
	const std::string script =
		config.hasEndpoint ? BuildStartupScript(safe.startup_script ? safe.startup_script : "", config.host)
				   : (safe.startup_script ? safe.startup_script : "");

	QWidget *cefWidget = panel.cef->create_widget(parent, url, nullptr);
	if (!cefWidget) {
		error = "QCef::create_widget() returned null";
		return nullptr;
	}

	self->cef_ = static_cast<QCefWidgetApi *>(cefWidget);
	self->widget_ = cefWidget;
	self->currentUrl_ = url;
	self->host_ = config.host;
	self->scheme_ = config.scheme;

	if (safe.transparent) {
		cefWidget->setAttribute(Qt::WA_TranslucentBackground);
		cefWidget->setAutoFillBackground(false);
	} else if (safe.background_color != 0) {
		// 尽力而为：给 Widget 铺个底色，页面自己画的内容会盖在上面。
		const QColor color = QColor::fromRgba(safe.background_color);
		QPalette palette = cefWidget->palette();
		palette.setColor(QPalette::Window, color);
		cefWidget->setPalette(palette);
		cefWidget->setAutoFillBackground(true);
	}

	if (!script.empty())
		self->cef_->setStartupScript(script);

	const obsharp_browser_callbacks callbacks = safe.callbacks;
	void *user = callbacks.user;

	self->sink_ = std::make_unique<BrowserEventSink>(
		[callbacks, user](const QString &title) {
			if (callbacks.on_title_changed)
				callbacks.on_title_changed(user, title.toUtf8().constData());
		},
		[callbacks, user](const QString &value) {
			if (callbacks.on_url_changed)
				callbacks.on_url_changed(user, value.toUtf8().constData());
		});

	QObject::connect(cefWidget, SIGNAL(titleChanged(QString)), self->sink_.get(),
			 SLOT(OnTitleChanged(QString)));
	QObject::connect(cefWidget, SIGNAL(urlChanged(QString)), self->sink_.get(), SLOT(OnUrlChanged(QString)));

	LogDebug("browser widget created for " + url);
	return self;
}

BrowserWidget::~BrowserWidget() {
	if (!widget_)
		return;

	// 先断开信号，再关浏览器，避免回调打到半销毁的对象上。
	if (sink_)
		QObject::disconnect(widget_, nullptr, sink_.get(), nullptr);

	if (cef_)
		cef_->closeBrowser();

	// widget_ 是我们用 QCef::create_widget() 创建的，归我们释放。用 deleteLater
	// 推迟到事件循环里，避免在信号处理过程中直接 delete。
	widget_->hide();
	widget_->deleteLater();

	cef_ = nullptr;
	widget_ = nullptr;
	sink_.reset();
}

bool BrowserWidget::Load(const std::string &url, std::string &error) {
	if (!cef_)
		return true;

	// obsharp://<host>/... 走映射；普通地址原样交给 CEF。
	std::string mapped;
	if (!MapObsharpUrlForBrowser(host_, url, scheme_, mapped, error))
		return false;

	currentUrl_ = mapped;
	cef_->setURL(mapped);
	return true;
}

void BrowserWidget::Reload() {
	if (cef_)
		cef_->reloadPage();
}

bool BrowserWidget::Zoom(int direction) {
	return cef_ ? cef_->zoomPage(direction) : false;
}

void BrowserWidget::ExecuteScript(const std::string &script) {
	if (cef_)
		cef_->executeJavaScript(script);
}

void BrowserWidget::SetStartupScript(const std::string &script) {
	if (cef_)
		cef_->setStartupScript(script);
}

void BrowserWidget::AllowPopups(bool allow) {
	if (cef_)
		cef_->allowAllPopups(allow);
}

void BrowserWidget::Resize(int width, int height) {
	if (widget_)
		widget_->resize(width, height);
}

void BrowserWidget::Move(int x, int y) {
	if (widget_)
		widget_->move(x, y);
}

void BrowserWidget::SetVisible(bool visible) {
	if (widget_)
		widget_->setVisible(visible);
}

void BrowserWidget::PostToPage(const std::string &json) {
	if (!cef_)
		return;

	std::string script = "window.dispatchEvent(new CustomEvent('obsharp-message', { detail: (";
	script += json.empty() ? "null" : json;
	script += ") }));";
	cef_->executeJavaScript(script);
}

void BrowserWidget::DeliverMessage(const std::string &json) {
	if (onMessage_)
		onMessage_(json);
}

} // namespace obsharp
