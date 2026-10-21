/*
 * OBSharp.Interop.Native —— OB# 的底层原生 C ABI。
 *
 * 这个头文件只描述 ABI，不包含任何 C++ / Qt / CEF 类型。托管绑定写在
 * src/OBSharp.Interop/Native/（命名空间 OBSharp.Interop.Native），全部是
 * internal —— 原生导入的东西不对外暴露。两边的改动要同步。
 *
 * 设计边界（重要）
 * ----------------
 * 本库**不实现任何逻辑**，也不自己找宿主。它只做三件事：
 *
 *   1. 一行式转发（Qt / CEF 的值语义操作）；
 *   2. C++ 虚接口必须由 C++ 类实现的地方放零判断适配器，每个回调原样转给 C#
 *      函数指针，连返回值都听 C# 的；
 *   3. C 语言做不到的胶水：文档里 Qt 主线程 / CEF IO 线程的封送、模块符号查找。
 *
 * 判断、解析、状态机全部在 C#：URL 与 host 映射、引导脚本、控制消息派发、HTTP 组包、
 * 分块解码、命名管道 / UDS 连接、scheme 注册去重、窗口与文件对话框编排。
 *
 * 日志由托管侧注入
 * ----------------
 * 本库**不再自己解析 libobs 的 blog()**。托管侧在程序集加载时（[ModuleInitializer]）
 * 调用 obsharp_set_log_callback() 把日志函数指针交进来，本库内部所有日志都走它。
 * 这样一来「日志往哪里写」是 C# 的决定，原生侧只负责把字符串递过去。没注入时退回
 * stderr（仅用于托管侧还没起来的极早期）。
 *
 * 回调的传递方式
 * --------------
 * 需要「这个对象长期回调谁」时，函数指针在**构造那一刻**一次性传入，不提供事后再改的
 * setter：
 *   * 浏览器 widget 的 title / url 回调 → obsharp_qcef_create_widget() 的 callbacks 参数；
 *   * 顶层窗口的关闭回调 → obsharp_window_create() 的 callbacks 参数；
 *   * 事件过滤器 → obsharp_widget_install_event_filter()；
 *   * 一次文件对话框的完成回调 → obsharp_file_dialog_open()；
 *   * scheme handler 的回调 → obsharp_scheme_register()（factory 就是在这一刻建的）；
 *   * 日志 → obsharp_set_log_callback()（全局，程序集加载时注入）。
 *
 * 字符串一律是 NUL 结尾的 UTF-8。
 *
 * 线程模型
 * --------
 *   * obsharp_version / obsharp_set_log_callback / obsharp_is_ui_thread /
 *     obsharp_find_module / obsharp_find_symbol 可以在任意线程调用。
 *   * 其余函数**都要求 Qt 主线程**，除非注释另有说明。不在主线程时先
 *     obsharp_invoke_ui() 切过去（托管绑定自己会切）。
 *   * obsharp_resource_handler_resume_read / _resume_skip 例外：任意线程可调用，
 *     内部负责封送到 CEF 的 IO 线程。
 *
 * 所有权
 * ------
 *   * 传进来的字符串由调用方所有，函数返回后即可释放（本库一律当场复制）。
 *   * 返回的 const char* 归库所有：下一次同类调用、或对象销毁后失效。
 *   * 返回的 void* 句柄（widget / window / dialog / handler）全是**借用**的
 *     Qt / CEF 指针；销毁时机由 C# 决定，本库不额外加引用计数（CefResourceHandler
 *     除外：它本身是 CEF 引用计数对象）。
 *
 * 版本
 * ----
 * 只有 obsharp_version() 供排查「加载的是不是刚编出来那份」。不在运行期做 ABI 版本
 * 校验：托管绑定和原生库总是同一个仓库、同一次构建产出的，错配是构建流程的问题，
 * 不是运行期要处理的情况。
 */

#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

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
extern "C"
{
#endif

  /* 返回码。0 表示成功，负数是错误。 */
  typedef enum obsharp_result
  {
    OBSHARP_OK = 0,

    /* 参数不合法（空指针、空字符串……）。 */
    OBSHARP_ERR_INVALID_ARGUMENT = -1,
    /* 依赖的宿主对象不存在（obs-browser 模块、CEF、Qt 应用……）。 */
    OBSHARP_ERR_UNAVAILABLE = -2,
    /* 在错误的线程上调用了只能在特定线程调用的函数。 */
    OBSHARP_ERR_WRONG_THREAD = -3,
    /* 其它内部错误（CEF 任务投递失败、注册失败……）。 */
    OBSHARP_ERR_INTERNAL = -4
  } obsharp_result;

  /*
   * 日志级别：数值跟 libobs 的 blog() 一致（util/base.h），
   * 托管侧把 LogLevel 映射过来时不用再查一次表。
   */
  typedef enum obsharp_log_level
  {
    OBSHARP_LOG_ERROR = 500,
    OBSHARP_LOG_WARNING = 200,
    OBSHARP_LOG_INFO = 300,
    OBSHARP_LOG_DEBUG = 400
  } obsharp_log_level;

  /* ---- 版本、日志注入与线程 ----------------------------------------------- */

  /* 人类可读的版本串，来自构建时注入的 GitVersion（本地构建为 "0.0.0-local"）。
   * 静态存储，永远有效。 */
  OBSHARP_NATIVE_API const char* OBSHARP_NATIVE_CALL obsharp_version(void);

  /*
   * 托管侧注入的日志函数。message 是 NUL 结尾的 UTF-8，借用指针，回调返回后失效
   * （托管侧必须当场复制）。会在任意线程上被调用。
   */
  typedef void(OBSHARP_NATIVE_CALL* obsharp_log_fn)(obsharp_log_level level, const char* message);

  /*
   * 注册日志函数。任意线程可调用；重复调用以最后一次为准；传 NULL 表示取消注册
   * （之后退回 stderr）。
   *
   * 托管侧在 OBSharp.Interop 程序集加载时（[ModuleInitializer]）调用一次。
   */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_set_log_callback(obsharp_log_fn fn);

  /* 当前线程是不是 Qt 主线程。任意线程可调用。 */
  OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_is_ui_thread(void);

  /* 要在 Qt 主线程上同步执行的动作。 */
  typedef void(OBSHARP_NATIVE_CALL* obsharp_ui_action_fn)(void* user);

  /*
   * 在 Qt 主线程上同步执行 fn(user)。已经在主线程时直接执行。
   *
   * 用阻塞方式投递（Qt::BlockingQueuedConnection）：函数返回时 fn 已经跑完。
   * 注意：fn 内部**不要**再反向调用会阻塞等待调用方线程的东西，否则自锁。
   *
   * 返回 OBSHARP_OK；从非主线程调用且 Qt 应用已经不在了时返回错误。
   */
  OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_invoke_ui(obsharp_ui_action_fn fn, void* user);

  /* ---- 宿主模块里的符号查找 ----------------------------------------------- */

  /*
   * 找**已经加载**的模块，返回 OS 模块句柄（Windows HMODULE / Unix 的 dlopen 句柄）。
   * 从不因为查找而加载新模块。
   *
   * 名字由托管侧决定（Windows 试 "obs.dll"、"libobs.dll"，Unix 试
   * "libobs.so.0" / "libobs.so" ...）——平台差异属于 C# 的判断。
   *
   * 找不到返回 NULL。任意线程可调用。
   */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_find_module(const char* name);

  /*
   * 在模块里查符号（GetProcAddress / dlsym）。module 为 NULL 时按进程全局查
   * （dlsym(RTLD_DEFAULT) / 主程序）。
   *
   * 本库刻意不做 obs_get_module / obs_get_module_lib 那套封装：那属于「怎么找到
   * 宿主」的业务判断，由 C# 按平台分别决定。
   *
   * 找不到返回 NULL。任意线程可调用。
   */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_find_symbol(void* module, const char* name);

  /* ---- obs-browser 的 QCef 面板接口（一行式转发） ------------------------- */

  /*
   * cef 是 obs-browser 的 QCef*（由 C# 调 obs_browser_create_qcef 拿到）。
   * 这些函数只是去掉 C++ 虚函数调用的语法门槛，不含任何判断。全部要求 Qt 主线程。
   *
   * 这里刻意不 include obs-browser 的 GPL 头文件：只要虚函数声明顺序一致，
   * vtable 布局就一致（见 src/QtCefBridge.h 里的说明）。
   */
  OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_qcef_init_browser(void* cef);
  OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_qcef_is_initialized(void* cef);
  OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_qcef_wait_for_init(void* cef);

  /* QCefWidget 的 title / url 信号。value 是借用指针，回调返回后失效。 */
  typedef void(OBSHARP_NATIVE_CALL* obsharp_string_event_fn)(void* user, const char* value);

  typedef struct obsharp_cef_widget_callbacks
  {
    /* 原样透传给每个回调的上下文指针，不参与生命周期管理。 */
    void* user;
    /* 两个都可以为 NULL（表示不关心）。 */
    obsharp_string_event_fn on_title_changed;
    obsharp_string_event_fn on_url_changed;
  } obsharp_cef_widget_callbacks;

  /*
   * 建一个 QCefWidget*。用法语义与 QCef::create_widget 完全一致，返回的是
   * 借用指针（widget 归调用方释放）。cookie_manager 可以为 NULL。
   *
   * url 由 C# 拼好（含 host→domain 映射），本库不做任何改写。
   *
   * 回调在**这里**一次性接管：本库不需要、也不提供事后再改回调的接口。callbacks
   * 可以为 NULL（表示这个 widget 不关心 title / url）。本库当场复制结构体内容，
   * 调用方传完即可回收。
   */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_qcef_create_widget(void* cef, void* parent, const char* url,
    void* cookie_manager, const obsharp_cef_widget_callbacks* callbacks);

  /* ---- QCefWidget 操作（一行式转发） -------------------------------------- */

  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_set_url(void* widget, const char* url);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_set_startup_script(void* widget, const char* script);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_allow_all_popups(void* widget, bool allow);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_reload(void* widget);
  /* direction < 0 缩小，> 0 放大，== 0 复位。返回 true 表示成功。 */
  OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_cef_widget_zoom(void* widget, int32_t direction);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_execute_script(void* widget, const char* script);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_close_browser(void* widget);

  /*
   * 断开创建时传入的 title / url 回调（把回调表清空，不销毁 widget）。
   *
   * 这不是「事后改订阅」——重新订阅仍然只能在 create_widget 时做。它存在只是因为
   * 拆除顺序有要求：托管侧释放 GCHandle 之前必须保证不会再有回调打进来，而 widget
   * 可能已经被 OBS 的停靠面板收养、比托管对象活得久。没有它就只能靠 widget 销毁来
   * 兜底，收养那条路径会在 GCHandle 释放之后收到信号（野指针）。
   */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_cef_widget_detach_callbacks(void* widget);

  /* ---- QWidget 通用操作（一行式转发） ------------------------------------- */

  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_resize(void* widget, int32_t width, int32_t height);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_move(void* widget, int32_t x, int32_t y);
  /* 读出当前尺寸。width / height 都可以为 NULL。widget 为 NULL 时写 0。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_size(void* widget, int32_t* width, int32_t* height);

  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_set_visible(void* widget, bool visible);
  OBSHARP_NATIVE_API bool OBSHARP_NATIVE_CALL obsharp_widget_is_visible(void* widget);

  /* show() + raise()。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_show(void* widget);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_hide(void* widget);
  /* update()：触发一次重绘。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_update(void* widget);

  /* deleteLater()：推迟到事件循环里销毁。之后这个指针立即失效。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_delete_later(void* widget);

  /* setParent(parent)；parent 为 NULL 时脱离父级。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_set_parent(void* widget, void* parent);
  /* 当前父级；没有时返回 NULL。 */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_widget_parent(void* widget);

  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_set_window_title(void* widget, const char* title);
  /* true 时置顶（Qt::WindowStaysOnTopHint）。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_set_topmost(void* widget, bool topmost);
  /* setFixedSize(w, h)。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_set_fixed_size(void* widget, int32_t width, int32_t height);

  /* Qt::WA_TranslucentBackground + setAutoFillBackground(false)。 */
  OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_widget_set_translucent_background(void* widget);
  /* 用 0xAARRGGBB 铺底色（palette + setAutoFillBackground(true)）。 */
  OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_widget_set_background_color(void* widget, uint32_t argb);

  /*
   * 把一个 widget 摆进容器：容器上没有布局就建一个边距 / 间距全 0 的 QVBoxLayout，
   * 然后把旧内容摘掉、把 child 加进去。child 为 NULL 时只清空。
   *
   * 本库**不拥有** child，也不会在容器销毁时删它。
   */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_set_content(void* container, void* child);

  /*
   * 按 OS 窗口 id 找 QWidget（Windows 上是 HWND → QWidget::find）。找不到返回 NULL。
   *
   * 「先在 HWND 里找、找不到再当成 QWidget* 用」这个判断在 C# 侧。
   */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_widget_find(uintptr_t native_id);

  /*
   * 事件过滤器的回调。在 Qt 主线程上被调用，event_type 是 QEvent::Type 的数值。
   * 返回 true 表示**吃掉**这个事件，false 表示继续正常派发。
   *
   * 「哪些事件要拦、要不要顺便踢一下重绘」全是 C# 的判断，本库不做。
   */
  typedef bool(OBSHARP_NATIVE_CALL* obsharp_filter_event_fn)(void* user, void* widget, int32_t event_type);

  /*
   * 装一个事件过滤器，返回的 token 用于撤掉它。fn 为 NULL 时返回 NULL。
   *
   * 一个 widget 可以装多个过滤器，各自独立，同一个 fn 也可以装在多个 widget 上。
   */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL
    obsharp_widget_install_event_filter(void* widget, obsharp_filter_event_fn fn, void* user);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_widget_remove_event_filter(void* widget, void* filter);

  /* ---- 普通顶层窗口（QCef 的容器） ---------------------------------------- */

  /* 用户点了窗口的关闭按钮。此时窗口已经被隐藏（不是销毁）。 */
  typedef void(OBSHARP_NATIVE_CALL* obsharp_event_fn)(void* user);

  typedef struct obsharp_window_callbacks
  {
    void* user;
    /* 可以为 NULL。 */
    obsharp_event_fn on_close_requested;
  } obsharp_window_callbacks;

  /*
   * 建一个空的顶层窗口（Qt::Window），返回它的 QWidget*。
   *
   * 这是个**纯容器**：本库只提供一个「关闭时隐藏并回调、不销毁」的 QWidget 子类，
   * 标题 / 尺寸 / 置顶 / 是否可缩放全由 C# 随后用 obsharp_widget_* 摆布
   * （set_window_title / resize / set_fixed_size / set_topmost / show）。
   *
   * 回调在这里一次性接管；callbacks 可以为 NULL。本库当场复制结构体内容。
   *
   * 销毁：C# 先 obsharp_widget_set_content(window, NULL)，再
   * obsharp_widget_hide + obsharp_widget_delete_later。
   */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_window_create(const obsharp_window_callbacks* callbacks);

  /* ---- 文件选择对话框（QFileDialog，一行式转发） -------------------------- */

  /* QFileDialog::AcceptMode 的取值。 */
  typedef enum obsharp_file_dialog_accept_mode
  {
    OBSHARP_FILE_DIALOG_ACCEPT_OPEN = 0,
    OBSHARP_FILE_DIALOG_ACCEPT_SAVE = 1
  } obsharp_file_dialog_accept_mode;

  /* QFileDialog::FileMode 的取值（只保留这个库用得到的四种）。 */
  typedef enum obsharp_file_dialog_file_mode
  {
    OBSHARP_FILE_DIALOG_FILE_EXISTING_FILE = 0,
    OBSHARP_FILE_DIALOG_FILE_EXISTING_FILES = 1,
    OBSHARP_FILE_DIALOG_FILE_ANY_FILE = 2,
    OBSHARP_FILE_DIALOG_FILE_DIRECTORY = 3
  } obsharp_file_dialog_file_mode;

  /*
   * 建一个 QFileDialog（不显示）。parent 为 NULL 时没有父级（C# 负责传
   * QApplication::activeWindow 对应的那个 widget，见 obsharp_active_window）。
   *
   * 建好之后由 C# 逐项设置参数，再调 obsharp_file_dialog_open 显示。
   */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_file_dialog_create(void* parent);

  /* 当前活动窗口的 QWidget*；没有时返回 NULL。 */
  OBSHARP_NATIVE_API void* OBSHARP_NATIVE_CALL obsharp_active_window(void);

  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_title(void* dialog, const char* title);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_directory(void* dialog, const char* directory);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_name_filter(void* dialog, const char* filter);
  /* true 时追加 QDir::Hidden | QDir::System。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_show_hidden(void* dialog, bool show);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_accept_mode(void* dialog,
    obsharp_file_dialog_accept_mode accept_mode);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_file_mode(void* dialog,
    obsharp_file_dialog_file_mode file_mode);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_default_suffix(void* dialog, const char* suffix);
  /* true 时设 QFileDialog::ShowDirsOnly。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_set_show_dirs_only(void* dialog, bool value);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_file_dialog_select_file(void* dialog, const char* filename);

  /*
   * 完成回调。cancelled 为 true 表示用户取消（或对话框被外部销毁）。
   *
   * 保证：
   *   * 只在 open 返回 OBSHARP_OK 之后被调用；
   *   * 在 Qt 主线程上调用，每次 open **恰好一次**；
   *   * dialog 指针在回调期间仍然有效，C# 可以在里面调
   *     obsharp_file_dialog_selected_count / _selected_path 读结果；
   *   * 回调返回后本库立刻 deleteLater 掉 dialog，C# 不要再碰它。
   */
  typedef void(OBSHARP_NATIVE_CALL* obsharp_file_dialog_done_fn)(void* user, void* dialog, bool cancelled);

  /*
   * 用 QFileDialog::open() 显示窗口模态对话框，函数本身**立即返回**，不阻塞。
   * 之后一定会收到一次 done 回调。返回值只表示有没有启动成功。
   *
   * 调用之后 dialog 的所有权转到本库，C# 不要再销毁它。
   */
  OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL
    obsharp_file_dialog_open(void* dialog, obsharp_file_dialog_done_fn done, void* user);

  /* 选中的路径数量（在 done 回调里读）。 */
  OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_file_dialog_selected_count(void* dialog);
  /* 第 index 个选中路径（UTF-8，借用指针，在 done 回调里读）。越界返回 NULL。 */
  OBSHARP_NATIVE_API const char* OBSHARP_NATIVE_CALL obsharp_file_dialog_selected_path(void* dialog, int32_t index);

  /* ---- scheme handler 桥（把 CEF 的同步 / 异步读接口转给 C#） -------------- */

  /* 一个请求头（name / value 都是 UTF-8，借用指针）。 */
  typedef struct obsharp_request_header
  {
    const char* name;
    const char* value;
  } obsharp_request_header;

  /*
   * CefResourceHandler 的 C# 后端。
   *
   * 线程：除 *_resume_* 外，所有回调都在 **CEF 的 IO 线程**上，不是 Qt 主线程。
   * 回调里不要碰 Qt，需要的话自己封送（本库提供 obsharp_invoke_ui）。
   *
   * handler 是本次请求的句柄（CefResourceHandler* 的借用形式），C#
   * 用它当 key 挂自己的每请求状态；请求结束时本库调 destroy 通知清理。
   */
  typedef struct obsharp_resource_handler_callbacks
  {
    /* 原样透传的上下文指针，不参与生命周期管理。 */
    void* user;

    /*
     * 请求到达。本库已经把 CefRequest 拆成裸数据：方法、完整 URL、请求头数组、
     * POST body 的原始字节。
     *
     * URL 的解析（拆 host / path / query）、控制消息的判断、要不要连 Kestrel，
     * 全在 C#。
     *
     * 返回 OBSHARP_OK 表示接着走 get_response_headers；返回负数表示让这次请求
     * 失败（CEF 会当成 ERR_FAILED）。
     */
    int32_t(OBSHARP_NATIVE_CALL* on_open)(void* user, void* handler, const char* method, const char* url,
      const obsharp_request_header* headers, int32_t header_count, const void* body, int32_t body_length);

    /*
     * 填响应头。C# 通过 obsharp_response_* 访问器写 CefResponse。
     *
     * 返回值是 body 长度：>= 0 表示已知长度，-1 表示未知（一直读到 read 返回负数）。
     * 本库不支持重定向（redirect 永远清空）。
     */
    int64_t(OBSHARP_NATIVE_CALL* get_response_headers)(void* user, void* handler, void* response);

    /*
     * 读 body。C# 往 buffer 里最多写 size 字节，返回：
     *   > 0  实际写入的字节数；
     *   == 0 暂时没有数据：本库挂起这次读，稍后由 C# 调
     *        obsharp_resource_handler_resume_read 续上；
     *   < 0  body 结束。
     */
    int32_t(OBSHARP_NATIVE_CALL* read)(void* user, void* handler, void* buffer, int32_t size);

    /*
     * 跳过 body 里的字节（CEF 处理 Range 请求时用）。返回：
     *   > 0  已经跳过了 *skipped 字节；
     *   == 0 暂时跳不了：本库挂起，稍后由 C# 调
     *        obsharp_resource_handler_resume_skip 续上（参数是**累计**跳过字节数）；
     *   < 0  失败。
     */
    int32_t(OBSHARP_NATIVE_CALL* skip)(void* user, void* handler, int64_t bytes_to_skip, int64_t* skipped);

    /* 请求被取消：之后不会再有这个 handler 的任何回调（destroy 除外）。 */
    void(OBSHARP_NATIVE_CALL* cancel)(void* user, void* handler);

    /* handler 即将销毁：释放挂在它上面的每请求状态。 */
    void(OBSHARP_NATIVE_CALL* destroy)(void* user, void* handler);
  } obsharp_resource_handler_callbacks;

  /*
   * 给 <scheme>://<domain>/... 注册 scheme handler factory：之后这个键上的每个
   * 请求都会新建一个 handler，回调走 callbacks（在**这里**一次性接管；本库当场
   * 复制结构体内容，调用方传完即可回收）。
   *
   * 本库负责切到 CEF 的 UI 线程（CefRegisterSchemeHandlerFactory 的要求）并等它
   * 跑完。「这个键注册过没有 / 端点换了要不要警告」属于 C# 的判断。
   *
   * 必须在 Qt 主线程调用。
   */
  OBSHARP_NATIVE_API int32_t OBSHARP_NATIVE_CALL obsharp_scheme_register(const char* scheme, const char* domain,
    const obsharp_resource_handler_callbacks* callbacks);

  /* 写响应。全部要求 CEF IO 线程（也就是在 get_response_headers 里调）。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_response_set_status(void* response, int32_t status);
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_response_set_status_text(void* response, const char* text);
  /*
   * 关键：CefResponse 的 mime_type 和 Content-Type 头是两个独立字段，只设
   * header map 的话 CEF 的加载器读不到类型，HTML 不会被当成文档解析。C# 侧要
   * 单独调一次这个函数。
   */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_response_set_mime_type(void* response, const char* mime_type);
  /*
   * 整体替换响应头。「哪些头要滤掉（Content-Length / Connection /
   * Transfer-Encoding）」是 C# 的判断，本库原样照抄。
   */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL obsharp_response_set_headers(void* response,
    const obsharp_request_header* headers, int32_t header_count);

  /*
   * 续上被挂起的读 / 跳。**任意线程可调用**：内部封送到 CEF 的 IO 线程再执行，
   * 期间会对 handler 加引用，所以 handler 在此期间被销毁也是安全的。
   *
   * 每次挂起只续一次；多调是空操作。
   */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL
    obsharp_resource_handler_resume_read(void* handler, int32_t bytes_available);
  /* bytes_skipped 是**累计**已跳过的字节数（不是增量）。 */
  OBSHARP_NATIVE_API void OBSHARP_NATIVE_CALL
    obsharp_resource_handler_resume_skip(void* handler, int64_t bytes_skipped);

#ifdef __cplusplus
} /* extern "C" */
#endif
