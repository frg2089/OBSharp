#pragma once

// SchemeHandler.h：CEF scheme handler 桥的内部声明。
//
// 本库在 scheme handler 这一块**实现了 C++ 虚接口**（CefResourceHandler /
// CefSchemeHandlerFactory），因为那是 CEF 强制要求的：它们没有 C 入口，只能靠子类
// 填 vtable。但这两个子类是**零判断适配器** —— 它们不解析 URL、不判断这是不是控制
// 消息、不决定 framing、不滤响应头，每个回调原样转给 C# 函数指针（连返回值都听 C#）。
//
// 所以本文件里声明的类型都只承担两件事：
//   1) 让 CEF 觉得这是一个合规的 resource handler；
//   2) 保存 CEF 异步契约要求的「挂起回调」（Read 返回 0 / Skip 返回 0 时的那一个）。
//
// 真正的 HTTP 组包、分块解码、消息路由全在 C#（OBSharp.Interop/UI/Internal/）。

#include "Export.h"

#include <include/cef_base.h>
#include <include/cef_request.h>
#include <include/cef_resource_handler.h>
#include <include/cef_response.h>
#include <include/cef_scheme.h>

#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <vector>

namespace obsharp {

  // 一个 (scheme, domain) 键上注册的回调表。
  //
  // 按键存而不是按 handler 存：CEF 的 factory 是「一个键一个 factory、全局生效」，
  // 之后这个键上的每个请求都由同一个 factory 建 handler。所以回调表挂在键上，
  // 建 handler 时再拷一份进去。
  struct SchemeCallbacks {
    obsharp_resource_handler_callbacks callbacks{};

    // 防止空表被误用：全零的 callbacks 等于「什么都不回调」，注册它没有意义。
    bool IsValid() const { return callbacks.on_open != nullptr; }
  };

  // 在 CEF 的 UI 线程上同步执行 fn 并等它跑完。
  //
  // CefRegisterSchemeHandlerFactory 要求必须在 TID_UI 上调用，而我们被 C# 从 Qt
  // 主线程调进来 —— Windows / Linux 上这两个线程**不是同一个**，所以要往 CEF 的
  // 消息循环上投一次并等结果。
  //
  // 返回 false 表示没跑成：投递失败（CEF 正在退出）或者等待超时。
  // 超时是必要的：CEF 退出时挂上去的任务可能永远不会执行，不设上限就是挂死。
  bool RunOnCefUiThreadAndWait(const std::function<void()>& fn);

  // scheme handler 适配器。
  //
  // 生命周期由 CEF 的引用计数管（IMPLEMENT_REFCOUNTING）：C# 手上的 void* 只是
  // 借用指针，它拿它当每请求状态的 key。
  class KestrelResourceHandler : public CefResourceHandler {
  public:
    explicit KestrelResourceHandler(const SchemeCallbacks& callbacks) : callbacks_(callbacks) {}

    // CefResourceHandler
    bool Open(CefRefPtr<CefRequest> request, bool& handle_request,
      CefRefPtr<CefCallback> callback) override;
    void GetResponseHeaders(CefRefPtr<CefResponse> response, int64_t& response_length,
      CefString& redirect_url) override;
    bool Read(void* data_out, int bytes_to_read, int& bytes_read,
      CefRefPtr<CefResourceReadCallback> callback) override;
    bool Skip(int64_t bytes_to_skip, int64_t& bytes_skipped,
      CefRefPtr<CefResourceSkipCallback> callback) override;
    void Cancel() override;

    // resume_read / resume_skip 的目标：把挂起的回调续上。
    //
    // 这两个由 obsharp_resource_handler_resume_read/_skip 在 CEF 的 IO 线程上调用
    // （调用方那边已经做过线程封送）。
    void ResumeRead(int32_t bytes_available);
    void ResumeSkip(int64_t bytes_skipped);

  private:
    ~KestrelResourceHandler() override;

    SchemeCallbacks callbacks_;

    // 上一条 dllimport 里 C# 的 Destroy 回调只需要来一次。
    bool destroyed_ = false;

    // 挂起状态。只由 IO 线程读写（CEF 的 Read / Skip 与我们的 Resume* 都在 TID_IO
    // 上），但 Open 有可能被别处同步调进来，所以还是加锁 —— 这里的争用可以忽略不计，
    // 而「漏一次解锁」的代价是死锁。
    std::mutex mutex_;
    CefRefPtr<CefResourceReadCallback> pending_read_;
    CefRefPtr<CefResourceSkipCallback> pending_skip_;

    IMPLEMENT_REFCOUNTING(KestrelResourceHandler);
    DISALLOW_COPY_AND_ASSIGN(KestrelResourceHandler);
  };

  // scheme handler factory：给每个请求建一个 KestrelResourceHandler。
  class KestrelSchemeHandlerFactory : public CefSchemeHandlerFactory {
  public:
    explicit KestrelSchemeHandlerFactory(const SchemeCallbacks& callbacks) : callbacks_(callbacks) {}

    CefRefPtr<CefResourceHandler> Create(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
      const CefString& scheme_name, CefRefPtr<CefRequest> request) override;

  private:
    SchemeCallbacks callbacks_;

    IMPLEMENT_REFCOUNTING(KestrelSchemeHandlerFactory);
    DISALLOW_COPY_AND_ASSIGN(KestrelSchemeHandlerFactory);
  };

  // 已注册的 (scheme, domain) → 回调表。
  //
  // 由 obsharp_scheme_register 写入、Factory 创建时读取。用 map + mutex 而不是
  // unordered_map：键的数量是个位数，顺序无关紧要，但稳定遍历在排查时有用。
  std::map<std::string, SchemeCallbacks>& RegisteredCallbacks();

  // 读写上面那张表的锁。
  std::mutex& RegisteredCallbacksMutex();

  // 拼 (scheme, domain) 的键。
  std::string MakeSchemeKey(const std::string& scheme, const std::string& domain);

} // namespace obsharp
