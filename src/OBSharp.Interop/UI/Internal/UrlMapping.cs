using System.Text;

namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// <c>obsharp://</c> 协议与 CEF 实际加载的 <c>https://&lt;host&gt;.obsharp/</c> 之间的映射。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>IsValidHostName</c> / <c>HasObsharpScheme</c> / <c>SplitObsharpUrl</c> /
/// <c>MapToCefUrl</c> / <c>HostDomain</c> / <c>MapObsharpUrlForBrowser</c>（<c>SchemeHandler.cpp</c>）。
/// </para>
/// <para>
/// 为什么要这一层映射：CEF 不能给自定义 scheme 注册 scheme handler 之外的东西（没有 TLS、
/// 没有 origin、安全上下文也不认），所以对外暴露的 <c>obsharp://host/path</c> 在浏览器里
/// 一律表现为 <c>https://host.obsharp/path</c>：scheme 借用 https（因此页面落在安全上下文里，
/// <c>crypto.subtle</c> / serviceWorker 之类才能用），host 借用 <c>.obsharp</c> 这个
/// 保留的假域名。请求根本不出网——scheme handler 按 (scheme, domain) 接住，转给 IPC 端点。
/// </para>
/// <para>
/// <b>host 名只能是 <c>^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$</c></b>（1~48 字符）。这不是
/// 苛刻，而是安全边界：host 会直接变成域名里的一段，允许大写 / 点 / 下划线 / 冒号就有
/// 办法把流量导向别的域名或伪造端口。
/// </para>
/// </remarks>
internal static class UrlMapping
{
    /// <summary>
    /// <c>"obsharp://"</c> 的长度。scheme 前缀一律按小写比较（大小写不敏感）。
    /// </summary>
    private const int ObsharpSchemeLength = 10;

    /// <summary>
    /// host 名的最大长度，跟 C++ 侧 <c>IsValidHostName</c> 的上界一致。
    /// </summary>
    private const int MaxHostLength = 48;

    /// <summary>
    /// host 名是否合法。
    /// </summary>
    /// <param name="host">待校验的 host；<see langword="null"/> / 空视为非法。</param>
    /// <remarks>
    /// 规则逐字照搬旧 C++：长度 1~48；首尾不能是 <c>'-'</c>；只允许 <c>a-z</c>、
    /// <c>0-9</c>、<c>'-'</c>。<b>大写也不行</b>——host 会出现在域名里，而域名比较本身
    /// 是大小写不敏感的，放行大写等于允许同一 host 有两种写法，后面做「url host 与
    /// 声明 host 是否相等」的比较时就要额外处理，索性在入口处统一成小写字母表。
    /// </remarks>
    public static bool IsValidHostName(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > MaxHostLength)
            return false;

        if (host[0] == '-' || host[^1] == '-')
            return false;

        foreach (char c in host)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
                return false;
        }

        return true;
    }

    /// <summary>
    /// URL 是不是以 <c>obsharp://</c> 开头（大小写不敏感）。
    /// </summary>
    /// <param name="url">待检查的 URL；<see langword="null"/> 视为否。</param>
    /// <remarks>
    /// 只认前缀，不校验后面的部分：「是不是 obsharp URL」和「这个 obsharp URL 合不合法」
    /// 是两个问题，前者决定走哪条分支，后者才报 <c>malformed obsharp URL</c>。
    /// 大小写不敏感是因为浏览器 / 用户手输的 scheme 大小写不受控（RFC 3986 也说 scheme
    /// 大小写不敏感）。
    /// </remarks>
    public static bool HasObsharpScheme(string? url)
        => url is not null &&
           url.Length >= ObsharpSchemeLength &&
           string.Compare(url, 0, "obsharp://", 0, ObsharpSchemeLength, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>
    /// 把 <c>obsharp://&lt;host&gt;&lt;rest&gt;</c> 拆成 host 与剩余部分。
    /// </summary>
    /// <param name="url">完整的 obsharp URL。</param>
    /// <param name="host">拆出来的 host 名（不含 <c>://</c>、不含路径）。</param>
    /// <param name="rest">其余部分，从 <c>'/'</c>、<c>'?'</c> 或 <c>'#'</c> 开始；没有则空串。</param>
    /// <returns>能拆出合法 host 时返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// host 到第一个 <c>'/'</c>、<c>'?'</c> 或 <c>'#'</c> 为止（<c>obsharp://&lt;host&gt;?x</c>
    /// 也算合法写法）。<c>'#'</c> 也算分隔符不是笔误：fragment 不属于 host。
    /// </para>
    /// <para>
    /// host 里出现 <c>':'</c> 或 <c>'@'</c> 一律判非法——那是「端口 / 用户信息」的语法，
    /// 不属于 host 名。放行它们等于允许 <c>obsharp://evil.com:443@x/</c> 这类伪造写法。
    /// </para>
    /// <para>
    /// 这里<b>不做</b> <see cref="IsValidHostName"/> 的字符校验：拆分是语法层面的，
    /// 字符集校验由调用方在确定「host 从哪来」之后再做（比如
    /// <see cref="TryMapObsharpUrlForBrowser"/> 只跟浏览器的 host 比相等，不校验字符集）。
    /// </para>
    /// </remarks>
    public static bool TrySplitObsharpUrl(string url, out string host, out string rest)
    {
        host = "";
        rest = "";

        if (!HasObsharpScheme(url))
            return false;

        string remainder = url[ObsharpSchemeLength..];

        // 用第一个 '/'、'?'、'#' 截断。注意 '?' 和 '#' 是**分隔符**而不是「保留在 rest 里
        // 的前缀，会被原样带进 rest」——所以 rest 里可能出现 "?x" 这种不以 '/' 开头的串，
        // MapToCefUrl 就是为这个才补斜杠的。
        int end = remainder.IndexOfAny(['/', '?', '#']);
        string name = end < 0 ? remainder : remainder[..end];

        if (name.Length == 0 || name.IndexOfAny([':', '@']) >= 0)
            return false;

        host = name;
        rest = end < 0 ? "" : remainder[end..];
        return true;
    }

    /// <summary>
    /// 把 (scheme, host, rest) 拼成 CEF 实际要加载的 URL。
    /// </summary>
    /// <param name="scheme">scheme；空 / <see langword="null"/> 时退回 <c>"https"</c>。</param>
    /// <param name="host">host 名（已经校验过的那一个）。</param>
    /// <param name="rest">路径 / 查询串 / fragment；允许为空、允许不以 <c>'/'</c> 开头。</param>
    /// <remarks>
    /// 结果形如 <c>&lt;scheme&gt;://&lt;host&gt;.obsharp&lt;rest&gt;</c>。三条补斜杠的规则：
    /// rest 为空 → 补 <c>"/"</c>（否则 URL 少了根路径，CEF 的规范化会自己补，但两边就不一致了）；
    /// rest 不以 <c>'/'</c> 开头 → 补一个（<c>obsharp://h?x</c> 拆出来的 rest 是 <c>"?x"</c>）；
    /// 否则原样。
    /// </remarks>
    public static string MapToCefUrl(string? scheme, string host, string rest)
    {
        var url = new StringBuilder();
        url.Append(string.IsNullOrEmpty(scheme) ? "https" : scheme);
        url.Append("://");
        url.Append(host);
        url.Append(".obsharp");

        if (string.IsNullOrEmpty(rest))
            url.Append('/');
        else if (rest[0] != '/')
            url.Append('/').Append(rest);
        else
            url.Append(rest);

        return url.ToString();
    }

    /// <summary>
    /// host 对应的假域名（<c>&lt;host&gt;.obsharp</c>）。
    /// </summary>
    /// <param name="host">host 名。</param>
    /// <remarks>
    /// scheme handler 是按 (scheme, domain) 注册的，所以「这个浏览器属于哪个 host」
    /// 在 CEF 那一侧就体现为 domain。单独抽成函数是为了让注册和映射两处永远算出同一个串。
    /// </remarks>
    public static string HostDomain(string host) => host + ".obsharp";

    /// <summary>
    /// 把要在某个浏览器里加载的 URL 映射成 CEF 形式，并校验 host 一致。
    /// </summary>
    /// <param name="browserHost">该浏览器绑定的 host（<c>options-&gt;host</c> / 从 url 推出）；没有则空。</param>
    /// <param name="url">调用方给的 URL，可能是 <c>obsharp://</c>，也可能是普通 http(s)。</param>
    /// <param name="scheme">要用的 scheme（<c>https</c> / <c>http</c>）。</param>
    /// <param name="mapped">映射结果；失败时是空串。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>映射（或原样透传）成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 非 <c>obsharp://</c> 的 URL 原样透传并返回成功——那是普通网络地址，跟本库的
    /// scheme handler 无关（v1 行为）。
    /// </para>
    /// <para>
    /// <c>obsharp://</c> 时必须三道校验，顺序不能换：
    /// 1) 浏览器有 host 吗（没有 host 就没有 scheme handler，映射出来的域名没人接）；
    /// 2) URL 本身能拆吗（拆不动 = 调用方才给的串就是坏的）；
    /// 3) URL 的 host 跟浏览器的 host 一致吗（跨 host 访问必须拒绝：否则一个页面就能
    /// 顺着另一个 host 的端点读到别人的插件数据）。
    /// </para>
    /// </remarks>
    public static bool TryMapObsharpUrlForBrowser(
        string? browserHost, string url, string? scheme, out string mapped, out string? error)
    {
        mapped = "";

        if (!HasObsharpScheme(url))
        {
            mapped = url;
            error = null;
            return true;
        }

        if (string.IsNullOrEmpty(browserHost))
        {
            // 这是使用错误而不是运行期故障：调用方给了个 obsharp:// 地址，却没告诉
            // 浏览器它是哪个 host。文案里直接写解法（set options->host）。
            error = "the URL is obsharp:// but this browser has no host; set options->host";
            return false;
        }

        if (!TrySplitObsharpUrl(url, out string host, out string rest))
        {
            error = "malformed obsharp URL: " + url;
            return false;
        }

        if (!string.Equals(host, browserHost, StringComparison.Ordinal))
        {
            error = "the URL host \"" + host + "\" does not match the browser host \"" + browserHost + "\"";
            return false;
        }

        mapped = MapToCefUrl(scheme, host, rest);
        error = null;
        return true;
    }
}
