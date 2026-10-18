/*
 * OBSharp.Interop.Browser.Native —— OB# 用的 CEF Qt Widget 原生库公开 C ABI。
 *
 * 这个头文件只描述 ABI，不包含任何 C++ / Qt / CEF 类型；OBSharp.Interop 里的
 * 托管绑定（src/OBSharp.Interop/Native/）就是照着它写的，两边改动要同步。
 *
 * 这个库本身不初始化 CEF，也不自己创建 CefApp：它复用 obs-browser 已经初始化
 * 好的那个 CEF 实例（obs_browser_create_qcef），以及 OBS 自己带的 Qt6。因此它
 * 只能被加载进 OBS 进程里使用 —— 也就是 OB# 宿主所在的进程。
 *
 * 字符串一律是 NUL 结尾的 UTF-8。
 *
 * 线程模型
 * --------
 *   * 除 obsharp_native_initialize / obsharp_native_is_available 外，其余函数都
 *     会碰 Qt，必须跑在 OBS 的 Qt 主线程上。不在主线程时先用
 *     obsharp_native_invoke_ui() 切过去。
 *   * obsharp_native_initialize() 幂等，可以在任意线程调用；它会异步等待
 *     obs-browser 把 CEF 初始化完。
 *
 * 所有权
 * ------
 *   * 传进来的字符串由调用方所有，函数返回后即可释放。
 *   * 返回的 const char* 归库所有：下一次同类调用、或对象销毁后失效，需要保留
 *     就自己拷一份。
 */

#pragma once

#include <stddef.h>
#include <stdint.h>

#define OBSHARP_NATIVE_ABI_VERSION 1u

#if defined(_WIN32)
#if defined(OBSHARP_NATIVE_BUILD)
#define OBSHARP_NATIVE_API __declspec(dllexport)
#else
#define OBSHARP_NATIVE_API __declspec(dllimport)
#endif
#define OBSHARP_NATIVE_CALL __cdecl
#else
#define OBSHARP_NATIVE_API __attribute__((visibility("default")))
#define OBSHARP_NATIVE_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* 不透明句柄：一个 CEF 浏览器 Widget 实例。 */
typedef struct obsharp_browser obsharp_browser;

/* 返回码。0 表示成功，负数是错误。 */
typedef enum obsharp_result {
	OBSHARP_OK = 0,

	/* 参数不合法（空指针、size 不匹配……）。 */
	OBSHARP_ERR_INVALID_ARGUMENT = -1,
	/* 还没调用 obsharp_native_initialize()，或者初始化失败。 */
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
typedef void(OBSHARP_NATIVE_CALL *obsharp_title_changed_fn)(void *user, const char *title);
/* 页面 URL 变化。 */
typedef void(OBSHARP_NATIVE_CALL *obsharp_url_changed_fn)(void *user, const char *url);
/* 一次主文档加载结束。error_code 为 0 表示成功；否则是 CEF 的 cef_errorcode_t。 */
typedef void(OBSHARP_NATIVE_CALL *obsharp_load_end_fn)(void *user, int32_t http_status_code, int32_t error_code,
						      const char *error_text);
/* 页面的 console.* 消息。source 与 error_text 一样是借用指针。 */
typedef void(OBSHARP_NATIVE_CALL *obsharp_console_fn)(void *user, int32_t level, const char *message,
						      const char *source, int32_t line);
/* 页面通过 window.obsharp.post(json) 发来的消息，json 是原文。 */
typedef void(OBSHARP_NATIVE_CALL *obsharp_message_fn)(void *user, const char *json);

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

	/* 初始 URL。为 NULL 时用 "http://<domain>/"。 */
	const char *url;
	/*
	 * 自定义 scheme handler 注册用的 scheme 与 host：
	 * 凡是 <scheme>://<domain>/... 的请求都会转到 Kestrel 的 endpoint。
	 * scheme 省略时用 "http"（obs-browser 已把它注册成 standard + CORS
	 * enabled，所以能直接复用）；domain 省略时用 "obsharp"。
	 */
	const char *scheme;
	const char *domain;
	/*
	 * Kestrel 的监听地址：Windows 是命名管道名（"obsharp-xxx" 或
	 * "\\.\pipe\obsharp-xxx"），Unix 是 UDS 路径。为 NULL 时不注册
	 * scheme handler，浏览器按普通网络访问 url。
	 */
	const char *endpoint;
	int32_t endpoint_kind;

	/* 每个页面文档创建前注入的脚本，可为 NULL。 */
	const char *startup_script;

	/* 非 0 时让 Widget 背景透明（用 background_color 的 alpha 通道也可以）。 */
	int32_t transparent;
	/*
	 * 0xAARRGGBB 背景色，非 0 时作为 Widget 的底色。这是尽力而为的：
	 * CEF 用无窗口渲染，页面自己画的背景会盖住它。transparent 优先。
	 */
	uint32_t background_color;

	/*
	 * 父窗口。Windows 上按 HWND 解释（内部走 QWidget::find，找不到再当成
	 * QWidget*）；其它平台直接当成 QWidget*。可以为 NULL，此时 Widget 没有父级。
	 */
	void *parent_widget;

	obsharp_browser_callbacks callbacks;
} obsharp_browser_options;

/* ---- 版本与初始化 ------------------------------------------------------- */

/* 编译进库的 ABI 版本；跟头文件里的 OBSHARP_NATIVE_ABI_VERSION 比对。 */
OBSHARP_NATIVE_API uint32_t OBSHARP_NATIVE_CALL obsharp_native_abi_version(void);

/* 人类可读的版本串，形如 "0.1.0"。静态存储，永远有效。 */
OBSHARP_NATIVE_API const char *OBSHARP_NATIVE_CALL obsharp_native_version(void);

/*
 * 初始化：解析 obs-browser 模块、拿到 QCef 面板接口，并异步等待 CEF 就绪。
 * 幂等，可在任意线程调用。log_tag 是可选的日志前缀，可为 NULL。
 * 成功返回 OBSHARP_OK；进程里没有 obs-browser 时返回 OBSHARP_ERR_BROWSER_UNAVAILABLE。
 */
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_native_initialize(const char *log_tag);

/* CEF 面板是否已经可以创建 Widget（1 = 是，0 = 否）。任意线程可调用。 */
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_native_is_available(void);

/* 最近的错误描述；没有错误时返回空串。借用指针。 */
OBSHARP_NATIVE_API const char *OBSHARP_NATIVE_CALL obsharp_native_last_error(void);

/* 当前线程是不是 Qt 主线程（1 = 是，0 = 否）。 */
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_native_is_ui_thread(void);

/*
 * 在 Qt 主线程上同步执行 fn(user)。已经在主线程时直接执行。
 * 返回 OBSHARP_OK；从非主线程调用且主线程不可用时返回错误。
 */
typedef void(OBSHARP_NATIVE_CALL *obsharp_ui_action_fn)(void *user);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_native_invoke_ui(obsharp_ui_action_fn fn, void *user);

/* ---- 浏览器生命周期 ----------------------------------------------------- */

/*
 * 创建浏览器并返回句柄；失败返回 NULL（用 obsharp_native_last_error() 看原因）。
 * 必须在 Qt 主线程调用。callbacks 里的函数指针会在 UI 线程被调用。
 */
OBSHARP_NATIVE_API obsharp_browser *OBSHARP_NATIVE_CALL
obsharp_browser_create(const obsharp_browser_options *options);

/* 销毁浏览器。必须在 Qt 主线程调用；传 NULL 是空操作。 */
OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_browser_destroy(obsharp_browser *browser);

/*
 * 底层的 QWidget*。调用方可以自己 setParent / 塞进 dock，或者交给
 * obs_frontend_add_dock_by_id()。Widget 归浏览器所有，destroy 时会一并销毁，
 * 不要自己 delete。句柄无效时返回 NULL。
 */
OBSHARP_NATIVE_API void *OBSHARP_NATIVE_CALL obsharp_browser_widget(obsharp_browser *browser);

/* ---- 浏览器操作（全部要求 Qt 主线程） ---------------------------------- */

OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_load(obsharp_browser *browser, const char *url);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_reload(obsharp_browser *browser);
/* direction < 0 缩小，> 0 放大，== 0 复位。 */
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_zoom(obsharp_browser *browser, int32_t direction);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_execute_script(obsharp_browser *browser,
									     const char *script);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_set_startup_script(obsharp_browser *browser,
										 const char *script);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_allow_popups(obsharp_browser *browser, int32_t allow);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_resize(obsharp_browser *browser, int32_t width,
								     int32_t height);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_move(obsharp_browser *browser, int32_t x, int32_t y);
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_set_visible(obsharp_browser *browser, int32_t visible);

/*
 * 给页面发一条消息：会在页面里派发 CustomEvent("obsharp-message")，detail 是
 * 解析后的 json。json 必须是合法 JSON 文本（这里不校验，原样透传）。
 */
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_browser_post_message(obsharp_browser *browser,
									    const char *json);

/* 当前文档 URL；没有时返回空串。借用指针。 */
OBSHARP_NATIVE_API const char *OBSHARP_NATIVE_CALL obsharp_browser_current_url(obsharp_browser *browser);

/* ---- 传输辅助 ----------------------------------------------------------- */

/*
 * 探测 endpoint 是否可连接（用于在创建浏览器前确认 Kestrel 已经起来了）。
 * 返回 OBSHARP_OK / OBSHARP_ERR_ENDPOINT / OBSHARP_ERR_INVALID_ARGUMENT。
 */
OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_native_probe_endpoint(const char *endpoint,
									    int32_t endpoint_kind);

#ifdef __cplusplus
} /* extern "C" */
#endif
