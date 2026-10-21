namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 一个浏览器实例解析完成之后的「宿主配置」：绑定的 host / scheme / domain、IPC 端点、初始 URL。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>obsharp::BrowserHostConfig</c>（<c>BrowserWidget.cpp</c>）。
/// 这是 <see cref="BrowserHostConfigResolver.TryResolve"/> 的产物，也是后续所有动作
/// （注册 scheme handler、建 widget、注入引导脚本、开 IPC 连接）的唯一输入——
/// 把「参数怎么解释」集中在一处算完，下游就不必再各自重算一遍，也就不会出现
/// 「注册用的 domain 和导航用的 domain 不一样」这类分家问题。
/// </para>
/// <para>
/// 注意 <see cref="Domain"/> 与 <see cref="Host"/> 的关系：有 host 时 domain 一定是
/// <c>&lt;host&gt;.obsharp</c>（见 <see cref="UrlMapping.HostDomain"/>）；没有 host 时才可能
/// 是调用方指定 / 默认的 <c>"obsharp"</c>（v1 行为）。
/// </para>
/// </remarks>
internal sealed class BrowserHostConfig
{
    /// <summary>
    /// 绑定的 host 名（已校验）；没有 host 时是空串（v1 行为，不注册 scheme handler）。
    /// </summary>
    public string Host = "";

    /// <summary>
    /// 浏览器里使用的 scheme。默认 <c>"https"</c>。
    /// </summary>
    /// <remarks>
    /// 默认 https 而不是 http：页面因此落在<b>安全上下文</b>里（<c>crypto.subtle</c> /
    /// serviceWorker / clipboard 这些只在安全上下文开放的 API 才能用）。但<b>并不做 TLS</b>——
    /// 请求由本库的 scheme handler 接住直接转给 IPC 端点，不走网络栈、也不握手。
    /// </remarks>
    public string Scheme = "https";

    /// <summary>
    /// scheme handler 注册用的 domain。有 host 时是 <c>&lt;host&gt;.obsharp</c>。
    /// </summary>
    public string Domain = "obsharp";

    /// <summary>
    /// IPC 端点；<see cref="HasEndpoint"/> 为 <see langword="false"/> 时是 <see langword="null"/>。
    /// </summary>
    public Endpoint? Endpoint;

    /// <summary>
    /// 交给 CEF 的初始 URL（已经映射成 <c>&lt;scheme&gt;://&lt;domain&gt;/...</c> 形式）。
    /// </summary>
    public string Url = "";

    /// <summary>
    /// 是否要注册 scheme handler / 开 IPC 连接。
    /// </summary>
    /// <remarks>
    /// 显式给了端点、或者有 host 能推出默认端点时为 <see langword="true"/>；两者都没有时
    /// 为 <see langword="false"/>——那种情况下浏览器按普通网络访问 <see cref="Url"/>
    /// （v1 行为），引导脚本也就不该注入（它的 <c>fetch('/__obsharp/message')</c> 没人接）。
    /// </remarks>
    public bool HasEndpoint;
}

/// <summary>
/// 把调用方给的浏览器参数解析成 <see cref="BrowserHostConfig"/>。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>ResolveBrowserHostConfig</c> + <c>SanitizeBrowserOptions</c>
/// （<c>BrowserWidget.cpp</c>）。<c>SanitizeBrowserOptions</c> 那一半（按 <c>size</c> 字段
/// 裁结构体）是 C ABI 的兼容性处理，托管侧拿到的参数已经是解析好的值，这里不复刻。
/// </para>
/// <para>
/// 这里做的每一条校验都对应一个「沉默的故障」：host 不合法 → scheme handler 注册到
/// 一个根本不会被访问的域名上；url host 与 host 不一致 → 页面加载的域和注册的域不是
/// 同一个；端点推不出来 → 页面请求直接出网（或者 404）。所以宁可在入口处报错。
/// </para>
/// </remarks>
internal static class BrowserHostConfigResolver
{
    /// <summary>
    /// 解析浏览器参数。
    /// </summary>
    /// <param name="url">初始 URL；可能是 <c>obsharp://&lt;host&gt;/...</c>，也可能是普通地址。</param>
    /// <param name="scheme">scheme 覆盖值；空表示用默认。</param>
    /// <param name="domain">domain 覆盖值（只在没有 host 时有意义）。</param>
    /// <param name="endpoint">显式指定的 IPC 端点地址；空表示按 host 推导。</param>
    /// <param name="endpointKind"><paramref name="endpoint"/> 的类型。</param>
    /// <param name="host">显式声明的 host（<c>options-&gt;host</c>）。</param>
    /// <param name="config">解析结果（失败时不要用）。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    public static bool TryResolve(
        string? url,
        string? scheme,
        string? domain,
        string? endpoint,
        OBSharpEndpointKind endpointKind,
        string? host,
        out BrowserHostConfig config,
        out string? error)
    {
        // 先给一个确定值：out 参数在失败分支里也不该是 null。
        config = new BrowserHostConfig();

        // 默认值。scheme 默认 https 的理由见 BrowserHostConfig.Scheme 的注释；
        // domain 默认 "obsharp" 是 v1 的约定（没有 host 时页面就是 https://obsharp/）。
        config.Scheme = "https";
        config.Domain = "obsharp";

        // NonEmpty 语义：null 和空串都算「没给」。
        string? schemeValue = string.IsNullOrEmpty(scheme) ? null : scheme;
        string? domainValue = string.IsNullOrEmpty(domain) ? null : domain;
        string? endpointValue = string.IsNullOrEmpty(endpoint) ? null : endpoint;
        string? urlValue = string.IsNullOrEmpty(url) ? null : url;
        string declaredHost = host ?? "";

        if (schemeValue is not null)
            config.Scheme = schemeValue;

        // host 从两处来：显式声明，或者 url 里的 obsharp://<host>/...
        // 两个都给了就必须一致，否则是参数错误——不一致时无论选哪一个，都会有一半
        // 配置（注册的 domain / 加载的 URL）指向错误的地方。
        string urlHost = "";
        string urlRest = "";
        if (urlValue is not null && UrlMapping.HasObsharpScheme(urlValue) &&
            !UrlMapping.TrySplitObsharpUrl(urlValue, out urlHost, out urlRest))
        {
            // 是 obsharp:// 却拆不出 host：这条 URL 本身坏了，比「host 不一致」更根本，
            // 所以先报这个（跟旧 C++ 的判断顺序一致）。
            error = "malformed obsharp URL: " + urlValue;
            return false;
        }

        string resolvedHost = declaredHost;
        if (resolvedHost.Length == 0)
        {
            // 没声明就从 url 里取。注意 url 不是 obsharp:// 时 urlHost 是空串，
            // 于是 resolvedHost 也是空——那是「普通网络地址」的正常情况。
            resolvedHost = urlHost;
        }
        else if (urlHost.Length != 0 && !string.Equals(urlHost, declaredHost, StringComparison.Ordinal))
        {
            error = "url host \"" + urlHost + "\" does not match options->host \"" + declaredHost + "\"";
            return false;
        }

        if (resolvedHost.Length != 0)
        {
            if (!UrlMapping.IsValidHostName(resolvedHost))
            {
                // 文案里带上正则：host 名的规则（小写、只允许字母数字和连字符、
                // 首尾不能是连字符、1~48 字符）是给调用方看的契约，不写出来只能靠猜。
                error = "invalid host name \"" + resolvedHost +
                        "\": expected ^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$";
                return false;
            }

            config.Host = resolvedHost;
            // domain 必须由 host 推导，不能沿用调用方给的：scheme handler 按
            // (scheme, domain) 注册，而 URL 映射出来的是 <host>.obsharp，
            // 两者不一致就等于注册了一个没人访问的键。
            config.Domain = UrlMapping.HostDomain(resolvedHost);
        }
        else if (domainValue is not null)
        {
            // 没有 host 时沿用 v1 的 scheme + domain（domain 默认 "obsharp"）。
            config.Domain = domainValue;
        }

        // 端点：调用方显式给了就用它，否则有 host 时按平台从 host 推导；两者都没有
        // 就不注册 scheme handler（浏览器按普通网络访问 url，也就是 v1 行为）。
        if (endpointValue is not null)
        {
            config.Endpoint = new Endpoint
            {
                Kind = endpointKind,
                Address = endpointValue,
            };
            config.HasEndpoint = true;
        }
        else if (config.Host.Length != 0)
        {
            // 从 host 推导出来的端点本来就是平台默认的那种，所以强制 Auto：
            // 否则调用方顺手填了个 UnixSocket 就会把它带偏（Windows 上根本没那种东西）。
            config.Endpoint = new Endpoint
            {
                Kind = OBSharpEndpointKind.Auto,
                Address = EndpointResolver.DefaultEndpointForHost(config.Host),
            };
            config.HasEndpoint = true;
        }

        // 交给 CEF 的初始 URL。
        if (urlValue is null)
        {
            // 没给 url：有 host 就映射成 <scheme>://<host>.obsharp/（注册的 domain 对得上），
            // 否则退回 v1 的 <scheme>://<domain>/。
            config.Url = config.Host.Length == 0
                ? config.Scheme + "://" + config.Domain + "/"
                : UrlMapping.MapToCefUrl(config.Scheme, config.Host, "");
            error = null;
            return true;
        }

        if (UrlMapping.HasObsharpScheme(urlValue))
        {
            // 前面已经保证：能走到这里说明 urlHost 与 config.Host 一致（或者 config.Host
            // 就是从 urlHost 来的），所以用 config.Host 映射不会产生跨 host 的地址。
            config.Url = UrlMapping.MapToCefUrl(config.Scheme, config.Host, urlRest);
            error = null;
            return true;
        }

        // 普通地址：原样交给 CEF（v1 行为）。
        config.Url = urlValue;
        error = null;
        return true;
    }
}
