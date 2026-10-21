#include "SchemeHandler.h"

#include "Log.h"

#include <include/cef_parser.h>
#include <include/cef_task.h>

#include <chrono>
#include <condition_variable>
#include <cstring>
#include <memory>

// SchemeHandler.cpp：CEF scheme handler 的桥。
//
// 这一块是整个原生库里唯一「有点实现」的地方，但请注意它**仍然不做任何判断**：
//
//   * 这个 URL 是该连 Kestrel 还是本地 204 响应？（C#）
//   * 响应头里哪些要滤掉、mime_type 设成什么？（C#）
//   * 分块的 CRLF 该怎么吃、body 什么时候结束？（C#）
//   * 同一个 (scheme, domain) 注册过没有、端点变了要不要警告？（C#）
//
// 它只负责两件 C# 做不到的事：
//   1) 填 vtable —— CefResourceHandler 和 CefSchemeHandlerFactory 没有 C 入口；
//   2) 保存 CEF 异步契约要求的挂起回调 —— Read 返回 0 时 CEF 要求「记住 callback、
//      bytes_read = 0、返回 true」，这个机械动作必须由持有 CefRefPtr 的这一层做。
//
// 所有回调都带着 handler 自己的指针（this）过去：C# 拿它当每请求状态表的 key，
// 因为同一个键上可能同时有几十个请求在飞。

namespace obsharp {

  namespace {

    // 投到 CEF UI 线程上的同步任务。
    //
    // 为什么需要：CefRegisterSchemeHandlerFactory 要求必须在 TID_UI 上调用，而我们
    // 从 Qt 主线程进来（Windows / Linux 上这两个是不同线程）。
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

    // CEF 的任务可能永远不跑（CEF 正在退出），所以必须有上限。
    constexpr int kCefTaskTimeoutMs = 5000;

    // 把 request 里的头搬成 C 结构数组。
    //
    // 注意借用语义：obsharp_request_header 里的 name/value 指向下面这个 vector 里
    // 的 std::string，**只在回调期间有效**。C# 侧必须在 on_open 返回前拷走 ——
    // 它确实是这么做的（转成托管字符串）。
    std::vector<std::string> BuildHeaderStorage(const CefRequest::HeaderMap& headers,
      std::vector<obsharp_request_header>& out) {
      std::vector<std::string> storage;
      storage.reserve(headers.size() * 2);
      out.reserve(headers.size());

      for (const auto& header : headers) {
        storage.push_back(header.first.ToString());
        const std::string value = header.second.ToString();
        storage.push_back(value);
      }

      // storage 里是 [name0, value0, name1, value1, ...]，指针成对指过去。
      for (size_t i = 0; i < headers.size(); ++i) {
        out.push_back(obsharp_request_header{storage[i * 2].c_str(), storage[i * 2 + 1].c_str()});
      }

      return storage;
    }

  } // namespace

  bool RunOnCefUiThreadAndWait(const std::function<void()>& fn) {
    if (!fn)
      return false;

    // 已经在 CEF UI 线程上：直调。这时候再 CefPostTask 出去等自己就是自锁。
    if (CefCurrentlyOn(TID_UI)) {
      fn();
      return true;
    }

    CefRefPtr<SyncTask> task = new SyncTask(fn);
    if (!CefPostTask(TID_UI, task)) {
      LogF(OBSHARP_LOG_WARNING, "CefPostTask(TID_UI) failed; CEF is probably shutting down");
      return false;
    }

    if (!task->WaitFor(std::chrono::milliseconds(kCefTaskTimeoutMs))) {
      LogF(OBSHARP_LOG_WARNING, "timed out waiting for a task on the CEF UI thread");
      return false;
    }

    return true;
  }

  std::map<std::string, SchemeCallbacks>& RegisteredCallbacks() {
    static std::map<std::string, SchemeCallbacks> callbacks;
    return callbacks;
  }

  std::mutex& RegisteredCallbacksMutex() {
    static std::mutex mutex;
    return mutex;
  }

  std::string MakeSchemeKey(const std::string& scheme, const std::string& domain) {
    return scheme + "://" + domain;
  }

  // ---- KestrelResourceHandler -------------------------------------------------

  KestrelResourceHandler::~KestrelResourceHandler() {
    // 通知 C# 释放挂在 handler 上的每请求状态。
    //
    // 这个指针马上就不能用了，所以 C# 必须在这里（且只在这里）把状态摘掉；
    // 之后再有 resume 调用会被 CefPostTask 之后发现 handler 已死而丢弃。
    if (!destroyed_ && callbacks_.callbacks.destroy) {
      destroyed_ = true;
      callbacks_.callbacks.destroy(callbacks_.callbacks.user, this);
    }
  }

  bool KestrelResourceHandler::Open(CefRefPtr<CefRequest> request, bool& handle_request,
    CefRefPtr<CefCallback> callback) {
    (void)callback; // 同步完成，不用它

    handle_request = false;

    if (!request)
      return false;

    const obsharp_resource_handler_callbacks& c = callbacks_.callbacks;
    if (!c.on_open)
      return false;

    // 把 CefRequest 拆成裸数据：方法、URL、头数组、POST body 的原始字节。
    // 这里**不解释** URL（不拆 host / path / query）—— 那是 C# 的事。
    const std::string method = request->GetMethod().ToString();
    const std::string url = request->GetURL().ToString();

    CefRequest::HeaderMap header_map;
    request->GetHeaderMap(header_map);

    std::vector<obsharp_request_header> headers;
    const std::vector<std::string> header_storage = BuildHeaderStorage(header_map, headers);

    // POST body：只拼 PDE_TYPE_BYTES 的元素（文件上传的 FILE 类型没有内存表示，
    // 跳过它。C# 那一侧的 scheme handler 只关心 JSON 控制消息，从不发文件上传）。
    std::string body;
    if (CefRefPtr<CefPostData> post_data = request->GetPostData()) {
      CefPostData::ElementVector elements;
      post_data->GetElements(elements);
      for (const auto& element : elements) {
        if (element->GetType() != PDE_TYPE_BYTES)
          continue;

        const size_t size = element->GetBytesCount();
        if (size == 0)
          continue;

        std::string bytes(size, '\0');
        if (element->GetBytes(size, bytes.data()))
          body += bytes;
      }
    }

    const int32_t result = c.on_open(c.user, this, method.c_str(), url.c_str(), headers.data(),
      static_cast<int32_t>(headers.size()), body.empty() ? nullptr : static_cast<const void*>(body.data()),
      static_cast<int32_t>(body.size()));

    // 负数 = 让这次请求失败（CEF 会当成 ERR_FAILED）。
    if (result < 0)
      return false;

    // 同步完成：handle_request = true 表示「响应头已经准备好，接着调
    // GetResponseHeaders」，不需要 callback（CEF 的另一种用法是异步 Open）。
    handle_request = true;
    return true;
  }

  void KestrelResourceHandler::GetResponseHeaders(CefRefPtr<CefResponse> response,
    int64_t& response_length, CefString& redirect_url) {
    response_length = 0;

    const obsharp_resource_handler_callbacks& c = callbacks_.callbacks;
    if (c.get_response_headers && response) {
      // C# 负责写 status / status_text / mime_type / headers，返回 body 长度。
      // 本库只是把响应对象交出去，并把它返回的 int64 原样交给 CEF。
      response_length = c.get_response_headers(c.user, this, static_cast<void*>(response.get()));
    }

    // 不支持重定向：清空就等于「这不是重定向响应」。C# 想跳转的话自己设 3xx +
    // Location 头（CEF 会在页面里发一个新请求）。
    redirect_url.clear();
  }

  bool KestrelResourceHandler::Read(void* data_out, int bytes_to_read, int& bytes_read,
    CefRefPtr<CefResourceReadCallback> callback) {
    bytes_read = 0;

    const obsharp_resource_handler_callbacks& c = callbacks_.callbacks;
    if (!c.read)
      return false;

    const int32_t result = c.read(c.user, this, data_out, bytes_to_read);

    if (result > 0) {
      bytes_read = result;
      return true;
    }

    if (result == 0) {
      // **必须 return true。**
      //
      // 按 cef_resource_handler.h 的契约：「把 data_out 记下来、bytes_read 置 0、
      // return true，稍后执行 callback」。返回 false 会被 CEF 当成「响应已经结束」，
      // 页面就被截断在缓冲区末尾（旧实现实测到的现象：DOM 停在 HTML 实体中间，
      // 比如 "...OB# 插&amp;#..."）。
      //
      // 数据到达时由 C# 调 obsharp_resource_handler_resume_read 把这个读续上，
      // CEF 随后会再调一次 Read。
      {
        std::lock_guard<std::mutex> lock(mutex_);
        pending_read_ = callback;
      }
      return true;
    }

    // result < 0：body 结束。这是唯一该返回 false 的情况。
    return false;
  }

  bool KestrelResourceHandler::Skip(int64_t bytes_to_skip, int64_t& bytes_skipped,
    CefRefPtr<CefResourceSkipCallback> callback) {
    bytes_skipped = 0;

    const obsharp_resource_handler_callbacks& c = callbacks_.callbacks;
    if (!c.skip)
      return false;

    int64_t skipped = 0;
    const int32_t result = c.skip(c.user, this, bytes_to_skip, &skipped);

    if (result > 0) {
      bytes_skipped = skipped;
      return true;
    }

    if (result == 0) {
      // 跟 Read 相反：Skip 的契约里「返回 false + 挂起 callback」才是合法的
      // 「稍后继续」。CEF 会等我们调 callback->Continue(累计跳过字节数)。
      {
        std::lock_guard<std::mutex> lock(mutex_);
        pending_skip_ = callback;
      }
      return false;
    }

    return false;
  }

  void KestrelResourceHandler::Cancel() {
    // 先把挂起的回调丢掉（Cancel 之后它们不该再被续上），再通知 C#。
    {
      std::lock_guard<std::mutex> lock(mutex_);
      pending_read_ = nullptr;
      pending_skip_ = nullptr;
    }

    const obsharp_resource_handler_callbacks& c = callbacks_.callbacks;
    if (c.cancel)
      c.cancel(c.user, this);
  }

  void KestrelResourceHandler::ResumeRead(int32_t bytes_available) {
    CefRefPtr<CefResourceReadCallback> callback;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      callback = pending_read_;
      pending_read_ = nullptr; // 取走即清空：多调一次也只生效一次
    }

    if (callback)
      callback->Continue(bytes_available);
  }

  void KestrelResourceHandler::ResumeSkip(int64_t bytes_skipped) {
    CefRefPtr<CefResourceSkipCallback> callback;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      callback = pending_skip_;
      pending_skip_ = nullptr;
    }

    if (callback)
      callback->Continue(bytes_skipped);
  }

  // ---- KestrelSchemeHandlerFactory --------------------------------------------

  CefRefPtr<CefResourceHandler> KestrelSchemeHandlerFactory::Create(CefRefPtr<CefBrowser> browser,
    CefRefPtr<CefFrame> frame, const CefString& scheme_name, CefRefPtr<CefRequest> request) {
    // CEF 每个请求都会调一次；我们只负责建 handler，怎么处理请求是 C# 的事。
    //
    // browser / frame / scheme_name / request 都不看：handler 需要的所有信息都在
    // 它自己的 Open() 里从 request 取。
    (void)browser;
    (void)frame;
    (void)scheme_name;
    (void)request;

    return new KestrelResourceHandler(callbacks_);
  }

} // namespace obsharp

// ---------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------

namespace {

  // 从挂起回调所属的 handler 指针还原 C++ 类型。
  //
  // 不做类型校验（C# 保证是它自己从 on_open 拿到的那个指针）。
  obsharp::KestrelResourceHandler* AsHandler(void* handler) {
    return static_cast<obsharp::KestrelResourceHandler*>(handler);
  }

} // namespace

// resume_read / resume_skip 由**任意线程**调用，所以要封送到 CEF 的 IO 线程上。
//
// 为什么要持引用：CefPostTask 到真正执行之间可能隔一段时间，期间 CEF 完全可能把
// 这个 handler 的最后一个引用放掉（比如页面关闭、请求取消）。CefRefPtr 拷贝会
// 增加引用计数，任务执行时对象一定还活着。
//
// 另外注意：CefPostTask 本身在 CEF 正在退出时会失败，此时什么都不做即可 ——
// 没有请求可续，也就没有「丢了」这回事。
namespace {

  class ResumeTask : public CefTask {
  public:
    ResumeTask(CefRefPtr<obsharp::KestrelResourceHandler> handler, int32_t bytes_available,
      int64_t bytes_skipped, bool is_skip)
      : handler_(std::move(handler)),
        bytes_available_(bytes_available),
        bytes_skipped_(bytes_skipped),
        is_skip_(is_skip) {
    }

    void Execute() override {
      if (!handler_)
        return;

      // 到这儿已经在 IO 线程上了，可以安全地碰 pending 回调。
      if (is_skip_)
        handler_->ResumeSkip(bytes_skipped_);
      else
        handler_->ResumeRead(bytes_available_);
    }

  private:
    CefRefPtr<obsharp::KestrelResourceHandler> handler_;
    int32_t bytes_available_ = 0;
    int64_t bytes_skipped_ = 0;
    bool is_skip_ = false;

    IMPLEMENT_REFCOUNTING(ResumeTask);
    DISALLOW_COPY_AND_ASSIGN(ResumeTask);
  };

} // namespace

extern "C" {

  int32_t OBSHARP_NATIVE_CALL obsharp_scheme_register(const char* scheme, const char* domain,
    const obsharp_resource_handler_callbacks* callbacks) {
    if (!scheme || !*scheme || !domain || !*domain || !callbacks)
      return OBSHARP_ERR_INVALID_ARGUMENT;

    // 只留 on_open 是必需的：没有它这次注册没有任何意义（每个请求都会被拒）。
    if (!callbacks->on_open)
      return OBSHARP_ERR_INVALID_ARGUMENT;

    obsharp::SchemeCallbacks entry;
    entry.callbacks = *callbacks;

    const std::string scheme_value = scheme;
    const std::string domain_value = domain;

    // 记下回调表。放在注册之前：factory 一旦被 CEF 调起来（有请求进来），
    // 它需要能从表里读到 callbacks。
    {
      std::lock_guard<std::mutex> lock(obsharp::RegisteredCallbacksMutex());
      obsharp::RegisteredCallbacks()[obsharp::MakeSchemeKey(scheme_value, domain_value)] = entry;
    }

    bool registered = false;
    const bool ran = obsharp::RunOnCefUiThreadAndWait([&]() {
      registered = CefRegisterSchemeHandlerFactory(scheme_value, domain_value,
        new obsharp::KestrelSchemeHandlerFactory(entry));
      });

    if (!ran) {
      obsharp::LogF(OBSHARP_LOG_ERROR,
        "could not reach the CEF UI thread to register the scheme handler for %s://%s",
        scheme_value.c_str(), domain_value.c_str());
      return OBSHARP_ERR_INTERNAL;
    }

    if (!registered) {
      // CEF 里每个 (scheme, domain) 只能有一个 factory，重复注册会返回 false。
      // 去重是 C# 的事（它保证同一个键只调一次）；这里只如实报失败。
      obsharp::LogF(OBSHARP_LOG_ERROR, "CefRegisterSchemeHandlerFactory(%s, %s) failed",
        scheme_value.c_str(), domain_value.c_str());
      return OBSHARP_ERR_INTERNAL;
    }

    obsharp::LogF(OBSHARP_LOG_DEBUG, "scheme handler registered for %s://%s",
      scheme_value.c_str(), domain_value.c_str());
    return OBSHARP_OK;
  }

  void OBSHARP_NATIVE_CALL obsharp_response_set_status(void* response, int32_t status) {
    if (auto* target = static_cast<CefResponse*>(response))
      target->SetStatus(status);
  }

  void OBSHARP_NATIVE_CALL obsharp_response_set_status_text(void* response, const char* text) {
    if (auto* target = static_cast<CefResponse*>(response))
      target->SetStatusText(text ? text : "");
  }

  void OBSHARP_NATIVE_CALL obsharp_response_set_mime_type(void* response, const char* mime_type) {
    // mime_type 必须单独设：CefResponse 里它和 Content-Type 头是两个独立字段，
    // CEF 的加载器读的是 GetMimeType()。只塞 header map 的话 HTML 不会被当成文档
    // 解析，页面停在 about:blank 或变成纯文本。
    if (auto* target = static_cast<CefResponse*>(response))
      target->SetMimeType(mime_type ? mime_type : "");
  }

  void OBSHARP_NATIVE_CALL obsharp_response_set_headers(void* response,
    const obsharp_request_header* headers, int32_t header_count) {
    auto* target = static_cast<CefResponse*>(response);
    if (!target)
      return;

    CefResponse::HeaderMap map;
    for (int32_t i = 0; headers && i < header_count; ++i) {
      const obsharp_request_header& header = headers[i];
      if (!header.name)
        continue;

      map.insert({CefString(header.name), CefString(header.value ? header.value : "")});
    }

    // 原样照抄：要滤掉 Content-Length / Connection / Transfer-Encoding 是 C# 的
    // 判断（它对 HTTP 语义负责），本库不替它筛。
    target->SetHeaderMap(map);
  }

  void OBSHARP_NATIVE_CALL obsharp_resource_handler_resume_read(void* handler,
    int32_t bytes_available) {
    auto* target = AsHandler(handler);
    if (!target)
      return;

    // 加引用再投递（理由见上面的说明），投递失败就算了：CEF 正在退出。
    if (!CefPostTask(TID_IO, new ResumeTask(target, bytes_available, 0, false))) {
      obsharp::LogF(OBSHARP_LOG_WARNING,
        "CefPostTask(TID_IO) failed while resuming a read; CEF is probably shutting down");
    }
  }

  void OBSHARP_NATIVE_CALL obsharp_resource_handler_resume_skip(void* handler,
    int64_t bytes_skipped) {
    auto* target = AsHandler(handler);
    if (!target)
      return;

    if (!CefPostTask(TID_IO, new ResumeTask(target, 0, bytes_skipped, true))) {
      obsharp::LogF(OBSHARP_LOG_WARNING,
        "CefPostTask(TID_IO) failed while resuming a skip; CEF is probably shutting down");
    }
  }

} // extern "C"
