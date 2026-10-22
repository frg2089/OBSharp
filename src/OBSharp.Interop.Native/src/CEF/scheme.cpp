#include "scheme.h"

#include "logger.h"

#include <include/cef_task.h>

#include <chrono>
#include <condition_variable>
#include <utility>
#include <vector>

// scheme.cpp：CEF scheme handler 桥的实现。
//
// 这一块是整个原生库里唯一「有点实现」的地方，但请注意它**仍然不做任何判断**：
//
//   * 这个 URL 该怎么响应？（托管侧）
//   * 响应头里哪些要滤掉、mime_type 设成什么？（托管侧）
//   * 分块的 CRLF 该怎么吃、body 什么时候结束？（托管侧）
//   * 同一个 (scheme, domain) 注册过没有？（托管侧）
//
// 它只负责两件调用方做不到的事：
//   1) 填 vtable —— CefResourceHandler 和 CefSchemeHandlerFactory 没有 C 入口；
//   2) 保存 CEF 异步契约要求的挂起回调 —— Read / Skip 走「稍后继续」时必须有人
//      记住那个 CefRefPtr，这个机械动作只能由持有它的这一层做。
//
// 所有回调都带着 handler 自己的指针（this）过去：托管侧拿它当每请求状态表的 key，
// 因为同一个键上可能同时有几十个请求在飞。

namespace OBSharp::Cef
{
  namespace
  {
    // 投到 CEF UI 线程上的同步任务。
    //
    // 为什么需要：CefRegisterSchemeHandlerFactory 要求必须在 TID_UI 上调用，而我们
    // 多半从 Qt 主线程进来（Windows / Linux 上这两个是不同线程）。
    class SyncTask : public CefTask
    {
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

    // CEF 的任务可能永远不跑（CEF 正在退出），所以等待必须有上限。
    constexpr int kCefTaskTimeoutMs = 5000;

    // 在 CEF 的 UI 线程上同步执行 fn 并等它跑完。返回 false 表示没跑成。
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
        Log(OBSHARP_LOG_WARNING, "CefPostTask(TID_UI) failed; CEF is probably shutting down");
        return false;
      }

      if (!task->WaitFor(std::chrono::milliseconds(kCefTaskTimeoutMs))) {
        Log(OBSHARP_LOG_WARNING, "timed out waiting for a task on the CEF UI thread");
        return false;
      }

      return true;
    }

    // 把 request 里的头搬成 C 结构数组。
    //
    // 注意借用语义：obsharp_request_header 里的 name/value 指向下面这个 vector 里
    // 的 std::string，**只在回调期间有效**。托管侧必须在 on_open 返回前拷走。
    std::vector<std::string> BuildHeaderStorage(const CefRequest::HeaderMap& headers,
      std::vector<obsharp_request_header>& out) {
      std::vector<std::string> storage;
      storage.reserve(headers.size() * 2);
      out.reserve(headers.size());

      for (const auto& header : headers) {
        storage.push_back(header.first.ToString());
        storage.push_back(header.second.ToString());
      }

      // storage 里是 [name0, value0, name1, value1, ...]，指针成对指过去。
      for (size_t i = 0; i < headers.size(); ++i)
        out.push_back(obsharp_request_header{storage[i * 2].c_str(), storage[i * 2 + 1].c_str()});

      return storage;
    }

    // 活着的 resource handler 集合。
    //
    // 为什么需要它：obsharp_resource_handler_resume_read/_skip 拿到的是裸指针，而
    // CEF 可以随时销毁 handler（页面关闭、导航、取消）。托管侧那个「每请求状态表」
    // 的检查只能收窄窗口，不能消除 —— 检查和调用之间 handler 完全可能被销毁。一旦
    // 把失效指针送进 ResumeTask 的 CefRefPtr 拷贝构造去加引用，就是对已释放内存的
    // 读写，进程直接没。
    //
    // 所以在原生侧也记一份：handler 构造时登记、析构时摘除。resume_* 先查这份表，
    // 查不到就丢弃这次唤醒。两个线程会同时访问（托管后台任务 + CEF IO 线程），所以
    // 配一把锁。
    //
    // 存指针值而不是 CefRefPtr：这里只回答「这个地址还活着吗」，不参与引用计数 ——
    // 持有强引用会让 handler 永远销毁不掉。
    std::unordered_set<ResourceHandler*>& LiveHandlers() {
      static std::unordered_set<ResourceHandler*> handlers;
      return handlers;
    }

    std::mutex& LiveHandlersMutex() {
      static std::mutex mutex;
      return mutex;
    }

    bool IsHandlerLive(ResourceHandler* handler) {
      if (!handler)
        return false;

      std::lock_guard<std::mutex> lock(LiveHandlersMutex());
      return LiveHandlers().find(handler) != LiveHandlers().end();
    }
  } // namespace

  // ---- ResourceHandler --------------------------------------------------------

  class ResourceHandler : public CefResourceHandler
  {
  public:
    explicit ResourceHandler(const obsharp_resource_handler_callbacks& callbacks) : callbacks_(callbacks) {
      // 登记：此后 resume_* 才认这个指针。
      std::lock_guard<std::mutex> lock(LiveHandlersMutex());
      LiveHandlers().insert(this);
    }

    // CefResourceHandler
    bool Open(CefRefPtr<CefRequest> request, bool& handle_request, CefRefPtr<CefCallback> callback) override;
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
    // （调用那边已经做过线程封送）。
    void ResumeRead(int bytes_available);
    void ResumeSkip(int64_t bytes_skipped);

  private:
    ~ResourceHandler() override {
      // 先摘登记，再通知托管侧。
      //
      // 顺序很重要：摘掉之后 resume_* 就已经不会再碰这个对象了，但托管侧的 destroy
      // 回调里还会用到 this 当下标去删状态 —— 那只是比较指针值，不解引用，安全。
      {
        std::lock_guard<std::mutex> lock(LiveHandlersMutex());
        LiveHandlers().erase(this);
      }

      // 通知托管侧释放挂在 handler 上的每请求状态。这个指针马上就不能用了，所以
      // 状态必须在这里（且只在这里）摘掉。
      if (!destroyed_ && callbacks_.destroy) {
        destroyed_ = true;
        callbacks_.destroy(callbacks_.user, this);
      }
    }

    obsharp_resource_handler_callbacks callbacks_{};

    // destroy 回调只需要来一次。
    bool destroyed_ = false;

    // 挂起状态。只由 IO 线程读写（CEF 的 Read / Skip 与我们的 Resume* 都在 TID_IO
    // 上），但任何路径都可能进来，所以还是加锁 —— 这里的争用可以忽略不计，
    // 而「漏一次解锁」的代价是死锁。
    std::mutex mutex_;
    CefRefPtr<CefResourceReadCallback> pending_read_;
    CefRefPtr<CefResourceSkipCallback> pending_skip_;

    IMPLEMENT_REFCOUNTING(ResourceHandler);
    DISALLOW_COPY_AND_ASSIGN(ResourceHandler);
  };

  bool ResourceHandler::Open(CefRefPtr<CefRequest> request, bool& handle_request,
    CefRefPtr<CefCallback> callback) {
    (void)callback; // 同步完成，不用它

    handle_request = true;

    if (!request)
      return false;

    if (!callbacks_.on_open)
      return false;

    // 把 CefRequest 拆成裸数据：方法、URL、头数组、POST body 的原始字节。
    // 这里**不解释** URL（不拆 host / path / query）—— 那是托管侧的事。
    const std::string method = request->GetMethod().ToString();
    const std::string url = request->GetURL().ToString();

    CefRequest::HeaderMap header_map;
    request->GetHeaderMap(header_map);

    std::vector<obsharp_request_header> headers;
    const std::vector<std::string> header_storage = BuildHeaderStorage(header_map, headers);

    // POST body：只拼 PDE_TYPE_BYTES 的元素（文件上传的 FILE 类型没有内存表示，
    // 跳过它 —— scheme handler 上跑的是 JSON 控制消息，从不发文件上传）。
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

    const int result = callbacks_.on_open(callbacks_.user, this, method.c_str(), url.c_str(), headers.data(),
      static_cast<int>(headers.size()), body.empty() ? nullptr : static_cast<const void*>(body.data()),
      static_cast<int>(body.size()));

    // 负数 = 让这次请求失败。handle_request = true + return false 是 CEF 里
    // 「立刻取消」的写法（handle_request 留 false 会退回已废弃的 ProcessRequest 路径）。
    return result >= 0;
  }

  void ResourceHandler::GetResponseHeaders(CefRefPtr<CefResponse> response, int64_t& response_length,
    CefString& redirect_url) {
    response_length = 0;

    if (callbacks_.get_response_headers && response) {
      // 托管侧负责写 status / status_text / mime_type / headers，返回 body 长度。
      // 本库只是把响应对象交出去，并把它返回的 int64 原样交给 CEF。
      response_length = callbacks_.get_response_headers(callbacks_.user, this, response.get());
    }

    // 不支持重定向：清空就等于「这不是重定向响应」。想跳转的话调用方自己设 3xx +
    // Location 头（CEF 会在页面里发一个新请求）。
    redirect_url.clear();
  }

  bool ResourceHandler::Read(void* data_out, int bytes_to_read, int& bytes_read,
    CefRefPtr<CefResourceReadCallback> callback) {
    bytes_read = 0;

    if (!callbacks_.read)
      return false;

    const int result = callbacks_.read(callbacks_.user, this, data_out, bytes_to_read);

    if (result > 0) {
      bytes_read = result;
      return true;
    }

    if (result == 0) {
      // **必须 return true。**
      //
      // 按 cef_resource_handler.h 的契约：「把 data_out 记下来、bytes_read 置 0、
      // return true，稍后执行 callback」。返回 false 会被 CEF 当成「响应已经结束」，
      // 页面就被截断在缓冲区末尾（实测到的现象：DOM 停在 HTML 实体中间，
      // 比如 "...OB# 插&amp;#..."）。
      //
      // 数据到达时由托管侧调 obsharp_resource_handler_resume_read 把这个读续上，
      // CEF 随后会再调一次 Read。
      {
        std::lock_guard<std::mutex> lock(mutex_);
        pending_read_ = callback;
      }
      return true;
    }

    // result < 0：body 结束。bytes_read 置 0 + return false 是 CEF 的「响应完成」。
    return false;
  }

  bool ResourceHandler::Skip(int64_t bytes_to_skip, int64_t& bytes_skipped,
    CefRefPtr<CefResourceSkipCallback> callback) {
    bytes_skipped = 0;

    if (!callbacks_.skip)
      return false;

    long long skipped = 0;
    const int result = callbacks_.skip(callbacks_.user, this, bytes_to_skip, &skipped);

    if (result > 0) {
      bytes_skipped = skipped;
      return true;
    }

    if (result == 0) {
      // 「稍后继续」：bytes_skipped 置 0、return true、记下 callback，等托管侧调
      // obsharp_resource_handler_resume_skip（传**累计**跳过字节数）。
      {
        std::lock_guard<std::mutex> lock(mutex_);
        pending_skip_ = callback;
      }
      return true;
    }

    // 失败：bytes_skipped < 0（-2 = ERR_FAILED）+ return false。
    bytes_skipped = -2;
    return false;
  }

  void ResourceHandler::Cancel() {
    // 先把挂起的回调丢掉（Cancel 之后它们不该再被续上），再通知托管侧。
    {
      std::lock_guard<std::mutex> lock(mutex_);
      pending_read_ = nullptr;
      pending_skip_ = nullptr;
    }

    if (callbacks_.cancel)
      callbacks_.cancel(callbacks_.user, this);
  }

  void ResourceHandler::ResumeRead(int bytes_available) {
    CefRefPtr<CefResourceReadCallback> callback;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      callback = pending_read_;
      pending_read_ = nullptr; // 取走即清空：多调一次也只生效一次
    }

    if (callback)
      callback->Continue(bytes_available);
  }

  void ResourceHandler::ResumeSkip(int64_t bytes_skipped) {
    CefRefPtr<CefResourceSkipCallback> callback;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      callback = pending_skip_;
      pending_skip_ = nullptr;
    }

    if (callback)
      callback->Continue(bytes_skipped);
  }

  // ---- SchemeHandlerFactory ---------------------------------------------------

  // 给每个请求建一个 ResourceHandler。
  //
  // CEF 的 factory 是「一个键一个 factory、全局生效」，之后这个键上的每个请求都由
  // 同一个 factory 建 handler，所以回调表挂在 factory 上、建 handler 时拷一份进去。
  class SchemeHandlerFactory : public CefSchemeHandlerFactory
  {
  public:
    explicit SchemeHandlerFactory(const obsharp_resource_handler_callbacks& callbacks) : callbacks_(callbacks) {}

    CefRefPtr<CefResourceHandler> Create(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
      const CefString& scheme_name, CefRefPtr<CefRequest> request) override {
      // CEF 每个请求都会调一次；我们只负责建 handler，怎么处理请求是托管侧的事。
      //
      // browser / frame / scheme_name / request 都不看：handler 需要的所有信息都在
      // 它自己的 Open() 里从 request 取。
      (void)browser;
      (void)frame;
      (void)scheme_name;
      (void)request;

      return new ResourceHandler(callbacks_);
    }

  private:
    obsharp_resource_handler_callbacks callbacks_{};

    IMPLEMENT_REFCOUNTING(SchemeHandlerFactory);
    DISALLOW_COPY_AND_ASSIGN(SchemeHandlerFactory);
  };

  // ---- 导出 ------------------------------------------------------------------

  bool obsharp_scheme_register(const char* scheme, const char* domain,
    const obsharp_resource_handler_callbacks* callbacks) {
    if (!scheme || !*scheme || !domain || !*domain || !callbacks)
      return false;

    // 只留 on_open 是必需的：没有它这次注册没有任何意义（每个请求都会被拒）。
    if (!callbacks->on_open)
      return false;

    const std::string scheme_value = scheme;
    const std::string domain_value = domain;

    // 当场复制结构体内容：调用方传完即可回收。
    obsharp_resource_handler_callbacks entry = *callbacks;

    bool registered = false;
    const bool ran = RunOnCefUiThreadAndWait([&]() {
      registered = CefRegisterSchemeHandlerFactory(scheme_value, domain_value,
        new SchemeHandlerFactory(entry));
    });

    if (!ran) {
      Log(OBSHARP_LOG_ERROR, "could not reach the CEF UI thread to register the scheme handler for %s://%s",
        scheme_value.c_str(), domain_value.c_str());
      return false;
    }

    if (!registered) {
      // CEF 里每个 (scheme, domain) 只能有一个 factory，重复注册会返回 false。
      // 去重是调用方的事；这里只如实报失败。
      Log(OBSHARP_LOG_ERROR, "CefRegisterSchemeHandlerFactory(%s, %s) failed", scheme_value.c_str(),
        domain_value.c_str());
      return false;
    }

    Log(OBSHARP_LOG_DEBUG, "scheme handler registered for %s://%s", scheme_value.c_str(), domain_value.c_str());
    return true;
  }

  void obsharp_response_set_status(CefResponse* response, int status) {
    if (response)
      response->SetStatus(status);
  }

  void obsharp_response_set_status_text(CefResponse* response, const char* text) {
    if (response)
      response->SetStatusText(text ? text : "");
  }

  void obsharp_response_set_mime_type(CefResponse* response, const char* mime_type) {
    // mime_type 必须单独设：CefResponse 里它和 Content-Type 头是两个独立字段，
    // CEF 的加载器读的是 GetMimeType()。只塞 header map 的话 HTML 不会被当成文档
    // 解析，页面停在 about:blank 或变成纯文本。
    if (response)
      response->SetMimeType(mime_type ? mime_type : "");
  }

  void obsharp_response_set_headers(CefResponse* response, const obsharp_request_header* headers,
    int header_count) {
    if (!response)
      return;

    CefResponse::HeaderMap map;
    for (int i = 0; headers && i < header_count; ++i) {
      const obsharp_request_header& header = headers[i];
      if (!header.name)
        continue;

      map.insert({CefString(header.name), CefString(header.value ? header.value : "")});
    }

    // 原样照抄：要滤掉 Content-Length / Connection / Transfer-Encoding 是调用方的
    // 判断（它对 HTTP 语义负责），本库不替它筛。
    response->SetHeaderMap(map);
  }

  namespace
  {
    // 把 resume_* 封送到 CEF 的 IO 线程上执行。
    //
    // 为什么要持引用：CefPostTask 到真正执行之间可能隔一段时间，期间 CEF 完全可能
    // 把这个 handler 的最后一个引用放掉（比如页面关闭、请求取消）。CefRefPtr 拷贝会
    // 增加引用计数，任务执行时对象一定还活着。
    //
    // CefPostTask 本身在 CEF 正在退出时会失败，此时什么都不做即可 —— 没有请求可续，
    // 也就没有「丢了」这回事。
    class ResumeTask : public CefTask
    {
    public:
      ResumeTask(CefRefPtr<ResourceHandler> handler, int bytes_available, int64_t bytes_skipped, bool is_skip)
        : handler_(std::move(handler)),
          bytes_available_(bytes_available),
          bytes_skipped_(bytes_skipped),
          is_skip_(is_skip) {
      }

      void Execute() override {
        if (!handler_)
          return;

        // 到这儿已经在 IO 线程上了，可以安全地碰挂起回调。
        if (is_skip_)
          handler_->ResumeSkip(bytes_skipped_);
        else
          handler_->ResumeRead(bytes_available_);
      }

    private:
      CefRefPtr<ResourceHandler> handler_;
      int bytes_available_ = 0;
      int64_t bytes_skipped_ = 0;
      bool is_skip_ = false;

      IMPLEMENT_REFCOUNTING(ResumeTask);
      DISALLOW_COPY_AND_ASSIGN(ResumeTask);
    };

    // 先确认这个 handler 还活着，再加引用投递到 IO 线程。
    //
    // 不查活的话，ResumeTask 的 CefRefPtr 拷贝构造会去读写那个地址的引用计数 ——
    // 如果 handler 已经被 CEF 销毁，就是对已释放内存的访问，进程直接终止。托管侧
    // 虽然也在查它自己的状态表，但那是另一份数据结构，检查和调用之间仍有窗口；
    // 这里才是最终把关的地方。
    void ResumeOnCefIoThread(ResourceHandler* handler, int bytes_available, int64_t bytes_skipped, bool is_skip) {
      if (!IsHandlerLive(handler))
        return;

      if (!CefPostTask(TID_IO, new ResumeTask(handler, bytes_available, bytes_skipped, is_skip)))
        Log(OBSHARP_LOG_WARNING, "CefPostTask(TID_IO) failed while resuming; CEF is probably shutting down");
    }
  } // namespace

  void obsharp_resource_handler_resume_read(ResourceHandler* handler, int bytes_available) {
    ResumeOnCefIoThread(handler, bytes_available, 0, false);
  }

  void obsharp_resource_handler_resume_skip(ResourceHandler* handler, long long bytes_skipped) {
    ResumeOnCefIoThread(handler, 0, bytes_skipped, true);
  }
}
