namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 注入到每个页面文档的启动脚本（引导脚本）的拼装。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>kBootstrapScript</c> + <c>BuildStartupScript</c>（<c>BrowserWidget.cpp</c>）。
/// 脚本体是<b>逐字</b>从 C++ 的 raw string 搬过来的：它是跟页面之间的接口约定
/// （<c>window.obsharp.post()</c> / <c>control()</c>），写法上的任何「改进」都会让
/// 页面里已经存在的代码失效。
/// </para>
/// <para>
/// 为什么脚本长这个样子：obs-browser 的面板接口（<c>BROWSER_PANEL_VERSION 3</c>）只暴露
/// <c>titleChanged</c> / <c>urlChanged</c> 两个信号，拿不到 <c>CefLoadHandler</c> /
/// <c>CefDisplayHandler</c> 的回调。所以「加载结束」和「console 消息」两类事件只能由
/// 脚本自己上报：它们都走同一条通道（<c>POST /__obsharp/message</c>），带 <c>"$obsharp"</c>
/// 键的 JSON 是控制消息（由 <see cref="BridgeDispatcher"/> 解析派发），其余原样交给
/// <c>on_message</c>。
/// </para>
/// <para>
/// 有 host 时（<c>obsharp://</c> 协议），CEF 里实际是 <c>https://&lt;host&gt;.obsharp/</c>，
/// 所以脚本里另外给出 <c>window.obsharp.host</c> 与 <c>window.obsharp.url</c>，
/// 让页面知道插件 API 那一侧的写法。
/// </para>
/// </remarks>
internal static class BootstrapScript
{
    /// <summary>
    /// 引导脚本体，原文照搬 <c>BrowserWidget.cpp</c> 的 <c>kBootstrapScript</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用非插值的 raw string（<c>"""..."""</c>）而不是 <c>$"..."</c>：脚本里有大量 <c>{</c> /
    /// <c>}</c>（对象字面量、函数体），插值字符串会把它们当格式占位符，要么编译报错
    /// 要么要写成 <c>{{</c>，那就不是「原文照搬」了。需要替换的只有下面那一行占位符，
    /// 用 <see cref="string.Replace(string, string)"/> 处理即可。
    /// </para>
    /// <para>
    /// 首尾各显式拼了一个 <c>'\n'</c>：C++ 的 <c>R"JS(</c> 后面紧跟一个换行、<c>)JS"</c> 前面
    /// 也是换行，这两个换行都属于字面量的内容；而 C# 的 raw string 会吃掉紧跟开头 <c>"""</c>
    /// 的换行、以及紧挨结尾 <c>"""</c> 之前的那个换行。所以两边各补一个，好让最终注入的
    /// 脚本文本跟旧实现<b>逐字节</b>一致（对执行没影响，但注入脚本会被页面 / 测试拿去比对，
    /// 差一个换行就会被当成「行为变了」）。
    /// </para>
    /// <para>
    /// 唯一的、不可避免的差异是行尾：旧 C++ 的字面量在仓库里是 CRLF，编译进去就是
    /// <c>"\r\n"</c>；这里是 <c>"\n"</c>。JS 把两者当同一个行终止符，而 C# 源码若写成
    /// <c>"\r\n"</c> 反而会在 Linux 构建 / 比对时制造噪音，所以统一用 LF。
    /// </para>
    /// </remarks>
    private const string Body = """

        (function () {
        if (window.obsharp && window.obsharp.__bootstrap) { return; }
        var api = window.obsharp || (window.obsharp = {});
        api.__bootstrap = true;
        __host__
        function send(value) {
            try {
            return fetch('/__obsharp/message', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(value === undefined ? null : value)
            });
            } catch (error) {
            return Promise.reject(error);
            }
        }

        function control(kind, extra) {
            var payload = { $obsharp: kind };
            if (extra) {
            for (var key in extra) {
                if (Object.prototype.hasOwnProperty.call(extra, key)) { payload[key] = extra[key]; }
            }
            }
            return send(payload);
        }

        function stringify(value) {
            if (typeof value === 'string') { return value; }
            try { return JSON.stringify(value); } catch (error) { return String(value); }
        }

        api.post = function (data) { return send(data); };
        api.control = control;

        if (!api.__consolePatched && window.console) {
            api.__consolePatched = true;
            var levels = { debug: 0, log: 1, info: 1, warn: 2, error: 3, assert: 3 };
            Object.keys(levels).forEach(function (name) {
            var original = console[name];
            if (typeof original !== 'function') { return; }
            console[name] = function () {
                try {
                var parts = [];
                for (var i = 0; i < arguments.length; i++) { parts.push(stringify(arguments[i])); }
                control('console', { level: levels[name], message: parts.join(' '), source: String(location.href), line: 0 });
                } catch (error) { /* 忽略 */ }
                return original.apply(console, arguments);
            };
            });
        }

        function reportLoad() {
            window.removeEventListener('load', reportLoad);
            try { control('load-end', { url: String(location.href) }); } catch (error) { /* 忽略 */ }
        }

        if (document.readyState === 'complete') {
            reportLoad();
        } else {
            window.addEventListener('load', reportLoad);
        }
        })();

        """;

    /// <summary>
    /// 脚本里代表「host 相关几行」的占位行（含两个前导空格，跟 JS 用两空格缩进一致）。
    /// </summary>
    /// <remarks>
    /// 占位符带缩进是为了让原文里的 <c>__host__</c> 看起来像正常的一行语句；替换 / 删除
    /// 时按整段（含空格）匹配，这样不管替换成什么，缩进都是对的。
    /// </remarks>
    private const string HostPlaceholder = "  __host__";

    /// <summary>
    /// 拼出最终注入的启动脚本。
    /// </summary>
    /// <param name="userScript">
    /// 调用方自己的脚本（<c>options-&gt;startup_script</c>）；空 / <see langword="null"/> 表示没有。
    /// </param>
    /// <param name="host">
    /// 浏览器绑定的 host；空 / <see langword="null"/> 表示没有 host（v1 行为，
    /// 脚本里不提供 <c>api.host</c> / <c>api.url</c>）。
    /// </param>
    /// <remarks>
    /// <para>
    /// 有 host 时把占位行换成两行赋值；没有 host 时把占位行删掉（脚本仍然可用，
    /// 只是页面不知道自己的规范地址）。
    /// </para>
    /// <para>
    /// 用户脚本拼在<b>最后</b>：它可能想覆盖 <c>window.obsharp.post</c> 之类的成员，
    /// 拼在前面会被随后的引导脚本覆盖掉。
    /// </para>
    /// </remarks>
    public static string Build(string? userScript, string? host)
    {
        string script = Body;

        // host 块以换行开头、不带结尾换行：占位行自己的换行留在原处，正好把
        // 「api.url = ...;」和下一行「function send」隔开。这样有没有 host，
        // 脚本的行结构都是一致的（旧 C++ 就是这么拼的）。
        string hostBlock = "";
        if (!string.IsNullOrEmpty(host))
        {
            hostBlock = "\n  api.host = \"" + host + "\";\n  api.url = \"obsharp://" + host + "/\";";
        }

        // 只替换一次（Replace 默认替换全部，但整段脚本里这个占位符只出现一次；
        // 用 Replace 而不是 IndexOf + 手工拼接，是因为它天然处理「找不到就原样返回」）。
        script = script.Replace(HostPlaceholder, hostBlock, StringComparison.Ordinal);

        // 用户脚本：前面补一个换行，避免跟引导脚本的最后一行（"})();"）粘在一起
        // ——粘上就是语法错误，而这个错误在页面里表现为「整段脚本没执行」，很难查。
        if (!string.IsNullOrEmpty(userScript))
            script += "\n" + userScript;

        return script;
    }
}
