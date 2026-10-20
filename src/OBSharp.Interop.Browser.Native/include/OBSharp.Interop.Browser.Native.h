/*
 * OBSharp.Interop.Browser.Native —— OB# 用的 CEF Qt Widget 原生库公开 C ABI。
 *
 * 这个头文件只描述 ABI，不包含任何 C++ / Qt / CEF 类型；OBSharp.Interop 里的
 * 托管绑定（src/OBSharp.Interop.Browser/）就是照着它写的，两边改动要同步。
 *
 * 这个库本身不初始化 CEF，也不自己创建 CefApp：它复用 obs-browser 已经初始化
 * 好的那个 CEF 实例（obs_browser_create_qcef），以及 OBS 自己带的 Qt6。因此它
 * 只能被加载进 OBS 进程里使用 —— 也就是 OB# 宿主所在的进程。
 *
 * 字符串一律是 NUL 结尾的 UTF-8。
 *
 * 设计边界（重要）
 * ----------------
 * 这个库**只做一件事**：把 CEF 包成一个 Qt Widget，外加一个可选的普通顶层窗口容器。
 * 它**不**碰 OBS 的停靠面板（那是 obs_frontend_add_dock_by_id 的事，由托管侧调）。
 *
 *   * 停靠面板：托管侧拿 obsharp_browser_widget() 的 QWidget*，交给
 *     obs_frontend_add_dock_by_id(id, title, widget)。OBS 自己会建 QDockWidget、
 *     挂进主窗口、**并且自动加「停靠窗口」菜单项**（内部走 AddDockWidget），
 *     所以本库不再提供任何 dock 相关 API，也不需要什么 add_menu_item。
 *   * 工具窗口：obsharp_window_* 提供一个普通顶层窗口（Qt::Window），托管侧把
 *     browser widget 放进去，或者干脆自己拿 widget 塞进任何容器。
 *
 * 线程模型
 * --------
 *   * 除 obsharp_browser_native_initialize / obsharp_browser_native_is_available 外，其余函数都
 *     会碰 Qt，必须跑在 OBS 的 Qt 主线程上。不在主线程时先用
 *     obsharp_browser_native_invoke_ui() 切过去。
 *   * obsharp_browser_native_initialize() 幂等，可以在任意线程调用；它会异步等待
 *     obs-browser 把 CEF 初始化完。
 *
 * 所有权
 * ------
 *   * 传进来的字符串由调用方所有，函数返回后即可释放。
 *   * 返回的 const char* 归库所有：下一次同类调用、或对象销毁后失效，需要保留
 *     就自己拷一份。
 *   * obsharp_browser / obsharp_window 句柄归调用方所有：用完必须 _destroy()。
 *   * obsharp_browser_widget() 返回的 QWidget* 归浏览器所有，不要自己 delete；
 *     交给 OBS 之前**不要**把它挂到别的父级上。
 *
 * host 与地址
 * -----------
 *   * host 是「一个名字 + 一台 Kestrel」。端点由 OB# 的托管侧（SDK）起监听：
 *     Windows 是命名管道 \\.\pipe\obsharp\<host>，Unix 是 /tmp/obsharp/<host>.socket。
 *   * 插件看到的地址是 obsharp://<host>/...。
 *   * CEF 里实际访问的是 https://<host>.obsharp/...，但**不做 TLS**：https 是
 *     CEF / Chromium 的内置 scheme，可以给它注册 scheme handler factory；我们的
 *     factory 总是返回 handler，所以请求被直接转给该 host 的命名管道 / UDS，压根
 *     不进网络栈、不握手、不加解密。对页面来说来源是 https，因此落在安全上下文里
 *     （crypto.subtle / serviceWorker / clipboard 这些 API 可用），而实际传输仍是
 *     明文的进程内 IPC，没有加密开销。
 *
 * 已知限制
 * --------
 *   * WebSocket 不经过 scheme handler：CEF 的 WebSocket 走独立的 connector，会自己
 *     去做 TLS 握手。所以 wss://<host>.obsharp/ 连不上，页面要双向实时通道请用
 *     window.obsharp.post() / CustomEvent("obsharp-message")，或者自己起一个真实的
 *     TCP 监听。
 */

#pragma once

#include <stddef.h>
#include <stdint.h>

#define OBSHARP_BROWSER_NATIVE_ABI_VERSION 3u

#if defined(_WIN32)
#if defined(OBSHARP_BROWSER_NATIVE_BUILD)
#define OBSHARP_BROWSER_NATIVE_API __declspec(dllexport)
#else
#define OBSHARP_BROWSER_NATIVE_API __declspec(dllimport)
#endif
#define OBSHARP_BROWSER_NATIVE_CALL __cdecl
#else
#define OBSHARP_BROWSER_NATIVE_API __attribute__((visibility("default")))
#define OBSHARP_BROWSER_NATIVE_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* 不透明句柄：一个 CEF 浏览器 Widget 实例。 */
typedef struct obsharp_browser obsharp_browser;

/* 不透明句柄：一个普通顶层窗口容器（可选的便利设施，见下）。 */
typedef struct obsharp_window obsharp_window;

/* 返回码。0 表示成功，负数是错误。 */
typedef enum obsharp_result {
	OBSHARP_OK = 0,

	/* 参数不合法（空指针、size 不匹配……）。 */
	OBSHARP_ERR_INVALID_ARGUMENT = -1,
	/* 还没调用 obsharp_browser_native_initialize()，或者初始化失败。 */
	OBSHARP_ERR_NOT_INITIALIZED = -2,
	/* 进程里没有可用的 obs-browser / CEF。 */
	OBSHARP_ERR_BROWSER_UNAVAILABLE = -3,
	/* 在错误的线程上调用了只能在 UI 线程调用的函数。 */
	OBSHARP_ERR_WRONG_THREAD = -4,
	/* 命名管道 / UDS 连接失败。 */
	OBSHARP_ERR_ENDPOINT = -5,
	/* 其它内部错误。 */
	OBSHARP_ERR_INTERNAL = -6
} obsharp_result;

/* endpoint 的解释方式。AUTO 按平台推断：Windows = 命名管道，其余 = UDS。 */
typedef enum obsharp_endpoint_kind {
	OBSHARP_ENDPOINT_AUTO = 0,
	OBSHARP_ENDPOINT_NAMED_PIPE = 1,
	OBSHARP_ENDPOINT_UNIX_SOCKET = 2
} obsharp_endpoint_kind;

/* 页面控制台消息的级别。 */
typedef enum obsharp_console_level {
	OBSHARP_CONSOLE_VERBOSE = 0,
	OBSHARP_CONSOLE_INFO = 1,
	OBSHARP_CONSOLE_WARNING = 2,
	OBSHARP_CONSOLE_ERROR = 3,
	OBSHARP_CONSOLE_FATAL = 4
} obsharp_console_level;

/* 页面标题变化。title 是 UTF-8，回调返回后失效。 */
typedef void(OBSHARP_BROWSER_NATIVE_CALL *obsharp_title_changed_fn)(void *user, const char *title);
/* 页面 URL 变化。 */
typedef void(OBSHARP_BROWSER_NATIVE_CALL *obsharp_url_changed_fn)(void *user, const char *url);
/* 一次主文档加载结束。error_code 为 0 表示成功；否则是 CEF 的 cef_errorcode_t。 */
typedef void(OBSHARP_BROWSER_NATIVE_CALL *obsharp_load_end_fn)(void *user, int32_t http_status_code, int32_t error_code,
						      const char *error_text);
/* 页面的 console.* 消息。source 与 error_text 一样是借用指针。 */
typedef void(OBSHARP_BROWSER_NATIVE_CALL *obsharp_console_fn)(void *user, int32_t level, const char *message,
						      const char *source, int32_t line);
/* 页面通过 window.obsharp.post(json) 发来的消息，json 是原文。 */
typedef void(OBSHARP_BROWSER_NATIVE_CALL *obsharp_message_fn)(void *user, const char *json);

typedef struct obsharp_browser_callbacks {
	/* 原样透传给每个回调的上下文指针，不参与生命周期管理。 */
	void *user;
	obsharp_title_changed_fn on_title_changed;
	obsharp_url_changed_fn on_url_changed;
	obsharp_load_end_fn on_load_end;
	obsharp_console_fn on_console_message;
	obsharp_message_fn on_message;
} obsharp_browser_callbacks;

/*
 * 创建浏览器时的参数。
 *
 * size 必须填成 sizeof(obsharp_browser_options)：将来加字段时旧调用方也能用。
 * 不认识的字段保持 0 / NULL 即可。
 */
typedef struct obsharp_browser_options {
	uint32_t size;

	/* 初始 URL。为 NULL 时用 "obsharp://<host>/"，没有 host 时用 "<scheme>://<domain>/"。 */
	const char *url;
	/*
	 * 自定义 scheme handler 注册用的 scheme 与 host：
	 * 凡是 <scheme>://<domain>/... 的请求都会转到 Kestrel 的 endpoint。
	 *
	 * scheme 省略时用 "https"：它是 CEF 的内置 scheme，可以挂 scheme handler
	 * factory，页面因此落在安全上下文里；请求由本库直接转给 IPC 端点，**不做
	 * TLS**。想退回旧行为（http）可以显式传 "http"。
	 * domain 省略时用 "obsharp"。
	 *
	 * 给了 host（见下面的 host 字段）时 domain 被忽略：实际注册的是
	 * "<host>.obsharp"。
	 */
	const char *scheme;
	const char *domain;
	/*
	 * Kestrel 的监听地址：Windows 是命名管道名（"obsharp-xxx" 或
	 * "\\.\pipe\obsharp-xxx"），Unix 是 UDS 路径。为 NULL 时不注册
	 * scheme handler，浏览器按普通网络访问 url。
	 *
	 * 给了 host 时可以为 NULL：这时按 host 推导（见 host 字段）。
	 */
	const char *endpoint;
	int32_t endpoint_kind;

	/* 每个页面文档创建前注入的脚本，可为 NULL。 */
	const char *startup_script;

	/* 非 0 时让 Widget 背景透明（用 background_color 的 alpha 通道也可以）。 */
	int32_t transparent;
	/*
	 * 0xAARRGGBB 背景色，非 0 时作为 Widget 的底色。这是尽力而为的：
	 * CEF 用无窗口渲染，页面自己画的内容会盖住它。transparent 优先。
	 */
	uint32_t background_color;

	/*
	 * 父窗口。Windows 上按 HWND 解释（内部走 QWidget::find，找不到再当成
	 * QWidget*）；其它平台直接当成 QWidget*。可以为 NULL，此时 Widget 没有父级。
	 *
	 * 走停靠面板时传 NULL：OBS 的 add_dock_by_id 会自己 setWidget。
	 */
	void *parent_widget;

	obsharp_browser_callbacks callbacks;

	/*
	 * OB# host 名（v2 新增，可 NULL）：小写字母 / 数字 / 连字符，1~48 个字符，
	 * 首尾不能是连字符。给了 host 就表示页面走 obsharp://<host>/：
	 *   * endpoint 为 NULL 时按平台推导：Windows "\\.\pipe\obsharp\<host>"，
	 *     Unix "/tmp/obsharp/<host>.socket"；
	 *   * url 为 NULL 时用 "obsharp://<host>/"；
	 *   * url 以 "obsharp://" 开头时，里面的 host 必须与这个字段一致；
	 *   * 注入的引导脚本里多出 window.obsharp.host / window.obsharp.url。
	 * 为 NULL 时行为与 v1 一致。
	 */
	const char *host;
} obsharp_browser_options;

/* 窗口事件。全部在 Qt 主线程上回调。 */
typedef void(OBSHARP_BROWSER_NATIVE_CALL *obsharp_window_event_fn)(void *user);

typedef struct obsharp_window_callbacks {
	/* 原样透传，不参与生命周期管理。 */
	void *user;
	/* 用户点了关闭：窗口已经被隐藏，不会自动销毁。可 NULL。 */
	obsharp_window_event_fn on_close_requested;
} obsharp_window_callbacks;

/*
 * 普通顶层窗口的创建参数。
 *
 * size 必须填成 sizeof(obsharp_window_options)。
 *
 * 这是一个**纯容器**：它只建一个 Qt::Window 顶层窗口，把浏览器 Widget 放进去，
 * 不碰 OBS。停靠面板请直接用 obsharp_browser_widget() + obs_frontend_add_dock_by_id。
 */
typedef struct obsharp_window_options {
	uint32_t size;

	/* 窗口标题。NULL 时为空。 */
	const char *title;

	/* 初始尺寸，<= 0 时用 720x480。 */
	int32_t width;
	int32_t height;

	/* 创建后是否立刻显示，默认 0。 */
	int32_t visible;
	/* 非 0 时窗口置顶（Qt::WindowStaysOnTopHint）。 */
	int32_t topmost;
	/* 默认 1；0 = 固定大小（禁止用户缩放）。 */
	int32_t resizable;

	obsharp_window_callbacks callbacks;
} obsharp_window_options;

/* ---- 版本与初始化 ------------------------------------------------------- */

/* 编译进库的 ABI 版本；跟头文件里的 OBSHARP_BROWSER_NATIVE_ABI_VERSION 比对。 */
OBSHARP_BROWSER_NATIVE_API uint32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_abi_version(void);

/* 人类可读的版本串，形如 "0.3.0"。静态存储，永远有效。 */
OBSHARP_BROWSER_NATIVE_API const char *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_version(void);

/*
 * 初始化：解析 obs-browser 模块、拿到 QCef 面板接口，并异步等待 CEF 就绪。
 * 幂等，可在任意线程调用。log_tag 是可选的日志前缀，可为 NULL。
 * 成功返回 OBSHARP_OK；进程里没有 obs-browser 时返回 OBSHARP_ERR_BROWSER_UNAVAILABLE。
 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_initialize(const char *log_tag);

/* CEF 面板是否已经可以创建 Widget（1 = 是，0 = 否）。任意线程可调用。 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_is_available(void);

/* 最近的错误描述；没有错误时返回空串。借用指针。 */
OBSHARP_BROWSER_NATIVE_API const char *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_last_error(void);

/* 当前线程是不是 Qt 主线程（1 = 是，0 = 否）。 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_native_is_ui_thread(void);

/*
 * 在 Qt 主线程上同步执行 fn(user)。已经在主线程时直接执行。
 * 返回 OBSHARP_OK；从非主线程调用且主线程不可用时返回错误。
 */
typedef void(OBSHARP_BROWSER_NATIVE_CALL *obsharp_ui_action_fn)(void *user);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_native_invoke_ui(obsharp_ui_action_fn fn, void *user);

/* ---- 浏览器（全部要求 Qt 主线程） -------------------------------------- */

/*
 * 创建浏览器并返回句柄；失败返回 NULL（用 obsharp_browser_native_last_error() 看原因）。
 * callbacks 里的函数指针会在 UI 线程被调用。
 */
OBSHARP_BROWSER_NATIVE_API obsharp_browser *OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_create(const obsharp_browser_options *options);

/* 销毁浏览器（连带它自己的 QWidget）。传 NULL 是空操作。 */
OBSHARP_BROWSER_NATIVE_API void OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_destroy(obsharp_browser *browser);

/*
 * 底层的 QWidget*。调用方可以：
 *   * 交给 obs_frontend_add_dock_by_id(id, title, widget)（OBS 会自己 setWidget）；
 *   * 放进 obsharp_window_create() 建的窗口；
 *   * 自己 setParent。
 *
 * Widget 归浏览器所有，destroy 时会一并销毁，不要自己 delete。
 * 交给 OBS 之前不要先挂到别的父级上（OBS 的 add_dock_by_id 会 setWidget）。
 * 句柄无效时返回 NULL。
 */
OBSHARP_BROWSER_NATIVE_API void *OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_widget(obsharp_browser *browser);

/*
 * 导航到 url。url 以 "obsharp://" 开头时按该浏览器的 host 映射成
 * https://<host>.obsharp/...（仍走 IPC、不做 TLS）；host 对不上返回
 * OBSHARP_ERR_INVALID_ARGUMENT。
 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_load(obsharp_browser *browser, const char *url);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_browser_reload(obsharp_browser *browser);
/* direction < 0 缩小，> 0 放大，== 0 复位。 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_zoom(obsharp_browser *browser, int32_t direction);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_execute_script(obsharp_browser *browser, const char *script);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_set_startup_script(obsharp_browser *browser, const char *script);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_allow_popups(obsharp_browser *browser, int32_t allow);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_resize(obsharp_browser *browser, int32_t width, int32_t height);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_move(obsharp_browser *browser, int32_t x, int32_t y);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_set_visible(obsharp_browser *browser, int32_t visible);

/*
 * 给页面发一条消息：会在页面里派发 CustomEvent("obsharp-message")，detail 是
 * 解析后的 json。json 必须是合法 JSON 文本（这里不校验，原样透传）。
 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_post_message(obsharp_browser *browser, const char *json);

/* 当前文档 URL；没有时返回空串。借用指针。 */
OBSHARP_BROWSER_NATIVE_API const char *OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_current_url(obsharp_browser *browser);

/* ---- 普通顶层窗口（可选，全部要求 Qt 主线程） -------------------------- */

/*
 * 创建一个空的普通顶层窗口（Qt::Window，普通标题栏、进任务栏、可单独 Alt+Tab）。
 * 之后用 obsharp_window_set_content() 把浏览器 Widget 放进去。
 *
 * 文本类参数的语义：这是纯 Qt 容器，**不**读 host / URL。
 */
OBSHARP_BROWSER_NATIVE_API obsharp_window *OBSHARP_BROWSER_NATIVE_CALL
obsharp_window_create(const obsharp_window_options *options);

/* 销毁窗口。传 NULL 是空操作。**不会**销毁里面的浏览器 Widget。 */
OBSHARP_BROWSER_NATIVE_API void OBSHARP_BROWSER_NATIVE_CALL obsharp_window_destroy(obsharp_window *window);

/*
 * 把 content（通常是 obsharp_browser_widget()）放进窗口，铺满。
 * content 为 NULL 时清空。窗口不接管 content 的所有权：
 * 销毁顺序应该是 obsharp_window_destroy() 之后（或之前都行，见 README）再 destroy 浏览器。
 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_window_set_content(obsharp_window *window, void *content);

/* 窗口里的 QWidget*（借用）。没设内容时返回窗口本身。 */
OBSHARP_BROWSER_NATIVE_API void *OBSHARP_BROWSER_NATIVE_CALL obsharp_window_widget(obsharp_window *window);

OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_show(obsharp_window *window);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_hide(obsharp_window *window);
/* 1 = 可见，0 = 不可见。 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL obsharp_window_is_visible(obsharp_window *window);

OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_window_set_title(obsharp_window *window, const char *title);
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_window_set_size(obsharp_window *window, int32_t width, int32_t height);

/* ---- 传输辅助 ----------------------------------------------------------- */

/*
 * 探测 endpoint 是否可连接（用于在创建浏览器前确认 Kestrel 已经起来了）。
 * 返回 OBSHARP_OK / OBSHARP_ERR_ENDPOINT / OBSHARP_ERR_INVALID_ARGUMENT。
 */
OBSHARP_BROWSER_NATIVE_API int32_t OBSHARP_BROWSER_NATIVE_CALL
obsharp_browser_native_probe_endpoint(const char *endpoint, int32_t endpoint_kind);

#ifdef __cplusplus
} /* extern "C" */
#endif
