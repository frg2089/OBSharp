using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using OBSharp.Interop.Abi;

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
/// <see cref="AbiApi.InvokeUI"/> 切到 Qt 主线程（旧代码走的是
/// <c>CefPostTask(TID_UI)</c> + <c>PostToQtThread</c> 两条异步投递，
/// 效果一样：业务回调最终落在 Qt 主线程）。
/// </para>
/// <para>
/// <b>不能抛异常</b>：这些方法是从原生栈直接 <c>call</c> 进来的，
/// <see cref="UnmanagedCallersOnlyAttribute"/> 方法里飞出异常会穿过原生栈（未定义行为，
/// 通常是整个进程直接没了）。所以每个回调都整体包在 try/catch 里，出错就用返回值表达。
/// </para>
/// </remarks>
internal static partial class SchemeHandlerBridge
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
    private static readonly ConcurrentDictionary<nint, HttpResponse> Requests = new();

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
    private static readonly Dictionary<string, Endpoint> Factories = [with(StringComparer.Ordinal)];

    /// <summary>
    /// 保护 <see cref="Factories"/> 的「查重 + 注册 + 记账」三步原子性：
    /// 两个线程同时注册同一个 key 时，只允许一个真的去调原生。
    /// </summary>
    private static readonly Lock FactoryLock = new();

    /// <summary>
    /// 交给原生侧的回调表（<c>User</c> 为 0：本类没有任何实例状态，全是静态的）。
    /// </summary>
    private static readonly ObsharpResourceHandlerCallbacks Callbacks = CreateCallbacks();

    /// <summary>
    /// 装配回调表。
    /// </summary>
    /// <remarks>
    /// 写成方法而不是内联字段初始化，是为了让 <c>&amp;Method</c> 取函数指针这件事
    /// 集中在一个地方：字段声明处直接写 <c>&amp;OnOpen</c> 也能编过，但可读性差，
    /// 而且以后加回调时容易漏。
    /// </remarks>
    private static unsafe ObsharpResourceHandlerCallbacks CreateCallbacks() => new()
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
                    NativeLogSink.Write(AbiLogLevel.Warning,
                        "scheme handler for " + key + " is already registered for endpoint \"" +
                        existing.Address + "\"; ignoring the new endpoint \"" + endpoint.Address + "\"");
                }

                return true;
            }

            SafeLog(AbiLogLevel.Debug, "registering the scheme handler for " + key +
                " (endpoint " + endpoint.Address + ")");

            if (!Scheme.Register(scheme, domain, in Callbacks))
            {
                error = "obsharp_scheme_register(" + scheme + ", " + domain + ") failed";
                return false;
            }

            Factories[key] = endpoint;
        }

        NativeLogSink.Write(AbiLogLevel.Debug, "scheme handler ready for " + key);
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
    private static unsafe int OnOpen(nint user, nint handler, byte* method, byte* url, ObsharpRequestHeader* headers,
        int headerCount, nint body, int bodyLength)
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
                Body = CopyBytes((void*)body, bodyLength),
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

            // 3. 页面 -> 原生 的消息通道：拦下 POST /__obsharp/message，不往 Kestrel 转。
            if (IsMessageRequest(request))
            {
                DispatchMessage(host, Encoding.UTF8.GetString(request.Body));

                // 204 + 无 body：页面那边只关心「消息发出去了」，不需要响应内容。
                HttpResponse done = HttpResponse.Completed(204, "No Content", HttpFraming.None, []);
                Requests[handler] = done;
                LogRequest(requestMethod, requestUrl, done);
                return 0;
            }

            // 4. 发请求收响应。连接目标来自 factory 注册时的那一份，不从 URL 推——同一个
            //    Kestrel 可以被好几个 domain 共用，而「哪个 domain 用哪个端点」只有注册时
            //    才知道。用 URL 里的 host 拼成 domain 去查（url 形如
            //    https://<host>.obsharp/...，所以 host 就是 <host>.obsharp）。
            Endpoint? endpoint = TryGetEndpointForDomain(host, out Endpoint registered) ? registered : null;

            HttpResponse response = SendRequest(endpoint, request);

            // 5. 登记 + 挂 CEF 唤醒。Open() 同步完成（旧代码也是这样）：返回时响应头必须
            //    已经可用，CEF 的 GetResponseHeaders 紧接着就会被调用，那时候必须已经能读到
            //    状态码和头，否则只能瞎猜。body 由 SendAsync 起的后台泵继续灌进响应。
            Requests[handler] = response;
            response.Progress = () =>
            {
                // 每批 body 就绪 / 结束都唤醒挂起的读和 Skip（没有挂起时是空操作）。
                // 原生侧会把 resume 封送到 CEF 的 IO 线程上，在那里调回我们的 Read。
                WakeRead(handler, response);
                WakeSkip(handler, response);
            };
            WakeRead(handler, response);
            WakeSkip(handler, response);

            LogRequest(requestMethod, requestUrl, response);
            return 0;
        }
        catch (Exception exception)
        {
            // 异常绝不能穿过原生栈：这里吞掉并让这次请求失败。
            SafeLog(AbiLogLevel.Error, "scheme handler OnOpen failed: " + exception);
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
            if (!Requests.TryGetValue(handler, out HttpResponse? state))
            {
                // 状态不在了（被 Cancel/Destroy 抢先清掉）：给一个最小可用的 502，
                // 让 CEF 有一个确定的响应，而不是拿空响应去渲染。
                AbiApi.Response(response).SetStatus(502);
                AbiApi.Response(response).SetStatusText("Bad Gateway");
                return 0;
            }

            // 状态码 0 说明连状态行都没读到（比如超时路径只写了错误信息）：
            // 按旧代码的做法补成 502，而不是把 0 交给 CEF。
            AbiApi.Response(response).SetStatus(state.Status == 0 ? 502 : state.Status);
            if (!string.IsNullOrEmpty(state.StatusText))
                AbiApi.Response(response).SetStatusText(state.StatusText);

            SetResponseHeaders(response, state);

            // 关键：MIME 类型必须单独设。
            //
            // CefResponse 里 mime_type 和 Content-Type 头是两个独立字段：只把 Content-Type
            // 塞进 header map，CEF 的加载器读的是 GetMimeType()，拿不到就按未知类型处理——
            // 结果是 HTML 不会被解析成文档，页面停在 about:blank（或纯文本）。
            // 所以从 header 里取出来单独设一遍。
            string? mime = state.TryGetHeader("Content-Type", out string? contentType) ? contentType : null;
            if (!string.IsNullOrEmpty(mime))
            {
                // 去掉 "; charset=utf-8" 这类参数：SetMimeType 只接受类型本身。
                int semicolon = mime.IndexOf(';');
                AbiApi.Response(response).SetMimeType(semicolon < 0 ? mime : mime[..semicolon]);
            }

            return state.Framing switch
            {
                HttpFraming.Length => state.ContentLength ?? 0,
                HttpFraming.None => 0,
                // Chunked / UntilClose：长度未知，读到 Read 返回负数为止。
                _ => -1,
            };
        }
        catch (Exception exception)
        {
            SafeLog(AbiLogLevel.Error, "scheme handler GetResponseHeaders failed: " + exception);
            return -1;
        }
    }

    /// <summary>
    /// 读 body。返回实际写入的字节数；0 = 暂时没数据（原生侧挂起）；负数 = body 结束。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int Read(nint user, nint handler, nint buffer, int size)
    {
        try
        {
            if (buffer is 0)
                return -1;

            if (!Requests.TryGetValue(handler, out HttpResponse? state))
                return -1;

            return state.Read(new Span<byte>((void*)buffer, size));
        }
        catch (Exception exception)
        {
            SafeLog(AbiLogLevel.Error, "scheme handler Read failed: " + exception);
            return -1;
        }
    }

    /// <summary>
    /// 跳过 body 里的字节（CEF 处理 Range 请求用）。返回负数 = 失败，0 = 暂时跳不了
    /// （原生侧挂起，之后由后台任务调 <c>resume_skip</c>，参数是 <b>累计</b>跳过字节数）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int Skip(nint user, nint handler, long bytesToSkip, long* skipped)
    {
        try
        {
            if (skipped is not null)
                *skipped = 0;

            if (!Requests.TryGetValue(handler, out HttpResponse? state))
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
            SafeLog(AbiLogLevel.Error, "scheme handler Skip failed: " + exception);
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
            if (Requests.TryGetValue(handler, out HttpResponse? state))
            {
                // 标记 + 关连接：正在灌 body 的后台泵会立刻带着异常退出，
                // 不会继续往缓冲里塞没人要的数据。
                state.Cancel();
            }
        }
        catch (Exception exception)
        {
            SafeLog(AbiLogLevel.Warning, "scheme handler Cancel failed: " + exception);
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
            if (Requests.TryRemove(handler, out HttpResponse? state))
                state.Dispose();
        }
        catch (Exception exception)
        {
            SafeLog(AbiLogLevel.Warning, "scheme handler Destroy failed: " + exception);
        }
    }

    #endregion

    #region 工作线程

    // body 泵不在这层：它跟连接走，见 KestrelConnection.SendAsync / PumpBodyAsync。
    // 这里只剩「唤醒 CEF 挂起的读 / Skip」两个转发。

    /// <summary>
    /// 唤醒挂起的读，把当前可读字节数带过去。
    /// </summary>
    /// <remarks>
    /// 报告的是「缓冲里现在有多少」，因为 CEF 的 <c>ReadCallback::Continue(bytesAvailable)</c>
    /// 语义就是可用字节数（旧代码 ResumeTask 里也是这么算的）。
    /// </remarks>
    private static void WakeRead(nint handler, HttpResponse state)
    {
        int available = state.AvailableForResume();
        AbiApi.Handler(handler).ResumeRead(available);
    }

    /// <summary>
    /// 推进一次挂起的 Skip；够了就把它续上。
    /// </summary>
    /// <remarks>
    /// <b>很关键</b>：<c>obsharp_resource_handler_resume_skip</c> 的第二个参数是
    /// <b>累计</b>已跳过字节数，不是这次新跳了多少——CEF 的 <c>SkipCallback::Continue</c>
    /// 本来就是累计语义，传增量会让 Range 请求整体错位。
    /// </remarks>
    private static void WakeSkip(nint handler, HttpResponse state)
    {
        if (!state.ServicePendingSkip(out long cumulativeSkipped))
            return;

        AbiApi.Handler(handler).ResumeSkip(cumulativeSkipped);
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
            SafeLog(AbiLogLevel.Warning, "failed to dispatch a bridge message: " + exception.Message);
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
    /// 写响应头（滤掉由 CEF 自己管的 framing 头，以及不该穿透给 CEF 的缓存头）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// content-length / connection / transfer-encoding 必须滤掉：body 的分帧已经由
    /// <see cref="Read"/> 的返回值 + <c>GetResponseHeaders</c> 的长度表达，再让 CEF 看到
    /// 原始头，它会按两套互相矛盾的规则去读 body（chunked 的头 + 「长度未知」的返回值）。
    /// </para>
    /// <para>
    /// <b>缓存头同样要滤掉。</b>内容是我们通过 <c>obsharp://</c> 这个自定义 scheme 提供的，
    /// 新鲜度该由我们决定，不能让上游的语义直接穿透到 CEF。根因在于 ASP.NET 的
    /// <c>MapStaticAssets</c> 给带指纹的文件名发 <c>Cache-Control: public,max-age=31536000,immutable</c>
    /// —— 那是「这个 URL 的内容永不改变」的承诺，而 URL 里的指纹是<b>构建时</b>算出来的。
    /// 实测踩过：某次 <c>blazor.web.*.js</c> 的响应体被截断，坏副本一旦按 immutable 缓存，
    /// 就再也不会重新请求，页面永久报 <c>SyntaxError</c>，DevTools 的 Sources 里甚至看不到
    /// 这个文件（因为它根本没走网络），只能手动清 CEF 缓存才能恢复。
    /// </para>
    /// <para>
    /// 处理方式：滤掉全部缓存头，改由这里统一发 <c>no-store</c>。本地 IPC 上省掉一次往返，
    /// 远不如「看到的一定是当前构建」重要 —— 顺带的好处是改前端资源不必再手动清缓存。
    /// </para>
    /// </remarks>
    private static unsafe void SetResponseHeaders(nint response, HttpResponse head)
    {
        var kept = new List<KeyValuePair<string, string>>(head.Headers.Count);
        foreach (KeyValuePair<string, string> header in head.Headers)
        {
            if (IsManagedResponseHeader(header.Key))
                continue;

            kept.Add(header);
        }

        // 见上面 remarks。加完之后 kept 至少有两项，不会再为空。
        kept.Add(new KeyValuePair<string, string>("Cache-Control", "no-store, no-cache, must-revalidate"));
        kept.Add(new KeyValuePair<string, string>("Pragma", "no-cache"));

        ObsharpRequestHeader[] headers = new ObsharpRequestHeader[kept.Count];
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

            AbiApi.Response(response).SetHeaders(headers);
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
    private static unsafe byte* AllocateUtf8(string text, out nint block)
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
    /// 这个响应头是否由我们接管、不该原样交给 CEF。
    /// </summary>
    /// <remarks>
    /// 分两类：
    /// <list type="bullet">
    /// <item>
    /// <b>framing</b>（content-length / connection / transfer-encoding）：body 的分帧由
    /// <see cref="Read"/> 的返回值 + <c>GetResponseHeaders</c> 的长度表达，重复告诉 CEF
    /// 会让它按矛盾的规则读 body。
    /// </item>
    /// <item>
    /// <b>缓存</b>（cache-control / pragma / expires / etag / last-modified / age）：
    /// 这个 scheme 的内容由我们自己提供，新鲜度必须由我们决定（详见
    /// <see cref="SetResponseHeaders"/>）。尤其是 <c>immutable</c> —— 一旦某个坏副本
    /// 被缓存，URL 不变就永远不会重新请求。
    /// </item>
    /// </list>
    /// </remarks>
    private static bool IsManagedResponseHeader(string name)
        => name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cache-control", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pragma", StringComparison.OrdinalIgnoreCase)
            || name.Equals("expires", StringComparison.OrdinalIgnoreCase)
            || name.Equals("etag", StringComparison.OrdinalIgnoreCase)
            || name.Equals("last-modified", StringComparison.OrdinalIgnoreCase)
            || name.Equals("age", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读一个 NUL 结尾的 UTF-8 借用指针；空指针返回 <see langword="null"/>。
    /// </summary>
    private static unsafe string? Utf8ToString(byte* text)
        => text is null ? null : Marshal.PtrToStringUTF8((nint)text);

    /// <summary>
    /// 拷一份原生侧的字节块；空指针 / 非正长度当空数组。
    /// </summary>
    private static unsafe byte[] CopyBytes(void* data, int length)
    {
        if (data is null || length <= 0)
            return [];

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
    private static void LogRequest(string method, string url, HttpResponse state)
    {
        string? error = state.Error;

        string text = method + " " + url + " -> " + state.Status.ToString();
        if (!string.IsNullOrEmpty(error))
            text += " (" + error + ")";

        SafeLog(error is null ? AbiLogLevel.Info : AbiLogLevel.Warning, text);
    }

    /// <summary>
    /// 写日志，但不让「日志本身」把请求弄挂（原生库没加载、日志系统未初始化都可能抛）。
    /// </summary>
    private static void SafeLog(AbiLogLevel level, string message)
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
    /// 发请求收响应：「Request -&gt; Response」，旧 <c>RunWorkerAsync</c> 的职责收敛在这里。
    /// 同步阻塞到响应头就绪（CEF 的 Open 契约要求返回时头已经可用）；body 在返回后
    /// 由 <see cref="KestrelConnection.SendAsync"/> 起的后台泵继续灌进响应对象。
    /// </summary>
    private static HttpResponse SendRequest(Endpoint? endpoint, HttpRequest request)
    {
        if (endpoint is null)
            return HttpResponse.FromError(502, "Kestrel endpoint unavailable: no endpoint is registered for this domain");

        KestrelConnection? connection = KestrelConnection.Connect(endpoint, out string? connectError);
        if (connection is null)
            return HttpResponse.FromError(502, "Kestrel endpoint unavailable: " + connectError);

        Task<HttpResponse> pending = connection.SendAsync(request);
        try
        {
            if (pending.Wait(HeadTimeoutMilliseconds))
                return pending.Result;
        }
        catch (Exception exception)
        {
            connection.Close();
            return HttpResponse.FromError(502, "Kestrel endpoint unavailable: " +
                (exception.InnerException ?? exception).Message);
        }

        // 超时：给 504。同时关掉连接让后台泵立刻退出，否则它拿到响应头之后还会继续
        // 往一个已经当错误处理掉的响应里灌数据（旧代码这里会把两段内容混在一起）。
        connection.Close();
        _ = pending.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return HttpResponse.FromError(504, "Kestrel endpoint did not respond within 30s");
    }
}
