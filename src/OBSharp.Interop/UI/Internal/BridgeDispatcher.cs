using System.Text.Json;

namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 一条 <c>console</c> 控制消息的内容。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 里 <c>DispatchBridgeJson</c> 直接透传给 <c>on_console_message</c> 的四个参数
/// （<c>level</c> / <c>message</c> / <c>source</c> / <c>line</c>）。
/// 收成一个类而不是四个散参数：这些值总是同时出现，将来加字段（比如 <c>stack</c>）
/// 也不会把每个调用点的签名都改一遍。
/// </para>
/// <para>
/// <see cref="Level"/> 的取值跟 JS 侧的 <c>levels</c> 表对应：
/// <c>0 = debug</c>、<c>1 = log/info</c>、<c>2 = warn</c>、<c>3 = error/assert</c>。
/// </para>
/// </remarks>
internal sealed class ConsoleMessageInfo
{
    /// <summary>日志级别（0~3，见类型注释）。</summary>
    public int Level;

    /// <summary>拼好的消息文本（多个参数以空格连接）。</summary>
    public string Message = "";

    /// <summary>来源（JS 侧填的是 <c>location.href</c>）。</summary>
    public string Source = "";

    /// <summary>行号；JS 侧的 <c>console</c> 补丁固定填 0。</summary>
    public int Line;
}

/// <summary>
/// 把页面通过 bridge 通道上报的 JSON 分成「控制消息」和「普通消息」。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>DispatchBridgeJson</c>（旧项目的
/// <c>OBSharp.Interop.Browser.Native.cpp</c>，那个项目已删除，逻辑迁到这里）。
/// 通道只有一条（<c>POST /__obsharp/message</c>），所以用 JSON 里有没有
/// <c>"$obsharp"</c> 键来区分：有 = 引导脚本自己发的控制消息（console / load-end），
/// 解析后派发给对应的回调、<b>不再</b>交给 <c>on_message</c>；没有 = 页面自己调
/// <c>window.obsharp.post()</c> 发的业务消息，原样往上转。
/// </para>
/// <para>
/// 为什么把「解析 + 分流」抽成纯函数而不是直接写进回调里：它必须是<b>可测的</b>——
/// 分流的边界（脏 JSON、键存在但类型不对、未知 kind）全是容易写错的细节，而这些细节
/// 一旦错了，表现只是「回调没触发」，没有任何异常。
/// </para>
/// <para>
/// 用 <c>System.Text.Json</c> 而不是 <c>Newtonsoft</c>：AOT / trim 下前者不需要反射
/// （<see cref="JsonDocument"/> 是纯词法 + DOM 解析），后者在 AOT 里要靠一堆
/// <c>DynamicDependency</c> 才能不出问题。
/// </para>
/// </remarks>
internal static class BridgeDispatcher
{
    /// <summary>
    /// 控制消息的标识键。
    /// </summary>
    /// <remarks>
    /// 键名以 <c>$</c> 开头是刻意的：正常的业务 JSON 几乎不会用这种键名，
    /// 于是页面即使自己发一段乱七八糟的 JSON，也不会被误判成控制消息。
    /// </remarks>
    public const string ControlKey = "$obsharp";

    /// <summary>
    /// 解析并派发一条 bridge 消息。
    /// </summary>
    /// <param name="json">页面 POST 上来的原始 JSON 文本。</param>
    /// <param name="onConsole">收到 <c>console</c> 控制消息时调用；<see langword="null"/> 表示不关心。</param>
    /// <param name="onLoadEnd">
    /// 收到 <c>load-end</c> 控制消息时调用，参数是
    /// <c>(statusCode, errorCode, errorText)</c>。固定传 <c>(200, 0, "")</c>——
    /// 面板接口拿不到 <c>CefLoadHandler</c>，这只是「页面自己报告文档已加载完」，
    /// 没有真实的状态码可言。
    /// </param>
    /// <returns>
    /// <see langword="true"/> 表示这是控制消息（已经被消费，<b>不应</b>再交给 <c>on_message</c>）；
    /// <see langword="false"/> 表示不是（调用方原样转给 <c>on_message</c>）。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>不抛异常</b>：这个函数在 CEF / Qt 的回调链上执行，往上抛会穿过原生栈。
    /// JSON 解析失败、根不是对象、<c>"$obsharp"</c> 不是字符串……一律当成「不是控制消息」，
    /// 走 <c>on_message</c> 分支——这跟旧 C++ 完全一致：<c>CefParseJSON</c> 失败时
    /// <c>value</c> 为空，直接落到最后的 <c>on_message</c>。
    /// </para>
    /// <para>
    /// 未知的 <c>kind</c> 仍然返回 <see langword="true"/>：键存在就说明这是引导脚本
    /// 那一侧发的控制消息（只是我们这版不认识），把它漏给 <c>on_message</c> 反而会让
    /// 业务方收到一条自己看不懂的 JSON。旧 C++ 的行为就是「有键就 return」。
    /// </para>
    /// </remarks>
    public static bool TryDispatch(
        string json,
        Action<ConsoleMessageInfo>? onConsole,
        Action<int, int, string>? onLoadEnd)
    {
        // 空串直接走普通消息分支：JsonDocument.Parse("") 会抛，而空串本来也没有
        // 任何「这是控制消息」的迹象。
        if (string.IsNullOrEmpty(json))
            return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 脏 JSON：旧代码 CefParseJSON 失败也是走 message 分支，这里保持一致。
            return false;
        }
        catch (ArgumentException)
        {
            // **不要**把这里窄化成只 catch JsonException：JsonDocument.Parse 对
            // 「含孤立 UTF-16 代理项的字符串」不是抛 JsonException，而是抛
            // ArgumentException（"Cannot transcode invalid UTF-16 string to UTF-8 JSON text."）——
            // 它来自 UTF-16 → UTF-8 的转码，发生在 JSON 词法分析之前。
            //
            // 这一步很关键：页面字符串来自 CEF 传进来的 UTF-8 字节，按 UTF-8 解码后如果
            // 有非法序列，.NET 会把它变成 U+FFFD 而不是孤立代理项，所以正常路径碰不到
            // 这个分支；但只要有人（比如 DirectBridge 测试、或者别处拼 JSON 再转进来）
            // 递了个带孤立代理项的托管字符串，ArgumentException 就会从这里飞出去，
            // 一路穿过 CEF 的 C++ 栈 —— 那是未定义行为，可能直接崩进程。
            // 所以按「脏 JSON」处理，返回 false 交给 on_message。
            return false;
        }

        // using 而不是 finally：JsonDocument 持有池化的字节缓冲，必须 Dispose 归还，
        // 否则高频 console 消息会一直占着池子。
        using (document)
        {
            JsonElement root = document.RootElement;

            // 只有对象才可能带键。数组 / 字符串 / 数字都按普通消息处理。
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty(ControlKey, out JsonElement kindElement))
                return false;

            // kind 必须是字符串；其它类型（数字 / null）说明格式不对，但仍然算
            // 「带控制键的消息」——按上面的约定不外泄。
            if (kindElement.ValueKind != JsonValueKind.String)
                return true;

            // 取字符串可能是「有损」的：形如 "\ud800" 的**转义**孤立代理项在词法上是
            // 合法 JSON（解析阶段不抛），但 GetString() 会抛 InvalidOperationException。
            // 这里用带兜底的读取，读不出来就当空串 —— 空 kind 会落到下面的
            // 「未知 kind」分支，仍然按控制消息吃掉，不会把脏数据漏给 on_message。
            string kind = ReadStringElement(kindElement);

            if (kind == "console")
            {
                // 单个字段类型不对不影响整条消息：GetInt32 / GetString 在类型不符时会抛，
                // 所以先看 ValueKind 再取值，缺字段 / 类型错就用默认值。
                // 旧 C++ 的 CefDictionaryValue::GetInt / GetString 对缺失键返回 0 / 空串，
                // 行为一致。
                var info = new ConsoleMessageInfo
                {
                    Level = ReadInt(root, "level"),
                    Message = ReadString(root, "message"),
                    Source = ReadString(root, "source"),
                    Line = ReadInt(root, "line"),
                };

                onConsole?.Invoke(info);
                return true;
            }

            if (kind == "load-end")
            {
                // 状态码 200 / 错误码 0：见参数说明。
                onLoadEnd?.Invoke(200, 0, "");
                return true;
            }

            // 未知 kind：见 remarks，仍然吃掉。
            return true;
        }
    }

    /// <summary>
    /// 读一个整数字段；缺失或类型不符时返回 0（跟 <c>CefDictionaryValue::GetInt</c> 一致）。
    /// </summary>
    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
            return 0;

        if (element.ValueKind != JsonValueKind.Number)
            return 0;

        // 用 TryGetInt32：页面可能写了个 1e100 这种超范围的值，GetInt32 会抛。
        return element.TryGetInt32(out int value) ? value : 0;
    }

    /// <summary>
    /// 读一个字符串字段；缺失或类型不符时返回空串（跟 <c>CefDictionaryValue::GetString</c> 一致）。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="ReadStringElement"/> 而不是直接 <c>GetString()</c>：理由同那里
    /// （转义的孤立代理项会让 <c>GetString()</c> 抛）。
    /// </remarks>
    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
            return "";

        // null 也当空串：JS 里 undefined 会被 JSON.stringify 丢掉，但显式 null 会留下来。
        return ReadStringElement(element);
    }

    /// <summary>
    /// 从已经是字符串的 <see cref="JsonElement"/> 里安全地取出值；取不出来时返回空串。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不能直接 <c>element.GetString()</c>：JSON 里 <c>"\ud800"</c> 这种转义的
    /// <b>孤立代理项</b>在语法上是合法的（JSON 规范不管 Unicode 语义），解析时只做词法检查，
    /// 所以 <see cref="JsonDocument.Parse"/> 不会报错；真正把它拼成 .NET 字符串时，
    /// <c>GetString()</c> 才抛 <see cref="InvalidOperationException"/>。
    /// </para>
    /// <para>
    /// 这个异常不能漏出去：<see cref="TryDispatch"/> 在 CEF / Qt 的回调链上执行，异常穿过
    /// 原生栈是未定义行为。而且这是**页面完全可控**的输入——一段
    /// <c>{"$obsharp":"\ud800"}</c> 就能让宿主崩掉，属于必须堵的口子。
    /// </para>
    /// <para>
    /// 这也解释了为什么整个 <see cref="TryDispatch"/> 的 try/catch 只包住 Parse：
    /// 解析之后的读取用的是 <c>TryGetProperty</c> / <c>ValueKind</c> / <c>TryGetInt32</c>
    /// 这些不抛的 API，唯一会抛的就是 <c>GetString()</c>，由这里单独兜住。
    /// </para>
    /// </remarks>
    private static string ReadStringElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return "";

        try
        {
            return element.GetString() ?? "";
        }
        catch (InvalidOperationException)
        {
            // 孤立代理项等无法表示成 .NET 字符串的内容：当空串处理。
            return "";
        }
        catch (ArgumentException)
        {
            // 保险起见把转码失败也算上（不同 .NET 版本对这条路径抛的类型不完全一致）。
            return "";
        }
    }
}
