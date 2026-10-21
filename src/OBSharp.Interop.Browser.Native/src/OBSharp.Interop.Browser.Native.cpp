// OBSharp.Interop.Browser.Native.cpp: 公开 C ABI（include/OBSharp.Interop.Browser.Native.h）的实现。
//
// 这个文件是唯一的「粘合层」：把 C ABI 的裸句柄翻译成 C++ 层的
// BrowserWidget / KestrelClient / SchemeHandler，并负责线程与生命周期。

#include "Export.h"

#include "BrowserWidget.h"
#include "HostApi.h"
#include "KestrelClient.h"
#include "Log.h"
#include "SchemeHandler.h"

#include <include/cef_parser.h>
#include <include/cef_task.h>
#include <include/cef_values.h>

#include <QtCore/QCoreApplication>
#include <QtCore/QMetaObject>
#include <QtCore/QThread>

#include <chrono>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
#include <string>
#include <thread>

// ---------------------------------------------------------------------------
// 句柄
// ---------------------------------------------------------------------------

// 一个浏览器实例。C ABI 里只有不透明指针，这里才是真实定义。
//
// host / scheme 由 BrowserWidget 自己记着（见 BrowserWidget.h），这里不重复存一份，
// 免得两处状态不一致；obsharp_browser_load() 的映射与校验都走 widget。
struct obsharp_browser {
	std::unique_ptr<obsharp::BrowserWidget> widget;
	obsharp_browser_callbacks callbacks{};
};

namespace {

// 版本串由构建系统注入（CMake 读仓库根的 Directory.Version.cmake，CI 用 GitVersion
// 生成）。源码里不写死版本；单独用 IDE 打开时退回占位串。
#ifndef OBSHARP_BROWSER_NATIVE_VERSION
#define OBSHARP_BROWSER_NATIVE_VERSION "0.0.0-local"
#endif

constexpr const char *kVersionString = OBSHARP_BROWSER_NATIVE_VERSION;

// 等 obs-browser 把 CEF 初始化完的上限。CEF 冷启动一般 1~3 秒。
constexpr int kCefReadyTimeoutMs = 15000;
// 投到 CEF UI 线程上的同步任务的上限（CEF 正在退出时任务可能永远不跑）。
constexpr int kCefTaskTimeoutMs = 5000;

// 控制消息的保留键，见 BrowserWidget.cpp 里的引导脚本。
constexpr const char *kControlKey = "$obsharp";

int32_t Fail(int32_t code, const std::string &message) {
	obsharp::RecordLastError(message);
	obsharp::LogDebug(message);
	return code;
}

// ---------------------------------------------------------------------------
// 全局状态
// ---------------------------------------------------------------------------

std::mutex g_mutex;            // 保护下面这些
obsharp::BrowserPanel g_panel; // obs-browser 的面板接口（不归我们所有）
bool g_initialized = false;
bool g_initFailed = false;
std::string g_initError;
QThread *g_uiThread = nullptr;

// 当前活着的浏览器。用来判断「投递到 Qt 线程的消息对应的浏览器还在不在」——
// 浏览器可能在消息投递之后、真正派发之前被销毁。
//
// 不能用单个「当前浏览器」指针：进程里可能同时有好几个（停靠面板 + 工具窗口），
// 后建的会覆盖先建的，消息就派发错对象了。
std::unordered_set<obsharp_browser *> g_liveBrowsers;

// domain（CEF 里的 "<host>.obsharp"）-> 浏览器。scheme handler 只知道自己注册在
// 哪个 domain 上，靠这张表把页面消息路由回对应的浏览器。
std::unordered_map<std::string, obsharp_browser *> g_browsersByDomain;

void OBSHARP_BROWSER_NATIVE_CALL DestroyBrowserThunk(void *user);

// ---------------------------------------------------------------------------
// Qt 主线程
// ---------------------------------------------------------------------------

// 异步在 Qt 主线程上跑一段代码。拿不到 QCoreApplication（进程正在退出）就丢弃。
void PostToQtThread(std::function<void()> fn) {
	QCoreApplication *app = QCoreApplication::instance();
	if (!app) {
		obsharp::LogWarning("the Qt application is gone; dropping a UI task");
		return;
	}

	QMetaObject::invokeMethod(app, [fn = std::move(fn)]() mutable { fn(); }, Qt::QueuedConnection);
}

// 是否在 Qt 主线程上。不在的话把原因写进 last error 并返回错误码。
int32_t RequireUiThread(const char *what) {
	if (obsharp_browser_native_is_ui_thread())
		return OBSHARP_OK;

	return Fail(OBSHARP_ERR_WRONG_THREAD, std::string(what) + " must be called on the Qt main thread");
}

// ---------------------------------------------------------------------------
// CEF UI 线程
// ---------------------------------------------------------------------------

// 把一个闭包搬到 CEF 的 UI 线程上同步执行。CefRegisterSchemeHandlerFactory
// 这类接口要求「必须在 CEF UI 线程」。
class SyncTask : public CefTask {
public:
	explicit SyncTask(std::function<void()> fn) : fn_(std::move(fn)) {}

	void Execute() override {
		fn_();
		{
			std::lock_guard<std::mutex> lock(mutex_);
			done_ = true;
		}
		cv_.notify_all();
	}

	bool WaitFor(std::chrono::milliseconds timeout) {
		std::unique_lock<std::mutex> lock(mutex_);
		return cv_.wait_for(lock, timeout, [this]() { return done_; });
	}

private:
	std::function<void()> fn_;
	std::mutex mutex_;
	std::condition_variable cv_;
	bool done_ = false;

	IMPLEMENT_REFCOUNTING(SyncTask);
	DISALLOW_COPY_AND_ASSIGN(SyncTask);
};

bool RunOnCefUiThread(const std::function<void()> &fn) {
	if (CefCurrentlyOn(TID_UI)) {
		fn();
		return true;
	}

	CefRefPtr<SyncTask> task = new SyncTask(fn);
	if (!CefPostTask(TID_UI, task)) {
		obsharp::LogWarning("CefPostTask(TID_UI) failed; CEF is probably shutting down");
		return false;
	}

	if (!task->WaitFor(std::chrono::milliseconds(kCefTaskTimeoutMs))) {
		obsharp::LogWarning("timed out waiting for a task on the CEF UI thread");
		return false;
	}

	return true;
}

// ---------------------------------------------------------------------------
// 页面 -> 原生
// ---------------------------------------------------------------------------

// 引导脚本的约定：带 "$obsharp" 键的 JSON 是控制消息（加载结束 / console），
// 其余原样交给 on_message。只能在 Qt 主线程上调用。
//
// |browser| 是<b>发起这条消息的那个浏览器</b>，由 scheme handler / widget 传进来。
// 不能用「全局当前浏览器」：进程里可能同时活着好几个（停靠面板一个、工具窗口一个），
// 消息必须回到它自己的那个回调上，否则 A 窗口的 console 消息会跑去 B 窗口。
void DispatchBridgeJson(obsharp_browser *browser, const std::string &json) {
	if (!browser)
		return;

	const obsharp_browser_callbacks callbacks = browser->callbacks;

	CefRefPtr<CefValue> value = CefParseJSON(json, JSON_PARSER_RFC);
	if (value && value->GetType() == VTYPE_DICTIONARY) {
		CefRefPtr<CefDictionaryValue> dict = value->GetDictionary();
		if (dict && dict->HasKey(kControlKey)) {
			const std::string kind = dict->GetString(kControlKey).ToString();

			if (kind == "console") {
				if (callbacks.on_console_message) {
					const int32_t level = dict->GetInt("level");
					const std::string message = dict->GetString("message").ToString();
					const std::string source = dict->GetString("source").ToString();
					const int32_t line = dict->GetInt("line");
					callbacks.on_console_message(callbacks.user, level, message.c_str(),
								     source.c_str(), line);
				}
			} else if (kind == "load-end") {
				// 面板接口拿不到 CefLoadHandler，这只是「页面自己报告文档已加载
				// 完」。所以状态码固定 200、错误码固定 0。
				if (callbacks.on_load_end)
					callbacks.on_load_end(callbacks.user, 200, 0, "");
			}

			return; // 控制消息不外泄给 on_message
		}
	}

	if (callbacks.on_message)
		callbacks.on_message(callbacks.user, json.c_str());
}

// SchemeHandler 的回调跑在 CEF 的 UI 线程上，先切回 Qt 主线程再派发。
//
// 带上发起消息的 browser：同一进程里可能有好几个浏览器（停靠面板 + 工具窗口），
// 消息必须回到各自的那个回调上。
void OnBridgeMessage(obsharp_browser *browser, const std::string &json) {
	if (!browser)
		return;

	std::string copy = json;
	PostToQtThread([browser, copy]() {
		// 浏览器可能在投递之后、派发之前被销毁了：g_liveBrowsers 里能查到才派发。
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			if (g_liveBrowsers.find(browser) == g_liveBrowsers.end())
				return;
		}

		DispatchBridgeJson(browser, copy);
	});
}

// scheme handler 是按 (scheme, domain) 注册的，拿不到「哪个浏览器」，只有 domain。
// 这里按 domain 找到对应的浏览器再派发。
//
// 同一个 domain 理论上可以有多个浏览器（比如面板和窗口都指向同一个 host）——
// 那种情况下消息只发给「最近注册的那一个」，够用（bridge 消息是页面自己上报的
// console / load-end，本来就是各窗口独立上报，而 domain 相同意味着页面也相同）。
void RouteBridgeMessage(const std::string &domain, const std::string &json) {
	obsharp_browser *browser = nullptr;
	{
		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_browsersByDomain.find(domain);
		if (it != g_browsersByDomain.end())
			browser = it->second;
	}

	if (browser)
		OnBridgeMessage(browser, json);
}

// ---------------------------------------------------------------------------
// 辅助
// ---------------------------------------------------------------------------

// 从全局状态里取一份面板接口的快照。
bool SnapshotPanel(obsharp::BrowserPanel &out) {
	std::lock_guard<std::mutex> lock(g_mutex);
	if (!g_initialized || g_initFailed || !g_panel.IsValid())
		return false;

	out = g_panel;
	return true;
}

// 等 obs-browser 把 CEF 拉起来。调用方应在 Qt 主线程上，且之前已经调用过
// init_browser()，所以这里通常只等一小会儿。
bool WaitForCefReady(const obsharp::BrowserPanel &panel) {
	const auto deadline =
		std::chrono::steady_clock::now() + std::chrono::milliseconds(kCefReadyTimeoutMs);

	while (!panel.cef->initialized()) {
		if (std::chrono::steady_clock::now() >= deadline)
			return false;

		std::this_thread::sleep_for(std::chrono::milliseconds(25));
	}

	return true;
}

// 注册 scheme handler。CEF 里每个 (scheme, host) 键只能有一个 factory，所以
// 是否已注册由 obsharp::InstallSchemeHandler() 自己记账（同键复用、端点不一致
// 记警告）；这里只负责切到 CEF  UI 线程并区分「切不过去」和「注册失败」。
//
// 消息路由：factory 是按 (scheme, domain) 全局唯一的，拿不到「这是哪个浏览器发的」。
// 所以这里传一个按 domain 查浏览器的路由函数（见 RouteBridgeMessage）。
bool InstallSchemeHandlerOnce(const std::string &scheme, const std::string &domain,
			      const obsharp::Endpoint &endpoint) {
	const std::string key = scheme + "://" + domain;

	std::string error;
	bool installed = false;
	const bool ran = RunOnCefUiThread([&]() {
		installed = obsharp::InstallSchemeHandler(
			scheme, domain, endpoint,
			[domain](const std::string &json) { RouteBridgeMessage(domain, json); }, error);
	});

	if (!ran) {
		Fail(OBSHARP_ERR_INTERNAL, "could not run the scheme registration on the CEF UI thread");
		return false;
	}

	if (!installed) {
		Fail(OBSHARP_ERR_INTERNAL, "failed to register the scheme handler: " + error);
		return false;
	}

	obsharp::LogDebug("scheme handler ready for " + key);
	return true;
}

bool IsUsable(obsharp_browser *browser) {
	return browser && browser->widget;
}

} // namespace

// ---------------------------------------------------------------------------
// 版本与初始化
// ---------------------------------------------------------------------------

extern "C" {

uint32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_abi_version(void) {
	return OBSHARP_BROWSER_NATIVE_ABI_VERSION;
}

const char *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_version(void) {
	return kVersionString;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_initialize(const char *log_tag) {
	if (log_tag && *log_tag)
		obsharp::SetLogTag(log_tag);

	// 打一行版本 + 本 DLL 的实际路径。
	//
	// 排查「改了原生库却没生效」时这是第一条要看的：OB# 的部署目录里
	// （bin/64bit 和 bin/any）可能同时存在这份 DLL，加载的如果不是刚编出来那份，
	// 代码怎么改都不会有效果。版本号也对得上头文件里的 ABI 才算真的更新了。
	{
		std::string where;
#ifdef _WIN32
		if (HMODULE self = nullptr;
		    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
					       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
				       reinterpret_cast<LPCWSTR>(&obsharp_browser_native_initialize), &self)) {
			wchar_t buffer[MAX_PATH]{};
			if (GetModuleFileNameW(self, buffer, MAX_PATH) > 0) {
				const int needed = WideCharToMultiByte(CP_UTF8, 0, buffer, -1, nullptr, 0, nullptr, nullptr);
				if (needed > 0) {
					where.resize(static_cast<size_t>(needed - 1));
					WideCharToMultiByte(CP_UTF8, 0, buffer, -1, where.data(), needed, nullptr, nullptr);
				}
			}
		}
#endif
		obsharp::LogInfo(std::string("native library v") + kVersionString + " (ABI " +
				 std::to_string(OBSHARP_BROWSER_NATIVE_ABI_VERSION) + ")" +
				 (where.empty() ? std::string() : " loaded from " + where));
	}

	obsharp::QCef *panelCef = nullptr;
	int panelVersion = 0;
	{
		std::lock_guard<std::mutex> lock(g_mutex);

		if (!g_initialized) {
			if (!g_uiThread) {
				if (QCoreApplication *app = QCoreApplication::instance())
					g_uiThread = app->thread();
				else
					g_uiThread = QThread::currentThread();
			}

			std::string error;
			if (obsharp::LoadBrowserPanel(g_panel, error)) {
				g_initError.clear();
			} else {
				g_initFailed = true;
				g_initError = error;
			}

			g_initialized = true;
		}

		if (g_initFailed) {
			obsharp::RecordLastError(g_initError);
			return OBSHARP_ERR_BROWSER_UNAVAILABLE;
		}

		panelCef = g_panel.cef;
		panelVersion = g_panel.version;
	}

	// 走 obs-browser 自己的入口把 CEF 拉起来（等价于用户打开一次浏览器 dock
	// 或建一个浏览器源）。这里不创建 CefApp、不调用 CefInitialize —— 那个
	// CefApp 是 obs-browser 的。内部有原子保护，重复调用无害。
	if (panelCef) {
		panelCef->init_browser();
		obsharp::LogInfo("initialized; obs-browser panel version " + std::to_string(panelVersion));
	}

	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_is_available(void) {
	obsharp::BrowserPanel panel;
	if (!SnapshotPanel(panel))
		return 0;

	return panel.cef->initialized() ? 1 : 0;
}

const char *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_last_error(void) {
	return obsharp::LastErrorText();
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_is_ui_thread(void) {
	QThread *ui = nullptr;
	{
		std::lock_guard<std::mutex> lock(g_mutex);
		ui = g_uiThread;
	}

	if (!ui) {
		QCoreApplication *app = QCoreApplication::instance();
		if (!app)
			return 0;

		ui = app->thread();
	}

	return QThread::currentThread() == ui ? 1 : 0;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_invoke_ui(obsharp_ui_action_fn fn, void *user) {
	if (!fn)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "fn must not be null");

	if (obsharp_browser_native_is_ui_thread()) {
		fn(user);
		return OBSHARP_OK;
	}

	QCoreApplication *app = QCoreApplication::instance();
	if (!app)
		return Fail(OBSHARP_ERR_WRONG_THREAD, "the Qt application is not available");

	const bool posted =
		QMetaObject::invokeMethod(app, [fn, user]() { fn(user); }, Qt::BlockingQueuedConnection);

	if (!posted)
		return Fail(OBSHARP_ERR_WRONG_THREAD, "could not post the action to the Qt main thread");

	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_probe_endpoint(const char *endpoint, int32_t endpoint_kind) {
	if (!endpoint || !*endpoint)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "endpoint must not be empty");

	obsharp::Endpoint target;
	std::string error;
	if (!obsharp::NormalizeEndpoint(endpoint, endpoint_kind, target, error))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, error);

	std::unique_ptr<obsharp::KestrelConnection> connection =
		obsharp::KestrelConnection::Connect(target, error);
	if (!connection)
		return Fail(OBSHARP_ERR_ENDPOINT, error);

	connection->Close();
	return OBSHARP_OK;
}

// ---------------------------------------------------------------------------
// 浏览器生命周期
// ---------------------------------------------------------------------------

obsharp_browser *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_create(const obsharp_browser_options *options) {
	if (!options) {
		Fail(OBSHARP_ERR_INVALID_ARGUMENT, "options must not be null");
		return nullptr;
	}

	if (options->size < sizeof(obsharp_browser_options)) {
		Fail(OBSHARP_ERR_INVALID_ARGUMENT,
		     "obsharp_browser_options.size is smaller than this build expects; recompile the "
		     "caller against a matching header");
		return nullptr;
	}

	if (!obsharp_browser_native_is_ui_thread()) {
		Fail(OBSHARP_ERR_WRONG_THREAD, "obsharp_browser_create must be called on the Qt main thread");
		return nullptr;
	}

	if (obsharp_browser_native_initialize(nullptr) != OBSHARP_OK)
		return nullptr; // initialize 已经把原因写进 last error

	obsharp::BrowserPanel panel;
	if (!SnapshotPanel(panel)) {
		Fail(OBSHARP_ERR_NOT_INITIALIZED, "the obs-browser panel interface is not available");
		return nullptr;
	}

	if (!WaitForCefReady(panel)) {
		Fail(OBSHARP_ERR_BROWSER_UNAVAILABLE,
		     "the obs-browser CEF instance did not finish initializing within the timeout");
		return nullptr;
	}

	// 分析 host / scheme / domain / 端点 / 初始 URL。没有 host 且没给端点时跟
	// v1 完全一样；有 host 时端点按平台从 host 推导（除非调用方显式给了）。
	obsharp::BrowserHostConfig hostConfig;
	{
		std::string error;
		if (!obsharp::ResolveBrowserHostConfig(options, hostConfig, error)) {
			Fail(OBSHARP_ERR_INVALID_ARGUMENT, error);
			return nullptr;
		}
	}

	if (hostConfig.hasEndpoint) {
		obsharp::Endpoint endpoint;
		std::string error;
		// 端点种类用分析结果里的那份：有 host 且调用方没给端点时它是 Auto
		// （按平台推导），不会被调用方顺手填错的 endpoint_kind 带偏。
		const int32_t kind = static_cast<int32_t>(hostConfig.endpoint.kind);
		if (!obsharp::NormalizeEndpoint(hostConfig.endpoint.address, kind, endpoint, error)) {
			Fail(OBSHARP_ERR_INVALID_ARGUMENT, "invalid endpoint: " + error);
			return nullptr;
		}

		if (!InstallSchemeHandlerOnce(hostConfig.scheme, hostConfig.domain, endpoint))
			return nullptr; // last error 已经设置好了
	}

	std::unique_ptr<obsharp_browser> browser(new obsharp_browser());
	browser->callbacks = options->callbacks;

	std::string error;
	std::unique_ptr<obsharp::BrowserWidget> widget =
		obsharp::BrowserWidget::Create(*options, panel, error);
	if (!widget) {
		Fail(OBSHARP_ERR_INTERNAL, "failed to create the browser widget: " + error);
		return nullptr;
	}

	browser->widget = std::move(widget);

	// 消息处理器要带上 browser 自己：见 OnBridgeMessage 的说明。
	obsharp_browser *raw = browser.get();
	raw->widget->SetMessageHandler([raw](const std::string &json) { OnBridgeMessage(raw, json); });

	{
		std::lock_guard<std::mutex> lock(g_mutex);
		g_liveBrowsers.insert(raw);
		if (!hostConfig.domain.empty())
			g_browsersByDomain[hostConfig.domain] = raw;
	}

	return browser.release();
}

void OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_destroy(obsharp_browser *browser) {
	if (!browser)
		return;

	if (!obsharp_browser_native_is_ui_thread()) {
		// 销毁会碰 Qt，又不能像其它操作那样直接返回错误（返回就等于泄漏），
		// 所以切到 Qt 主线程上同步做完。
		obsharp::LogWarning("obsharp_browser_destroy was called off the Qt main thread; marshalling");
		obsharp_browser_native_invoke_ui(&DestroyBrowserThunk, browser);
		return;
	}

	{
		// 先从存活集合里摘掉：之后再到来的消息就不会派发到这个已销毁的对象上。
		std::lock_guard<std::mutex> lock(g_mutex);
		g_liveBrowsers.erase(browser);

		for (auto it = g_browsersByDomain.begin(); it != g_browsersByDomain.end();) {
			if (it->second == browser)
				it = g_browsersByDomain.erase(it);
			else
				++it;
		}
	}

	delete browser;
}

void *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_widget(obsharp_browser *browser) {
	if (!browser || !browser->widget)
		return nullptr;

	return browser->widget->Widget();
}

// ---------------------------------------------------------------------------
// 浏览器操作
// ---------------------------------------------------------------------------

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_load(obsharp_browser *browser, const char *url) {
	if (const int32_t result = RequireUiThread("obsharp_browser_load"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_load: browser is null");

	if (!url || !*url)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "url must not be empty");

	// url 以 obsharp:// 开头时按本浏览器的 host 映射成 CEF 形式，host 对不上
	// 就是参数错误；普通地址原样加载。
	std::string error;
	if (!browser->widget->Load(url, error))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_load: " + error);

	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_reload(obsharp_browser *browser) {
	if (const int32_t result = RequireUiThread("obsharp_browser_reload"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_reload: browser is null");

	browser->widget->Reload();
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_zoom(obsharp_browser *browser, int32_t direction) {
	if (const int32_t result = RequireUiThread("obsharp_browser_zoom"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_zoom: browser is null");

	browser->widget->Zoom(direction);
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_execute_script(obsharp_browser *browser, const char *script) {
	if (const int32_t result = RequireUiThread("obsharp_browser_execute_script"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_execute_script: browser is null");

	if (!script)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "script must not be null");

	browser->widget->ExecuteScript(script);
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_set_startup_script(obsharp_browser *browser, const char *script) {
	if (const int32_t result = RequireUiThread("obsharp_browser_set_startup_script");
	    result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_set_startup_script: browser is null");

	browser->widget->SetStartupScript(script ? script : "");
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_allow_popups(obsharp_browser *browser, int32_t allow) {
	if (const int32_t result = RequireUiThread("obsharp_browser_allow_popups"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_allow_popups: browser is null");

	browser->widget->AllowPopups(allow != 0);
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_resize(obsharp_browser *browser, int32_t width, int32_t height) {
	if (const int32_t result = RequireUiThread("obsharp_browser_resize"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_resize: browser is null");

	if (width < 0 || height < 0)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "width and height must not be negative");

	browser->widget->Resize(width, height);
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_move(obsharp_browser *browser, int32_t x, int32_t y) {
	if (const int32_t result = RequireUiThread("obsharp_browser_move"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_move: browser is null");

	browser->widget->Move(x, y);
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_set_visible(obsharp_browser *browser, int32_t visible) {
	if (const int32_t result = RequireUiThread("obsharp_browser_set_visible"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_set_visible: browser is null");

	browser->widget->SetVisible(visible != 0);
	return OBSHARP_OK;
}

int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_post_message(obsharp_browser *browser, const char *json) {
	if (const int32_t result = RequireUiThread("obsharp_browser_post_message"); result != OBSHARP_OK)
		return result;

	if (!IsUsable(browser))
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "obsharp_browser_post_message: browser is null");

	if (!json)
		return Fail(OBSHARP_ERR_INVALID_ARGUMENT, "json must not be null");

	browser->widget->PostToPage(json);
	return OBSHARP_OK;
}

const char *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_current_url(obsharp_browser *browser) {
	if (!IsUsable(browser))
		return "";

	return browser->widget->CurrentUrl().c_str();
}

} // extern "C"

// ---------------------------------------------------------------------------
// thunk
// ---------------------------------------------------------------------------

namespace {

void OBSHARP_BROWSER_NATIVE_CALL DestroyBrowserThunk(void *user) {
	obsharp_browser_destroy(static_cast<obsharp_browser *>(user));
}

} // namespace
