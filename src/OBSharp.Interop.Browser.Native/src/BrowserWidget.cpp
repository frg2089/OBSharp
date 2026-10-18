#include "BrowserWidget.h"

#include "Log.h"

#include <QtCore/QObject>
#include <QtGui/QColor>
#include <QtGui/QPalette>
#include <QtWidgets/QWidget>

#include <cstring>

namespace obsharp {
namespace {

// 引导脚本：定义 window.obsharp.post()，把消息发到我们自己的 scheme handler
// （POST /__obsharp/message，见 SchemeHandler.cpp 的 kMessageTarget）。
//
// obs-browser 的面板接口（BROWSER_PANEL_VERSION 3）只暴露 titleChanged /
// urlChanged 两个信号，拿不到 CefLoadHandler / CefDisplayHandler 的回调，所以
// 「加载结束」和「console 消息」这两类事件也走同一条通道：带 "$obsharp" 键的
// JSON 是控制消息（native 侧解析后派发），其余原样交给 on_message。
constexpr const char *kBootstrapScript = R"JS(
(function () {
  if (window.obsharp && window.obsharp.__bootstrap) { return; }
  var api = window.obsharp || (window.obsharp = {});
  api.__bootstrap = true;

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

std::string MakeDefaultUrl(const obsharp_browser_options &options) {
	const char *scheme = options.scheme ? options.scheme : "http";
	const char *domain = options.domain ? options.domain : "obsharp";
	return std::string(scheme) + "://" + domain + "/";
}

} // namespace

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

std::string BuildStartupScript(const std::string &userScript) {
	std::string script = kBootstrapScript;
	if (!userScript.empty()) {
		script += "\n";
		script += userScript;
	}

	return script;
}

std::unique_ptr<BrowserWidget> BrowserWidget::Create(const obsharp_browser_options &options, BrowserPanel &panel,
						     std::string &error) {
	if (!panel.IsValid()) {
		error = "the obs-browser panel interface is not available";
		return nullptr;
	}

	auto self = std::unique_ptr<BrowserWidget>(new BrowserWidget());

	QWidget *parent = ResolveParent(options.parent_widget);
	const bool hasEndpoint = options.endpoint && *options.endpoint;
	const std::string url = (options.url && *options.url) ? options.url : MakeDefaultUrl(options);

	const std::string script = hasEndpoint ? BuildStartupScript(options.startup_script ? options.startup_script : "")
					       : (options.startup_script ? options.startup_script : "");

	QWidget *cefWidget = panel.cef->create_widget(parent, url, nullptr);
	if (!cefWidget) {
		error = "QCef::create_widget() returned null";
		return nullptr;
	}

	self->cef_ = static_cast<QCefWidgetApi *>(cefWidget);
	self->widget_ = cefWidget;
	self->currentUrl_ = url;

	if (options.transparent) {
		cefWidget->setAttribute(Qt::WA_TranslucentBackground);
		cefWidget->setAutoFillBackground(false);
	} else if (options.background_color != 0) {
		// 尽力而为：给 Widget 铺个底色，页面自己画的内容会盖在上面。
		const QColor color = QColor::fromRgba(options.background_color);
		QPalette palette = cefWidget->palette();
		palette.setColor(QPalette::Window, color);
		cefWidget->setPalette(palette);
		cefWidget->setAutoFillBackground(true);
	}

	if (!script.empty())
		self->cef_->setStartupScript(script);

	const obsharp_browser_callbacks callbacks = options.callbacks;
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

void BrowserWidget::Load(const std::string &url) {
	if (!cef_)
		return;

	currentUrl_ = url;
	cef_->setURL(url);
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
