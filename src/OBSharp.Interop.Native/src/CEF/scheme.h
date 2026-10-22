#pragma once

// scheme.h：CEF scheme handler 桥。
//
// 本库在这一块**实现了 C++ 虚接口**（CefResourceHandler / CefSchemeHandlerFactory），
// 因为那是 CEF 强制要求的：它们没有 C 入口，只能靠子类填 vtable。但这两个子类是
// **零判断适配器** —— 它们不解析 URL、不判断这是不是控制消息、不决定 framing、
// 不滤响应头，每个回调原样转给托管函数指针（连返回值都听调用方）。
//
// 所以这里声明的类型只承担两件事：
//   1) 让 CEF 觉得这是一个合规的 resource handler；
//   2) 保存 CEF 异步契约要求的「挂起回调」（Read / Skip 走「稍后继续」时的那一个）。
//
// 真正的 HTTP 组包、分块解码、消息路由全在托管侧。

#include "obsharp.h"

#include <include/cef_base.h>
#include <include/cef_request.h>
#include <include/cef_resource_handler.h>
#include <include/cef_response.h>
#include <include/cef_scheme.h>

#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_set>

namespace OBSharp::Cef
{
  // 一个请求头（name / value 都是 UTF-8 借用指针，只在所属回调期间有效）。
  struct obsharp_request_header
  {
    const char* name;
    const char* value;
  };

  // 每请求的 handler 句柄（借用）。实现在 scheme.cpp，生命周期由 CEF 的引用计数
  // 管；托管侧拿它当每请求状态表的 key，请求结束时由 destroy 回调通知清理。
  class ResourceHandler;

  // ---- 回调表 ----------------------------------------------------------------
  //
  // 线程：除 *_resume_* 外，所有回调都在 **CEF 的 IO 线程**上，不是 Qt 主线程。
  // 回调里不要碰 Qt，需要的话自己封送（obsharp_ui_invoke 只封送 Qt 主线程）。

  // 请求到达。本库已经把 CefRequest 拆成裸数据：方法、完整 URL、请求头数组、
  // POST body 的原始字节。URL 的解析（拆 host / path / query）、控制消息的判断
  // 全在托管侧。
  //
  // 返回 >= 0 表示接着走 get_response_headers；返回 < 0 表示让这次请求立刻失败
  // （CEF 当成 ERR_FAILED）。
  typedef int(OBSHARP_CALL* obsharp_resource_handler_open_fn)(void* user, ResourceHandler* handler,
    const char* method, const char* url, const obsharp_request_header* headers, int header_count,
    const void* body, int body_length);

  // 填响应头。调用方通过 obsharp_response_* 访问器写 CefResponse。
  //
  // 返回值是 body 长度：>= 0 表示已知长度，-1 表示未知（一直读到 read 返回「结束」）。
  // 本库不支持重定向（redirect 永远清空）。
  typedef long long(OBSHARP_CALL* obsharp_resource_handler_response_headers_fn)(void* user,
    ResourceHandler* handler, CefResponse* response);

  // 读 body。往 buffer 里最多写 size 字节，返回：
  //   > 0  实际写入的字节数；
  //   == 0 暂时没有数据：本库挂起这次读，稍后由调用方
  //        obsharp_resource_handler_resume_read 续上；
  //   < 0  body 结束。
  typedef int(OBSHARP_CALL* obsharp_resource_handler_read_fn)(void* user, ResourceHandler* handler,
    void* buffer, int size);

  // 跳过 body 里的字节（CEF 处理 Range 请求时用）。返回：
  //   > 0  已经跳过了 *skipped 字节；
  //   == 0 暂时跳不了：本库挂起，稍后由调用方
  //        obsharp_resource_handler_resume_skip 续上（参数是**累计**跳过字节数）；
  //   < 0  失败。
  typedef int(OBSHARP_CALL* obsharp_resource_handler_skip_fn)(void* user, ResourceHandler* handler,
    long long bytes_to_skip, long long* skipped);

  // 请求被取消：之后不会再有这个 handler 的任何回调（destroy 除外）。
  typedef void(OBSHARP_CALL* obsharp_resource_handler_event_fn)(void* user, ResourceHandler* handler);

  struct obsharp_resource_handler_callbacks
  {
    // 原样透传给每个回调的上下文指针，不参与生命周期管理。
    void* user;
    obsharp_resource_handler_open_fn on_open;
    obsharp_resource_handler_response_headers_fn get_response_headers;
    obsharp_resource_handler_read_fn read;
    obsharp_resource_handler_skip_fn skip;
    // 请求被取消。
    obsharp_resource_handler_event_fn cancel;
    // handler 即将销毁：释放挂在它上面的每请求状态。
    obsharp_resource_handler_event_fn destroy;
  };

  // ---- 导出 ------------------------------------------------------------------

  /*
   * 给 <scheme>://<domain>/... 注册 scheme handler factory：之后这个键上的每个请求
   * 都会新建一个 handler，回调走 callbacks（在**这里**一次性接管；本库当场复制
   * 结构体内容，调用方传完即可回收）。
   *
   * 任意线程可调用。本库负责切到 CEF 的 UI 线程（CefRegisterSchemeHandlerFactory
   * 的要求）并等它跑完。「这个键注册过没有、端点换了要不要警告」是调用方的判断。
   *
   * on_open 为 NULL 时返回 false：没有它每个请求都会被拒，注册没有意义。
   */
  OBSHARP_API bool OBSHARP_CALL obsharp_scheme_register(const char* scheme, const char* domain,
    const obsharp_resource_handler_callbacks* callbacks);

  // 写响应。全部要求 CEF IO 线程（也就是在 get_response_headers 回调里调）。
  OBSHARP_API void OBSHARP_CALL obsharp_response_set_status(CefResponse* response, int status);
  OBSHARP_API void OBSHARP_CALL obsharp_response_set_status_text(CefResponse* response, const char* text);
  /*
   * 关键：CefResponse 的 mime_type 和 Content-Type 头是两个独立字段，只设 header map
   * 的话 CEF 的加载器读不到类型，HTML 不会被当成文档解析。调用方要单独调一次这个。
   */
  OBSHARP_API void OBSHARP_CALL obsharp_response_set_mime_type(CefResponse* response, const char* mime_type);
  /*
   * 整体替换响应头。「哪些头要滤掉（Content-Length / Connection /
   * Transfer-Encoding）」是调用方的判断，本库原样照抄。
   */
  OBSHARP_API void OBSHARP_CALL obsharp_response_set_headers(CefResponse* response,
    const obsharp_request_header* headers, int header_count);

  /*
   * 续上被挂起的读 / 跳。**任意线程可调用**：内部封送到 CEF 的 IO 线程再执行，
   * 期间会对 handler 加引用，所以 handler 在此期间被销毁也是安全的（查无此 handler
   * 时直接丢弃这次唤醒）。
   *
   * 每次挂起只续一次；多调是空操作。
   */
  OBSHARP_API void OBSHARP_CALL obsharp_resource_handler_resume_read(ResourceHandler* handler,
    int bytes_available);
  // bytes_skipped 是**累计**已跳过的字节数（不是增量）。
  OBSHARP_API void OBSHARP_CALL obsharp_resource_handler_resume_skip(ResourceHandler* handler,
    long long bytes_skipped);
}
