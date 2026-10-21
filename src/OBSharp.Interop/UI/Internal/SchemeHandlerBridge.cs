using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using OBSharp.Interop.Native;

namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// CEF scheme handler 的托管后端：把 <c>&lt;scheme&gt;://&lt;domain&gt;/...</c> 上的请求
/// 转成 Kestrel 的 HTTP/1.1 请求，再把响应喂回 CEF。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>KestrelResourceHandler</c> + <c>KestrelSchemeHandlerFactory</c> +
/// <c>InstallSchemeHandler</c>（<c>SchemeHandler.cpp</c>）。旧代码是 C++ 实现
/// <c>CefResourceHandler</c> 虚接口；现在原生侧只剩一个「零判断适配器」，
/// 所有决定（URL 怎么拆、要不要连 Kestrel、每请求怎么缓冲、错误怎么表达）都在这里。
/// </para>
/// <para>
/// <b>线程</b>：六个回调除 <c>_resume_*</c> 之外全部在 CEF 的 <b>IO 线程</b>上被调用，
/// 不是 Qt 主线程——所以这里绝不直接碰 Qt，只有页面消息派发那一处用
/// <see cref="NativeApi.InvokeUI"/> 切到 Qt 主线程（旧代码走的是
/// <c>CefPostTask(TID_UI)</c> + <c>PostToQtThread</c> 两条异步投递，
/// 效果一样：业务回调最终落在 Qt 主线程）。
/// </para>
/// <para>
/// <b>不能抛异常</b>：这些方法是从原生栈直接 <c>call</c> 进来的，
/// <see cref="UnmanagedCallersOnlyAttribute"/> 方法里飞出异常会穿过原生栈（未定义行为，
/// 通常是整个进程直接没了）。所以每个回调都整体包在 try/catch 里，出错就用返回值表达。
/// </para>
/// </remarks>
internal static unsafe class SchemeHandlerBridge
{
    /// <summary>
    /// 页面 -&gt; 原生 的消息通道地址（POST 到这里就是 bridge 消息，不往 Kestrel 转）。
    /// </summary>
    private const string MessageTarget = "/__obsharp/message";

    /// <summary>
    /// 等响应头的超时。跟旧代码一样给 30 秒：本地管道正常是几毫秒，
    /// 这个值只在 Kestrel 卡死 / 连上了却不回话时才会用到。
    /// </summary>
    private const int HeadTimeoutMilliseconds = 30_000;

    /// <summary>
    /// 后台读循环每次从会话里读多少。
    /// </summary>
    private const int ReadChunkSize = 16 * 1024;

    /// <summary>
    /// 每请求状态，key 是 handler 指针（原生侧保证同一个请求的 handle 稳定、
    /// 且 <see cref="OnDestroy"/> 一定来一次）。
    /// </summary>
    private static readonly ConcurrentDictionary<nint, NativeRequestState> Requests = new();

    /// <summary>
    /// domain -&gt; 页面消息接收者。scheme handler 是按 (scheme, domain) 全局注册的，
    /// 拿不到「这条消息是哪个浏览器发的」，所以按 domain 反查——跟旧
    /// <c>RouteBridgeMessage</c> 的思路一致。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Action<string>?> MessageSinks =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 已经注册过的 (scheme, domain) -&gt; 端点。CEF 里这个键全局唯一，重复注册会失败，
    /// 所以先记账再注册（旧 <c>g_factories</c>）。
    /// </summary>
    private static readonly Dictionary<string, Endpoint> Factories = new(StringComparer.Ordinal);

    /// <summary>
    /// 保护 <see cref="Factories"/> 的「查重 + 注册 + 记账」三步原子性：
    /// 两个线程同时注册同一个 key 时，只允许一个真的去调原生。
    /// </summary>
    private static readonly object FactoryLock = new();

    /// <summary>
    /// 交给原生侧的回调表（<c>User</c> 为 0：本类没有任何实例状态，全是静态的）。
    /// </summary>
    private static readonly NativeTypes.ResourceHandlerCallbacks Callbacks = CreateCallbacks();

    /// <summary>
    /// 装配回调表。
    /// </summary>
    /// <remarks>
    /// 写成方法而不是内联字段初始化，是为了让 <c>&amp;Method</c> 取函数指针这件事
    /// 集中在一个地方：字段声明处直接写 <c>&amp;OnOpen</c> 也能编过，但可读性差，
    /// 而且以后加回调时容易漏。
    /// </remarks>
    private static NativeTypes.ResourceHandlerCallbacks CreateCallbacks() => new()
    {
        User = 0,
        OnOpen = &OnOpen,
        GetResponseHeaders = &GetResponseHeaders,
        Read = &Read,
        Skip = &Skip,
        Cancel = &OnCancel,
        Destroy = &OnDestroy,
    };

    /// <summary>
    /// 给 <paramref name="scheme"/>://<paramref name="domain"/> 注册 scheme handler。
    /// </summary>
    /// <param name="scheme">scheme 名（有 host 时是 <c>https</c>）。</param>
    /// <param name="domain">domain 名（<c>&lt;host&gt;.obsharp</c>）。</param>
    /// <param name="endpoint">这个 domain 的请求要转给哪个 Kestrel 端点。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>注册成功（或早就注册过）返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 同一个 (scheme, domain) <b>只注册一次</b>：CEF 里这个键全局唯一，重复注册会失败，
    /// 所以本方法自己记账——第二次调用直接当成功；端点不一致时记一条警告并沿用第一次
    /// 注册的那个端点（factory 一旦注册就改不了端点，报错只会让第二个浏览器建不出来，
    /// 而用旧端点至少还能连上本来就在跑的那个 Kestrel）。
    /// </para>
    /// <para>
    /// 必须在 Qt 主线程上调用（<c>obsharp_scheme_register</c> 的要求；原生侧内部会再切到
    /// CEF UI 线程并等它跑完）。
    /// </para>
    /// </remarks>
    public static bool EnsureInstalled(string scheme, string domain, Endpoint endpoint, out string? error)
    {
        error = null;

        if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(domain))
        {
            error = "scheme and domain must not be empty";
            return false;
        }

        string key = scheme + "://" + domain;

        lock (FactoryLock)
        {
            if (Factories.TryGetValue(key, out Endpoint? existing))
            {
                // 复用：factory 已经注册过，端点改不了了。端点不一致说明调用方对同一个
                // host 给了两个不同的 Kestrel，记一笔警告，用第一次的。
                if (!string.Equals(existing.Address, endpoint.Address, StringComparison.Ordinal) ||
                    existing.Kind != endpoint.Kind)
                {
                    NativeLogSink.Write(NativeLogLevel.Warning,
                        "scheme handler for " + key + " is already registered for endpoint \"" +
                        existing.Address + "\"; ignoring the new endpoint \"" + endpoint.Address + "\"");
                }

                return true;
            }

            int result = NativeMethods.obsharp_scheme_register(scheme, domain, in Callbacks);
            if (result != (int)NativeResult.Ok)
            {
                error = "obsharp_scheme_register(" + scheme + ", " + domain + ") failed: " +
                        (NativeResult)result;
                return false;
            }

            Factories[key] = endpoint;
        }

        NativeLogSink.Write(NativeLogLevel.Debug, "scheme handler ready for " + key);
        return true;
    }

    /// <summary>
    /// 设置某个 domain 的页面消息接收者（<see langword="null"/> 表示撤销）。
    /// </summary>
    /// <remarks>
    /// 消息接收者按 domain 注册而不是按浏览器实例：scheme handler 是全局的，
    /// 同一个 domain 上可能有多个浏览器（停靠面板 + 工具窗口），旧代码的取舍是
    /// 「发给最近注册的那一个」——这里也一样，后设置的覆盖先设置的。
    /// </remarks>
    public static void SetMessageSink(string domain, Action<string>? sink)
    {
        if (string.IsNullOrEmpty(domain))
            return;

        if (sink is null)
            MessageSinks.TryRemove(domain, out _);
        else
            MessageSinks[domain] = sink;
    }

    #region CEF 回调

    /// <summary>
    /// 请求到达。返回 0 表示成功（接着走 <see cref="GetResponseHeaders"/>），负数表示失败。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnOpen(nint user, nint handler, byte* method, byte* url, NativeTypes.RequestHeader* headers,
        int headerCount, void* body, int bodyLength)
    {
        try
        {
            if (handler == 0)
                return -1;

            // 1. 立刻把原生侧给的借用数据拷成托管值：返回之后这些指针就失效了。
            string requestMethod = Utf8ToString(method) ?? "GET";
            string requestUrl = Utf8ToString(url) ?? string.Empty;
            var request = new HttpRequest
            {
                Method = requestMethod,
                Body = CopyBytes(body, bodyLength),
            };

            for (int i = 0; i < headerCount; i++)
            {
                string? name = Utf8ToString(headers[i].Name);
                string? value = Utf8ToString(headers[i].Value);
                if (name is null)
                    continue;

                request.Headers.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            }

            // 2. 拆 URL：url 形如 https://<host>.obsharp/<path>?<query>，
            //    target 要用 path + query（Host: 头用完整 host，即 <host>.obsharp）。
            SplitRequestUrl(requestUrl, out string host, out string target);
            request.Host = host;
            request.Target = target;

            var state = new NativeRequestState();
            Requests[handler] = state;

            // 3. 页面 -> 原生 的消息通道：拦下 POST /__obsharp/message，不往 Kestrel 转。
            if (IsMessageRequest(request))
            {
                DispatchMessage(host, Encoding.UTF8.GetString(request.Body));

                // 204 + 无 body：页面那边只关心「消息发出去了」，不需要响应内容。
                state.Complete(new HttpResponseHead
                {
                    Status = 204,
                    StatusText = "No Content",
                    Framing = HttpFraming.None,
                    ContentLength = 0,
                }, Array.Empty<byte>());

                LogRequest(requestMethod, requestUrl, state);
                return 0;
            }

            // 4. 起后台工作：连 Kestrel、拿响应头、然后把 body 流式搬过来。
            //
            // 连接目标来自 factory 注册时的那一份，不从 URL 推——同一个 Kestrel 可以被
            // 好几个 domain 共用，而「哪个 domain 用哪个端点」只有注册时才知道。
            // 用 URL 里的 host 拼成 domain 去查（url 形如 https://<host>.obsharp/...，
            // 所以 host 就是 <host>.obsharp）。
            Endpoint? endpoint = TryGetEndpointForDomain(host, out Endpoint registered) ? registered : null;

            _ = Task.Run(() => RunWorker(handler, state, endpoint, request));

            // 5. 阻塞等响应头。旧代码也是这样：Open() 同步完成，handle_request 置真返回 true，
            //    不借助 callback——CEF 的 GetResponseHeaders 紧接着就会被调用，
            //    那时候必须已经能读到状态码和头，否则只能瞎猜。
            if (!state.WaitForHead(HeadTimeoutMilliseconds))
            {
                // 超时：给 504。同时标记取消并关掉会话，否则后台任务拿到头之后还会继续
                // 往一个已经当错误处理掉的缓冲里追加数据（旧代码这里会把两段内容混在一起）。
                state.ServeError(504, "Kestrel endpoint did not respond within 30s");
            }

            LogRequest(requestMethod, requestUrl, state);
            return 0;
        }
        catch (Exception exception)
        {
            // 异常绝不能穿过原生栈：这里吞掉并让这次请求失败。
            SafeLog(NativeLogLevel.Error, "scheme handler OnOpen failed: " + exception);
            Requests.TryRemove(handler, out _);
            return -1;
        }
    }

    /// <summary>
    /// 填响应头。返回 body 长度（<c>&gt;= 0</c> 已知，<c>-1</c> 未知）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long GetResponseHeaders(nint user, nint handler, nint response)
    {
        try
        {
            if (!Requests.TryGetValue(handler, out NativeRequestState? state))
            {
                // 状态不在了（被 Cancel/Destroy 抢先清掉）：给一个最小可用的 502，
                // 让 CEF 有一个确定的响应，而不是拿空响应去渲染。
                NativeMethods.obsharp_response_set_status(response, 502);
                NativeMethods.obsharp_response_set_status_text(response, "Bad Gateway");
                return 0;
            }

            HttpResponseHead head = state.SnapshotHead();

            // 状态码 0 说明连状态行都没读到（比如超时路径只写了错误信息）：
            // 按旧代码的做法补成 502，而不是把 0 交给 CEF。
            if (head.Status == 0)
                head.Status = 502;

            NativeMethods.obsharp_response_set_status(response, head.Status);
            if (!string.IsNullOrEmpty(head.StatusText))
                NativeMethods.obsharp_response_set_status_text(response, head.StatusText);

            SetResponseHeaders(response, head);

            // 关键：MIME 类型必须单独设。
            //
            // CefResponse 里 mime_type 和 Content-Type 头是两个独立字段：只把 Content-Type
            // 塞进 header map，CEF 的加载器读的是 GetMimeType()，拿不到就按未知类型处理——
            // 结果是 HTML 不会被解析成文档，页面停在 about:blank（或纯文本）。
            // 所以从 header 里取出来单独设一遍。
            string? mime = head.HeaderValue("Content-Type");
            if (!string.IsNullOrEmpty(mime))
            {
                // 去掉 "; charset=utf-8" 这类参数：SetMimeType 只接受类型本身。
                int semicolon = mime.IndexOf(';');
                NativeMethods.obsharp_response_set_mime_type(response, semicolon < 0 ? mime : mime[..semicolon]);
            }

            return head.Framing switch
            {
                HttpFraming.Length => head.ContentLength,
                HttpFraming.None => 0,
                // Chunked / UntilClose：长度未知，读到 Read 返回负数为止。
                _ => -1,
            };
        }
        catch (Exception exception)
        {
            SafeLog(NativeLogLevel.Error, "scheme handler GetResponseHeaders failed: " + exception);
            return -1;
        }
    }

    /// <summary>
    /// 读 body。返回实际写入的字节数；0 = 暂时没数据（原生侧挂起）；负数 = body 结束。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Read(nint user, nint handler, void* buffer, int size)
    {
        try
        {
            if (buffer is null)
                return -1;

            if (!Requests.TryGetValue(handler, out NativeRequestState? state))
                return -1;

            return state.Read((byte*)buffer, size);
        }
        catch (Exception exception)
        {
            SafeLog(NativeLogLevel.Error, "scheme handler Read failed: " + exception);
            return -1;
        }
    }

    /// <summary>
    /// 跳过 body 里的字节（CEF 处理 Range 请求用）。返回负数 = 失败，0 = 暂时跳不了
    /// （原生侧挂起，之后由后台任务调 <c>resume_skip</c>，参数是 <b>累计</b>跳过字节数）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Skip(nint user, nint handler, long bytesToSkip, long* skipped)
    {
        try
        {
            if (skipped is not null)
                *skipped = 0;

            if (!Requests.TryGetValue(handler, out NativeRequestState? state))
                return -1;

            long taken = state.TrySkip(bytesToSkip, out bool pending);
            if (skipped is not null)
                *skipped = taken;

            // 跳够了（或者 body 已结束、没得跳了），直接返回成功。
            if (!pending)
                return 1;

            // 不够：状态里已经记好了「还差多少」，等后台任务收到更多数据后由它续上
            // （续的对象是原生侧挂起的这次 Skip，携带累计值）。
            return 0;
        }
        catch (Exception exception)
        {
            SafeLog(NativeLogLevel.Error, "scheme handler Skip failed: " + exception);
            return -1;
        }
    }

    /// <summary>
    /// 请求被取消：标记取消并关掉会话。之后不会再有这个 handler 的任何回调
    /// （<see cref="OnDestroy"/> 除外）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnCancel(nint user, nint handler)
    {
        try
        {
            if (Requests.TryGetValue(handler, out NativeRequestState? state))
            {
                // 标记 + 关连接：阻塞在 ReadBody 里的后台任务会立刻带着异常返回，
                // 不会继续往缓冲里塞没人要的数据。
                state.Cancel();
            }
        }
        catch (Exception exception)
        {
            SafeLog(NativeLogLevel.Warning, "scheme handler Cancel failed: " + exception);
        }
    }

    /// <summary>
    /// handler 即将销毁：把每请求状态摘下来并释放（会话的连接在这里关）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDestroy(nint user, nint handler)
    {
        try
        {
            if (Requests.TryRemove(handler, out NativeRequestState? state))
                state.Dispose();
        }
        catch (Exception exception)
        {
            SafeLog(NativeLogLevel.Warning, "scheme handler Destroy failed: " + exception);
        }
    }

    #endregion

    #region 工作线程

    /// <summary>
    /// 后台工作：连 Kestrel -> 拿响应头 -> 循环读 body。全部在 Task 里跑，
    /// 阻塞无所谓（CEF 的 IO 线程绝对不会被它挡住）。
    /// </summary>
    private static void RunWorker(nint handler, NativeRequestState state, Endpoint? endpoint, HttpRequest request)
    {
        if (endpoint is null)
        {
            state.ServeError(502, "Kestrel endpoint unavailable: no endpoint is registered for this domain");
            WakeRead(handler, state);
            return;
        }

        KestrelSession? session;
        HttpResponseHead? head;
        try
        {
            session = KestrelSession.Start(endpoint, request, out head, out string? startError);
            if (session is null || head is null)
            {
                state.ServeError(502, "Kestrel endpoint unavailable: " + startError);
                WakeRead(handler, state);
                return;
            }
        }
        catch (Exception exception)
        {
            state.ServeError(502, "Kestrel endpoint unavailable: " + exception.Message);
            WakeRead(handler, state);
            return;
        }

        // 头已经拿到：交给 OnOpen（它可能还在等）和 GetResponseHeaders。
        state.SetHead(head, session);
        WakeRead(handler, state);

        byte[] buffer = new byte[ReadChunkSize];
        for (; ; )
        {
            if (state.IsCancelled)
                return;

            int read;
            try
            {
                read = session.ReadBody(buffer, out string? readError);
                if (read < 0)
                {
                    state.Fail(readError);
                    break;
                }
            }
            catch (Exception exception)
            {
                // 会话在别的线程被关掉（Cancel）时会走到这里：记下原因就退出，
                // 不再把它当正常数据。
                state.Fail(exception.Message);
                break;
            }

            if (read == 0)
            {
                state.Finish();
                break;
            }

            state.Append(buffer.AsSpan(0, read));

            // 读完一块就唤醒挂起的读：原生侧会把这次 resume 封送到 CEF 的 IO 线程上，
            // 在那里调回我们的 Read。没有挂起的读时它是空操作，所以可以放心每次都调。
            WakeRead(handler, state);

            // Skip 也要在这里推进：旧代码的 ResumeTask 同时处理 pendingRead 和 pendingSkip。
            WakeSkip(handler, state);
        }

        // 结束（正常或出错）后再各唤醒一次：挂起的 Read 会发现「没数据且 finished」
        // 从而返回 -1；挂起的 Skip 会带着累计值完成。
        WakeRead(handler, state);
        WakeSkip(handler, state);
    }

    /// <summary>
    /// 唤醒挂起的读，把当前可读字节数带过去。
    /// </summary>
    /// <remarks>
    /// 报告的是「缓冲里现在有多少」，因为 CEF 的 <c>ReadCallback::Continue(bytesAvailable)</c>
    /// 语义就是可用字节数（旧代码 ResumeTask 里也是这么算的）。
    /// </remarks>
    private static void WakeRead(nint handler, NativeRequestState state)
    {
        int available = state.AvailableForResume();
        NativeMethods.obsharp_resource_handler_resume_read(handler, available);
    }

    /// <summary>
    /// 推进一次挂起的 Skip；够了就把它续上。
    /// </summary>
    /// <remarks>
    /// <b>很关键</b>：<c>obsharp_resource_handler_resume_skip</c> 的第二个参数是
    /// <b>累计</b>已跳过字节数，不是这次新跳了多少——CEF 的 <c>SkipCallback::Continue</c>
    /// 本来就是累计语义，传增量会让 Range 请求整体错位。
    /// </remarks>
    private static void WakeSkip(nint handler, NativeRequestState state)
    {
        if (!state.ServicePendingSkip(out long cumulativeSkipped))
            return;

        NativeMethods.obsharp_resource_handler_resume_skip(handler, cumulativeSkipped);
    }

    #endregion

    #region 辅助

    /// <summary>
    /// 把页面消息交给对应的浏览器（按 domain 找 sink）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>同步调用 sink，不做线程封送</b>：sink（<c>WebView.OnBridgeMessage</c>）自己负责
    /// 「入队 + 泵到 Qt 主线程」——它这么做是刻意的，因为新 ABI 只有阻塞版的
    /// <c>obsharp_invoke_ui</c>，在 CEF 的 IO 线程上直接调用会死锁。这里再封送一次只会
    /// 让控制消息被排两次队、打乱 console 与 load-end 的先后顺序。
    /// </para>
    /// <para>
    /// 交给 sink 的是<b>原始 JSON</b>：控制消息（<c>$obsharp</c>）和普通消息的分流由 sink
    /// 一侧用 <see cref="BridgeDispatcher.TryDispatch"/> 做。
    /// </para>
    /// </remarks>
    private static void DispatchMessage(string domain, string json)
    {
        if (!MessageSinks.TryGetValue(domain, out Action<string>? sink) || sink is null)
            return;

        try
        {
            sink(json);
        }
        catch (Exception exception)
        {
            // sink 里抛异常绝不能穿过原生栈（OnOpen 是从 CEF 直接 call 进来的）。
            SafeLog(NativeLogLevel.Warning, "failed to dispatch a bridge message: " + exception.Message);
        }
    }

    /// <summary>
    /// 按 domain 查这个 domain 注册时用的端点。
    /// </summary>
    private static bool TryGetEndpointForDomain(string domain, out Endpoint endpoint)
    {
        // 注册时的 key 是 "scheme://domain"，而这里只有 domain：遍历一遍即可
        // （注册过的 factory 数量是「浏览器个数」量级，可以忽略不计）。
        lock (FactoryLock)
        {
            foreach (KeyValuePair<string, Endpoint> entry in Factories)
            {
                int separator = entry.Key.IndexOf("://", StringComparison.Ordinal);
                if (separator < 0)
                    continue;

                if (string.Equals(entry.Key[(separator + 3)..], domain, StringComparison.Ordinal))
                {
                    endpoint = entry.Value;
                    return true;
                }
            }
        }

        endpoint = new Endpoint();
        return false;
    }

    /// <summary>
    /// 是不是「页面 -&gt; 原生」的消息请求：POST 且路径（去掉 query）正好是
    /// <see cref="MessageTarget"/>。
    /// </summary>
    private static bool IsMessageRequest(HttpRequest request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            return false;

        string target = request.Target;
        int query = target.IndexOf('?');
        if (query >= 0)
            target = target[..query];

        return string.Equals(target, MessageTarget, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把 CEF 里的 url 拆成「host」和「path + query」。
    /// </summary>
    /// <remarks>
    /// url 形如 <c>https://&lt;host&gt;.obsharp/&lt;path&gt;?&lt;query&gt;</c>。
    /// 不用 <see cref="Uri"/>：它会把百分号转义、点段（<c>.</c>/<c>..</c>）之类的内容
    /// 重新规范化一遍，Kestrel 那边收到的路径就和页面请求的不完全一样了。
    /// 手工拆能保证 <c>path?query</c> 逐字不变。
    /// </remarks>
    private static void SplitRequestUrl(string url, out string host, out string target)
    {
        host = "obsharp";
        target = "/";

        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            // 不像绝对 URL（理论上不会发生）：原样当 target，host 用默认值。
            target = url.Length == 0 ? "/" : url;
            return;
        }

        int hostStart = schemeEnd + 3;
        int rest = IndexOfAny(url, hostStart, '/', '?', '#');

        host = rest < 0 ? url[hostStart..] : url[hostStart..rest];
        if (host.Length == 0)
            host = "obsharp";

        if (rest < 0)
        {
            target = "/";
            return;
        }

        string tail = url[rest..];

        // 片段（#...）不发给服务端。
        int hash = tail.IndexOf('#');
        if (hash >= 0)
            tail = tail[..hash];

        if (tail.Length == 0)
            target = "/";
        else if (tail[0] == '/')
            target = tail;
        else
            target = "/" + tail; // "https://host?x" 这种：path 为空，query 还在
    }

    /// <summary>
    /// 从 <paramref name="start"/> 开始找第一个属于 <paramref name="candidates"/> 的字符。
    /// </summary>
    private static int IndexOfAny(string text, int start, params char[] candidates)
    {
        for (int i = start; i < text.Length; i++)
        {
            foreach (char candidate in candidates)
            {
                if (text[i] == candidate)
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 写响应头（滤掉由 CEF 自己管的 framing 头）。
    /// </summary>
    /// <remarks>
    /// content-length / connection / transfer-encoding 必须滤掉：body 的分帧已经由
    /// <see cref="Read"/> 的返回值 + <c>GetResponseHeaders</c> 的长度表达，再让 CEF 看到
    /// 原始头，它会按两套互相矛盾的规则去读 body（chunked 的头 + 「长度未知」的返回值）。
    /// </remarks>
    private static void SetResponseHeaders(nint response, HttpResponseHead head)
    {
        var kept = new List<KeyValuePair<string, string>>(head.Headers.Count);
        foreach (KeyValuePair<string, string> header in head.Headers)
        {
            if (string.Equals(header.Key, "content-length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "transfer-encoding", StringComparison.OrdinalIgnoreCase))
                continue;

            kept.Add(header);
        }

        if (kept.Count == 0)
        {
            // 空数组也要调用一次：原生侧是「整体替换」语义，不调就留着上一次的值。
            NativeMethods.obsharp_response_set_headers(response, null, 0);
            return;
        }

        NativeTypes.RequestHeader* headers = stackalloc NativeTypes.RequestHeader[kept.Count];
        nint* blocks = stackalloc nint[kept.Count * 2];
        int allocated = 0;

        try
        {
            for (int i = 0; i < kept.Count; i++)
            {
                headers[i].Name = AllocateUtf8(kept[i].Key, out nint nameBlock);
                blocks[allocated++] = nameBlock;

                headers[i].Value = AllocateUtf8(kept[i].Value, out nint valueBlock);
                blocks[allocated++] = valueBlock;
            }

            NativeMethods.obsharp_response_set_headers(response, headers, kept.Count);
        }
        finally
        {
            // 原生侧在调用期间就把字符串复制走了，所以调用一返回就能释放。
            for (int i = 0; i < allocated; i++)
                Marshal.FreeHGlobal(blocks[i]);
        }
    }

    /// <summary>
    /// 把字符串编成 NUL 结尾的 UTF-8，分配在非托管堆上（调用方负责
    /// <see cref="Marshal.FreeHGlobal"/>）。
    /// </summary>
    private static byte* AllocateUtf8(string text, out nint block)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        block = Marshal.AllocHGlobal(byteCount + 1);

        byte* destination = (byte*)block;
        fixed (char* chars = text)
        {
            if (byteCount > 0)
                Encoding.UTF8.GetBytes(chars, text.Length, destination, byteCount);
        }

        destination[byteCount] = 0;
        return destination;
    }

    /// <summary>
    /// 读一个 NUL 结尾的 UTF-8 借用指针；空指针返回 <see langword="null"/>。
    /// </summary>
    private static string? Utf8ToString(byte* text)
        => text is null ? null : Marshal.PtrToStringUTF8((nint)text);

    /// <summary>
    /// 拷一份原生侧的字节块；空指针 / 非正长度当空数组。
    /// </summary>
    private static byte[] CopyBytes(void* data, int length)
    {
        if (data is null || length <= 0)
            return Array.Empty<byte>();

        byte[] copy = new byte[length];
        fixed (byte* destination = copy)
            Buffer.MemoryCopy(data, destination, length, length);

        return copy;
    }

    /// <summary>
    /// 记一条请求日志（旧代码那条排查「空白页」用的 Info 行）。
    /// </summary>
    /// <remarks>
    /// 每条请求一行：排查「页面停在 about:blank / 空白页」时，先看这里有没有子资源请求
    /// （<c>_framework/blazor.web.js</c>、<c>*.css</c>、<c>/_blazor/negotiate</c>）以及状态码。
    /// </remarks>
    private static void LogRequest(string method, string url, NativeRequestState state)
    {
        HttpResponseHead head = state.SnapshotHead();
        string? error = state.Error;

        string text = method + " " + url + " -> " + head.Status.ToString();
        if (!string.IsNullOrEmpty(error))
            text += " (" + error + ")";

        SafeLog(error is null ? NativeLogLevel.Info : NativeLogLevel.Warning, text);
    }

    /// <summary>
    /// 写日志，但不让「日志本身」把请求弄挂（原生库没加载、日志系统未初始化都可能抛）。
    /// </summary>
    private static void SafeLog(NativeLogLevel level, string message)
    {
        try
        {
            NativeLogSink.Write(level, message);
        }
        catch (Exception exception)
        {
            // 日志失败没有补救手段，也绝不能影响请求处理；至少要留一条痕迹。
            System.Diagnostics.Debug.WriteLine(
                "SchemeHandlerBridge.SafeLog failed: " + exception + " | original: " + message);
        }
    }

    #endregion

    /// <summary>
    /// 一次请求的共享状态：后台任务往里写，CEF 的 IO 线程往外读。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应旧 C++ 的 <c>Stream</c>。缓冲与状态都由 <see cref="Gate"/> 保护；
    /// <c>pendingSkip*</c> 只在 IO 线程上读写（后台任务只负责「发现够了就续上」）。
    /// </para>
    /// <para>
    /// 旧代码用 <c>std::mutex</c> + <c>condition_variable</c>；这里换成 <see cref="Monitor"/>
    /// 加一个 <see cref="ManualResetEventSlim"/>：条件变量那种「唤醒后重新检查谓词」的写法
    /// 在 C# 里没有语言支持，用事件更不容易写错。
    /// </para>
    /// </remarks>
    private sealed class NativeRequestState : IDisposable
    {
        /// <summary>保护下面所有字段的锁。</summary>
        private readonly object Gate = new();

        /// <summary>「响应头已经可用」的信号（OnOpen 等它）。</summary>
        private readonly ManualResetEventSlim HeadReady = new(false);

        private HttpResponseHead? _head;
        private KestrelSession? _session;

        /// <summary>已经缓冲、还没交给 CEF 的 body 字节。</summary>
        private byte[] _buffer = Array.Empty<byte>();
        private int _length;
        private int _offset;

        private bool _finished;
        private bool _cancelled;
        private bool _disposed;
        private string? _error;

        // Skip 的挂起状态：只有 CEF IO 线程写，后台任务在 ServicePendingSkip 里读/推进。
        private bool _skipPending;
        private long _skipRemaining;
        private long _skipTotal;

        /// <summary>响应头（给 <see cref="GetResponseHeaders"/> 用；没有时返回一个 502 占位）。</summary>
        public HttpResponseHead SnapshotHead()
        {
            lock (Gate)
            {
                return _head ?? new HttpResponseHead { Status = 502, StatusText = "Bad Gateway" };
            }
        }

        /// <summary>当前错误原因（没有则为 <see langword="null"/>）。</summary>
        public string? Error
        {
            get
            {
                lock (Gate)
                    return _error;
            }
        }

        /// <summary>body 是否已经结束（正常结束或出错都算）。</summary>
        public bool IsFinished
        {
            get
            {
                lock (Gate)
                    return _finished;
            }
        }

        /// <summary>是否已经被取消。</summary>
        public bool IsCancelled
        {
            get
            {
                lock (Gate)
                    return _cancelled;
            }
        }

        /// <summary>等响应头；超时返回 <see langword="false"/>。</summary>
        public bool WaitForHead(int millisecondsTimeout)
        {
            try
            {
                return HeadReady.Wait(millisecondsTimeout);
            }
            catch (ObjectDisposedException)
            {
                // Destroy 抢先销毁了事件：当作「头已经没意义了」。
                return true;
            }
        }

        /// <summary>后台任务拿到响应头：交给 OnOpen / GetResponseHeaders，并启动 body 流。</summary>
        public void SetHead(HttpResponseHead head, KestrelSession session)
        {
            lock (Gate)
            {
                // 已经被取消 / 已经被 ServeError 抢答：会话没人要了，直接关掉，
                // 免得泄漏连接（旧代码靠析构函数兜底）。
                if (_cancelled || _finished)
                {
                    session.Close();
                    return;
                }

                _head = head;
                _session = session;
            }

            HeadReady.Set();
        }

        /// <summary>
        /// 直接给一个错误响应（502 / 504）：不需要连 Kestrel，body 就是错误文本。
        /// </summary>
        /// <remarks>
        /// 注意这里<b>不</b>置 <see cref="_cancelled"/>：错误响应本身还是要交给页面的
        /// （502 的文本就是排查线索），<see cref="Read"/> 必须能把它读出去。
        /// 只靠 <see cref="_finished"/> 把「后台还在读的那条连接」掐掉：<see cref="Append"/>
        /// 见到 finished 就不再往缓冲里追加，工作循环下一轮也会立刻退出。
        /// </remarks>
        public void ServeError(int status, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);

            lock (Gate)
            {
                if (_finished)
                    return;

                _error = message;
                _head = new HttpResponseHead
                {
                    Status = status,
                    StatusText = status == 504 ? "Gateway Timeout" : "Bad Gateway",
                    Framing = HttpFraming.Length,
                    ContentLength = body.Length,
                };

                _buffer = body;
                _length = body.Length;
                _offset = 0;
                _finished = true;

                // 正在读 Kestrel 的那条连接没人要了：关掉它，让后台任务马上退出，
                // 而不是继续往这个「错误响应」的缓冲里追加真正的响应数据。
                _session?.Close();
                _session = null;
            }

            HeadReady.Set();
        }

        /// <summary>
        /// 直接给一个完整响应（204 No Content 那条路径）。
        /// </summary>
        public void Complete(HttpResponseHead head, byte[] body)
        {
            lock (Gate)
            {
                _head = head;
                _buffer = body;
                _length = body.Length;
                _offset = 0;
                _finished = true;
                _error = null;
            }

            HeadReady.Set();
        }

        /// <summary>追加后台读到的一段 body。</summary>
        public void Append(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return;

            lock (Gate)
            {
                if (_cancelled || _finished)
                    return;

                if (_length + data.Length > _buffer.Length)
                {
                    int grow = Math.Max(_buffer.Length * 2, _length + data.Length);
                    Array.Resize(ref _buffer, Math.Max(grow, ReadChunkSize));
                }

                data.CopyTo(_buffer.AsSpan(_length));
                _length += data.Length;
            }
        }

        /// <summary>body 正常结束。</summary>
        public void Finish()
        {
            lock (Gate)
                _finished = true;
        }

        /// <summary>body 出错结束。</summary>
        public void Fail(string? error)
        {
            lock (Gate)
            {
                _error = error;
                _finished = true;
            }
        }

        /// <summary>
        /// CEF 侧读：有数据就拷走；结束了返回 -1；暂时没数据返回 0（原生侧会挂起这次读）。
        /// </summary>
        /// <remarks>
        /// 这里返回 0 而<b>不是</b> -1 是旧代码用血换来的教训：按 <c>cef_resource_handler.h</c>
        /// 的契约，「还没数据」必须是「置 bytes_read = 0、稍后执行 callback」，
        /// 返回 false 会被 CEF 当成「响应已经结束」，页面就被截断在缓冲区末尾
        /// （实测表现：DOM 停在 HTML 实体中间，比如 "...OB# 插&amp;#..."）。
        /// 原生侧对此的约定是：Read 返回 0 就挂起，之后由我们
        /// <c>obsharp_resource_handler_resume_read</c> 续上。
        /// </remarks>
        public int Read(byte* buffer, int size)
        {
            lock (Gate)
            {
                // 只有「被 CEF 取消」才立刻结束。ServeError 设的 _aborted 不算：
                // 502/504 的正文还是要读给页面看的。
                if (_cancelled)
                    return -1;

                int available = _length - _offset;
                if (available > 0)
                {
                    int count = size <= 0 ? available : Math.Min(available, size);
                    Marshal.Copy(_buffer, _offset, (nint)buffer, count);
                    _offset += count;
                    CompactLocked();
                    return count;
                }

                // body 已经收完、缓冲也空了：响应结束。只有这里才返回负数。
                if (_finished)
                    return -1;

                // 还没数据（后台任务在读 Kestrel）：返回 0 让原生侧挂起这次读。
                return 0;
            }
        }

        /// <summary>
        /// CEF 侧跳过若干字节。
        /// </summary>
        /// <param name="bytesToSkip">这次要跳过的总字节数。</param>
        /// <param name="pending">
        /// <see langword="true"/> 表示没跳够、且 body 还没结束：调用方应返回 0 让原生侧挂起，
        /// 之后由后台任务用 <see cref="ServicePendingSkip"/> 推进。
        /// </param>
        /// <returns>本次实际跳掉的字节数。</returns>
        /// <remarks>
        /// 「跳不够就挂起」和「记下还差多少」必须放在同一个锁里：拆成两步的话，
        /// 后台任务可能正好在两步之间追加了数据并唤醒一次（那次唤醒会落空，
        /// 于是这次 Skip 永远等不到下一次唤醒——CEF 那边就是卡住不动的页面）。
        /// </remarks>
        public long TrySkip(long bytesToSkip, out bool pending)
        {
            pending = false;

            if (bytesToSkip <= 0)
                return 0;

            lock (Gate)
            {
                if (_cancelled)
                    return 0;

                int available = _length - _offset;
                int take = (int)Math.Min(available, bytesToSkip);
                _offset += take;
                CompactLocked();

                long remaining = bytesToSkip - take;

                // 跳够了，或者 body 已经结束（没得跳了也算完成，CEF 会按范围不可满足处理）。
                if (remaining <= 0 || _finished)
                    return take;

                _skipPending = true;
                _skipTotal = bytesToSkip;
                _skipRemaining = remaining;
                pending = true;
                return take;
            }
        }

        /// <summary>是否还有挂起的 Skip 在等数据。</summary>
        public bool HasPendingSkip
        {
            get
            {
                lock (Gate)
                    return _skipPending;
            }
        }

        /// <summary>
        /// 推进挂起的 Skip。返回 <see langword="true"/> 表示这次 Skip 可以完成了，
        /// <paramref name="cumulativeSkipped"/> 是要传给 <c>resume_skip</c> 的<b>累计</b>值。
        /// </summary>
        public bool ServicePendingSkip(out long cumulativeSkipped)
        {
            cumulativeSkipped = 0;

            lock (Gate)
            {
                if (!_skipPending)
                    return false;

                int available = _length - _offset;
                if (available > 0 && _skipRemaining > 0)
                {
                    int take = (int)Math.Min(available, _skipRemaining);
                    _offset += take;
                    _skipRemaining -= take;
                    CompactLocked();
                }

                // 还没跳够，且 body 还没结束：等下一批数据。
                if (_skipRemaining > 0 && !_finished)
                    return false;

                // 跳够了，或者没得跳了（CEF 会按「范围不可满足」处理剩下的部分）。
                cumulativeSkipped = _skipTotal - _skipRemaining;
                _skipPending = false;
                _skipTotal = 0;
                _skipRemaining = 0;
                return true;
            }
        }

        /// <summary>挂起中的读能拿到多少字节（给 <c>resume_read</c> 用）。</summary>
        public int AvailableForResume()
        {
            lock (Gate)
                return Math.Min(_length - _offset, int.MaxValue);
        }

        /// <summary>
        /// 取消：标记 + 关连接。关连接放在锁外面——它会打断正在阻塞的读，
        /// 期间不持有锁可以避免和后台任务的 <see cref="Fail"/> 抢锁。
        /// </summary>
        public void Cancel()
        {
            KestrelSession? session;
            lock (Gate)
            {
                _cancelled = true;
                session = _session;
                _session = null;
            }

            session?.Close();
            HeadReady.Set(); // OnOpen 可能还在等：叫醒它，让它看到 cancelled。
        }

        /// <inheritdoc />
        public void Dispose()
        {
            KestrelSession? session;

            lock (Gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _cancelled = true;
                session = _session;
                _session = null;

                _buffer = Array.Empty<byte>();
                _length = 0;
                _offset = 0;
            }

            session?.Close();

            // 事件也要释放：它是带 WaitHandle 的资源。等不到的人已经被 cancelled 分支
            // 叫醒了（Destroy 只在请求结束后才来，正常情况下没人还在等）。
            try
            {
                HeadReady.Set();
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }

            HeadReady.Dispose();
        }

        /// <summary>把已消费的前缀搬掉（旧 <c>CompactLocked</c>）。</summary>
        private void CompactLocked()
        {
            if (_offset <= 0)
                return;

            if (_offset >= _length)
            {
                _length = 0;
                _offset = 0;
                return;
            }

            // 跟旧代码一样给个 64KB 的门槛：短响应没必要每读一次就搬一遍内存。
            if (_offset > 64 * 1024)
            {
                int remaining = _length - _offset;
                Buffer.BlockCopy(_buffer, _offset, _buffer, 0, remaining);
                _length = remaining;
                _offset = 0;
            }
        }
    }
}
