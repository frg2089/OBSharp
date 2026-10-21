using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 响应 body 的传输方式（对应旧 <c>HttpResponseHead::Framing</c>）。
/// </summary>
internal enum HttpFraming
{
    /// <summary>
    /// 没有 body（1xx / 204 / 304）。
    /// </summary>
    None,

    /// <summary>
    /// <c>Content-Length</c> 给定长度。
    /// </summary>
    Length,

    /// <summary>
    /// <c>Transfer-Encoding: chunked</c>。
    /// </summary>
    Chunked,

    /// <summary>
    /// 一直读到对端关闭。
    /// </summary>
    UntilClose,
}

/// <summary>
/// 要转给 Kestrel 的请求（已经拆成 HTTP/1.1 需要的样子）。
/// </summary>
/// <remarks>
/// <see cref="Target"/> 是 path + query，<see cref="Host"/> 只用于 <c>Host:</c> 头。
/// 对应旧 <c>obsharp::HttpRequest</c>。
/// </remarks>
internal sealed class HttpRequest
{
    /// <summary>HTTP 方法，默认 <c>GET</c>。</summary>
    public string Method = "GET";

    /// <summary>path + query，默认 <c>/</c>。</summary>
    public string Target = "/";

    /// <summary><c>Host:</c> 头的值；空时用 <c>obsharp</c>。</summary>
    public string Host = "";

    /// <summary>原请求头。发送时会被滤掉 host / connection / accept-encoding / content-length。</summary>
    public List<KeyValuePair<string, string>> Headers = new();

    /// <summary>请求体原始字节。</summary>
    public byte[] Body = Array.Empty<byte>();
}

/// <summary>
/// 响应头 + body 的传输方式（对应旧 <c>obsharp::HttpResponseHead</c>）。
/// </summary>
internal sealed class HttpResponseHead
{
    /// <summary>状态码；解析不出来时是 0。</summary>
    public int Status;

    /// <summary>状态描述（<c>"OK"</c> / <c>"Not Found"</c>……）；没有时是空串。</summary>
    public string StatusText = "";

    /// <summary>响应头，保持到达顺序，名字大小写保持原样。</summary>
    public List<KeyValuePair<string, string>> Headers = new();

    /// <summary>body 的读法。</summary>
    public HttpFraming Framing;

    /// <summary><see cref="HttpFraming.Length"/> 时的 body 长度。</summary>
    public long ContentLength;

    /// <summary>
    /// 按名字取第一个匹配的头值（大小写不敏感）。没有时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 旧代码返回空串（<c>std::string</c> 只能这么返回）；托管侧用可空引用区分
    /// 「没有这个头」和「有这个头但值是空」，调用点的判断反而更直白。
    /// </remarks>
    public string? HeaderValue(string name)
    {
        foreach (KeyValuePair<string, string> header in Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }

        return null;
    }

    /// <summary>
    /// 是否存在某个头（大小写不敏感）。
    /// </summary>
    public bool HasHeader(string name)
    {
        foreach (KeyValuePair<string, string> header in Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// 一条全双工的字节连接（Windows 命名管道 / Unix domain socket）。
/// </summary>
/// <remarks>
/// <para>
/// 旧代码在这里用的是 <c>CreateFileW</c> / <c>socket(2)</c> 加一堆 Win32 错误码判断。
/// 现在换成托管 API：命名管道用 <see cref="NamedPipeClientStream"/>，UDS 用
/// <see cref="Socket"/> + <see cref="UnixDomainSocketEndPoint"/>——不用 P/Invoke，
/// 也就不用自己管 <c>HANDLE</c> / <c>fd</c> 的关闭时机。
/// </para>
/// <para>
/// 关键语义跟旧 <c>KestrelConnection</c> 一一对应：读<b>返回 0 字节 = 对端正常关闭</b>
/// （不是错误），只有真正的 IO 失败才报错。上层 <see cref="KestrelSession"/> 用这个区别
/// 决定「body 结束」还是「连接断了」。
/// </para>
/// </remarks>
internal abstract class KestrelConnection : IDisposable
{
    /// <summary>
    /// 连接超时（毫秒）。本地管道正常几毫秒就绪；给这么长只是为了避免
    /// 「管道存在但没有空闲实例」时无限挂住调用方的工作线程。
    /// </summary>
    private const int ConnectTimeoutMilliseconds = 5000;

    /// <summary>
    /// 建连接。失败返回 <see langword="null"/> 并写 <paramref name="error"/>。
    /// </summary>
    public static KestrelConnection? Connect(Endpoint endpoint, out string? error)
    {
        error = null;

        // EndpointResolver 已经把 Auto 解析成具体值了；这里再兜一次底，免得手工构造的
        // Endpoint 把连接跑到错误的分支上（表现会是「连不上」而看不出原因）。
        OBSharpEndpointKind kind = endpoint.Kind;
        if (kind == OBSharpEndpointKind.Auto)
            kind = OperatingSystem.IsWindows() ? OBSharpEndpointKind.NamedPipe : OBSharpEndpointKind.UnixSocket;

        if (kind == OBSharpEndpointKind.NamedPipe)
        {
            if (!OperatingSystem.IsWindows())
            {
                error = "named pipes are only available on Windows";
                return null;
            }

            // 归一化后的地址是 \\.\pipe\obsharp\<host>；NamedPipeClientStream 要的是
            // 「服务器名 + 裸管道名」，所以必须把前缀拆掉（旧代码是整串交给 CreateFileW）。
            SplitPipeAddress(endpoint.Address, out string serverName, out string pipeName);

            var pipe = new NamedPipeClientStream(serverName, pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(ConnectTimeoutMilliseconds);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                pipe.Dispose();
                error = "CreateFileW(" + endpoint.Address + ") failed: " + exception.Message;
                return null;
            }

            return new PipeConnection(pipe);
        }

        if (kind == OBSharpEndpointKind.UnixSocket)
        {
            if (OperatingSystem.IsWindows())
            {
                error = "unix domain sockets are not available on Windows";
                return null;
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(endpoint.Address));
            }
            catch (Exception exception) when (exception is SocketException or PlatformNotSupportedException
                or ArgumentException)
            {
                socket.Dispose();
                error = "connect(" + endpoint.Address + ") failed: " + exception.Message;
                return null;
            }

            return new SocketConnection(socket);
        }

        error = "unknown endpoint kind";
        return null;
    }

    /// <summary>
    /// 全部写完返回 <see langword="true"/>；失败写 <paramref name="error"/>。
    /// </summary>
    public abstract bool TryWriteAll(ReadOnlySpan<byte> data, out string? error);

    /// <summary>
    /// 读一段。返回 <see langword="true"/> 且 <paramref name="bytesRead"/> 为 0 表示对端关闭；
    /// 返回 <see langword="false"/> 表示出错。
    /// </summary>
    public abstract bool TryRead(Span<byte> destination, out int bytesRead, out string? error);

    /// <summary>
    /// 关闭连接（可以重复调用，也可以从别的线程调用）。
    /// </summary>
    public abstract void Close();

    /// <inheritdoc />
    public void Dispose() => Close();

    /// <summary>
    /// 把归一化后的管道地址拆成「服务器名 + 裸管道名」。
    /// </summary>
    /// <remarks>
    /// 归一化后的形态是 <c>\\.\pipe\obsharp\&lt;host&gt;</c>。也接受调用方直接写
    /// <c>//server/pipe/name</c>（旧 NormalizeEndpoint 允许正斜杠）和裸管道名。
    /// </remarks>
    private static void SplitPipeAddress(string address, out string serverName, out string pipeName)
    {
        serverName = ".";
        pipeName = address;

        string normalized = address.Replace('/', '\\');
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return; // 已经是裸管道名

        string[] parts = normalized[2..].Split('\\');
        if (parts.Length > 0 && parts[0].Length > 0)
            serverName = parts[0];

        int pipeIndex = -1;
        for (int i = 1; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], "pipe", StringComparison.OrdinalIgnoreCase))
            {
                pipeIndex = i;
                break;
            }
        }

        int nameStart = pipeIndex >= 0 ? pipeIndex + 1 : 1;
        if (nameStart < parts.Length)
            pipeName = string.Join('\\', parts, nameStart, parts.Length - nameStart);
    }

    /// <summary>
    /// 这个 IO 异常是不是「对端已经关闭」。旧代码按 Win32 错误码判断，托管侧读 HResult 低 16 位。
    /// </summary>
    private static bool IsPeerClosed(IOException exception) => (exception.HResult & 0xFFFF) switch
    {
        109 => true, // ERROR_BROKEN_PIPE
        232 => true, // ERROR_NO_DATA
        38 => true,  // ERROR_HANDLE_EOF
        233 => true, // ERROR_PIPE_NOT_CONNECTED
        _ => false,
    };

    /// <summary>
    /// Windows 命名管道连接。
    /// </summary>
    private sealed class PipeConnection : KestrelConnection
    {
        private readonly NamedPipeClientStream _pipe;
        private int _closed;

        public PipeConnection(NamedPipeClientStream pipe) => _pipe = pipe;

        public override bool TryWriteAll(ReadOnlySpan<byte> data, out string? error)
        {
            error = null;
            try
            {
                // Stream.Write 自己负责写满或抛异常，不需要像旧代码那样循环 WriteFile。
                _pipe.Write(data);
                _pipe.Flush();
                return true;
            }
            catch (ObjectDisposedException)
            {
                error = "the connection to the Kestrel endpoint was closed";
                return false;
            }
            catch (IOException exception) when (IsPeerClosed(exception))
            {
                error = "the Kestrel endpoint closed the connection while writing";
                return false;
            }
            catch (IOException exception)
            {
                error = "WriteFile failed: " + exception.Message;
                return false;
            }
        }

        public override bool TryRead(Span<byte> destination, out int bytesRead, out string? error)
        {
            bytesRead = 0;
            error = null;
            try
            {
                // Read 在管道上返回 0 就表示对端关闭——跟旧代码把
                // ERROR_BROKEN_PIPE / ERROR_HANDLE_EOF / ERROR_PIPE_NOT_CONNECTED
                // 映射成「读到 0 字节、返回成功」是同一个意思。
                bytesRead = _pipe.Read(destination);
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Cancel() 会从别的线程关掉连接，正在阻塞的读就会落在这里。
                // 当成错误返回，工作线程据此退出（绝不会把半截数据当正文）。
                error = "the connection to the Kestrel endpoint was closed";
                return false;
            }
            catch (IOException exception) when (IsPeerClosed(exception))
            {
                return true; // bytesRead 已经是 0
            }
            catch (IOException exception)
            {
                error = "ReadFile failed: " + exception.Message;
                return false;
            }
        }

        public override void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            _pipe.Dispose();
        }
    }

    /// <summary>
    /// Unix domain socket 连接。
    /// </summary>
    /// <remarks>
    /// 这个类在所有平台上都会编译，但只有非 Windows 才会被 <see cref="Connect"/> 用到：
    /// 项目是多 RID 的同一个 TFM（没有 <c>-windows</c> 后缀），<c>#if</c> 反而会
    /// 让「Windows 构建里没有 UDS 代码」这种平台差异回到源码里。
    /// </remarks>
    private sealed class SocketConnection : KestrelConnection
    {
        private readonly Socket _socket;
        private int _closed;

        public SocketConnection(Socket socket) => _socket = socket;

        public override bool TryWriteAll(ReadOnlySpan<byte> data, out string? error)
        {
            error = null;
            try
            {
                int written = 0;
                while (written < data.Length)
                {
                    int chunk = _socket.Send(data[written..]);
                    if (chunk <= 0)
                    {
                        error = "short write to the Kestrel endpoint";
                        return false;
                    }

                    written += chunk;
                }

                return true;
            }
            catch (ObjectDisposedException)
            {
                error = "the connection to the Kestrel endpoint was closed";
                return false;
            }
            catch (SocketException exception)
            {
                error = "write() failed: " + exception.Message;
                return false;
            }
        }

        public override bool TryRead(Span<byte> destination, out int bytesRead, out string? error)
        {
            bytesRead = 0;
            error = null;
            try
            {
                bytesRead = _socket.Receive(destination);
                return true;
            }
            catch (ObjectDisposedException)
            {
                error = "the connection to the Kestrel endpoint was closed";
                return false;
            }
            catch (SocketException exception)
            {
                error = "read() failed: " + exception.Message;
                return false;
            }
        }

        public override void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            _socket.Dispose();
        }
    }
}

/// <summary>
/// 一次「发请求 -&gt; 收响应」的会话（对应旧 <c>obsharp::KestrelSession</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> 返回时响应头已经就绪，之后用 <see cref="ReadBody"/> 增量取 body。
/// 整个过程<b>同步阻塞</b>：调用方是 scheme handler 起的工作任务，阻塞在那里没关系，
/// 反倒是不阻塞会把「长度 / 分块 / 读到关闭」三种状态机拆得到处都是。
/// </para>
/// <para>
/// 连接在 <see cref="Close"/> / <see cref="Dispose"/> 时关闭。<see cref="Close"/> 可以从
/// 别的线程调（<c>Cancel</c> 就是这么做的）：阻塞中的读会立刻带着异常返回。
/// </para>
/// </remarks>
internal sealed class KestrelSession : IDisposable
{
    private const int ReadChunkSize = 16 * 1024;

    /// <summary>
    /// 已消费的前缀攒到这么多才压缩一次，避免长响应里每读一小段就搬一次内存
    /// （旧 <c>Shrink</c> 的语义）。
    /// </summary>
    private const int ShrinkThreshold = 64 * 1024;

    private readonly KestrelConnection _connection;

    /// <summary>
    /// 从连接读进来的原始字节：<c>[_rawPos, _rawLength)</c> 是还没被消费的部分。
    /// 相当于旧代码的 <c>raw_</c> + <c>rawPos_</c>，只是用定长数组 + 显式长度，
    /// 免掉 <c>std::string::append</c> 每次可能 realloc 的开销。
    /// </summary>
    private byte[] _raw = new byte[ReadChunkSize];

    private int _rawLength;
    private int _rawPos;

    private readonly byte[] _scratch = new byte[ReadChunkSize];

    private HttpFraming _framing;
    private long _remaining;         // Length / Chunked 模式下剩余未读字节
    private bool _chunkDone;         // Chunked 模式下已经读到 0 号分块
    private bool _pendingChunkCrlf;  // 一块刚读完，下一次先消费它的尾随 CRLF
    private bool _eof;

    private KestrelSession(KestrelConnection connection)
        => _connection = connection;

    /// <summary>
    /// 建连接 + 发请求 + 读响应头。<b>同步阻塞</b>；失败返回 <see langword="null"/> 并写
    /// <paramref name="error"/>（跟旧 <c>KestrelSession::Start</c> 一样，错误串里带上下文）。
    /// </summary>
    public static KestrelSession? Start(Endpoint endpoint, HttpRequest request, out HttpResponseHead? head,
        out string? error)
    {
        head = null;
        error = null;

        KestrelConnection? connection = KestrelConnection.Connect(endpoint, out error);
        if (connection is null)
            return null;

        byte[] payload = BuildRequestBytes(request);
        if (!connection.TryWriteAll(payload, out error))
        {
            error = "failed to send the request to the Kestrel endpoint: " + error;
            connection.Close();
            return null;
        }

        HttpResponseHead responseHead = new();
        KestrelSession session = new(connection);

        try
        {
            // 读状态行 + 响应头。
            if (!session.TryReadLine(out string line, out error))
                return null;

            // 状态行形如 "HTTP/1.1 200 OK"：第一个空格和第二个空格之间是状态码，
            // 之后是状态描述。没有第二个空格（"HTTP/1.1 200"）时状态描述留空。
            {
                int firstSpace = line.IndexOf(' ');
                if (firstSpace < 0)
                {
                    error = "malformed HTTP status line: " + line;
                    return null;
                }

                int secondSpace = line.IndexOf(' ', firstSpace + 1);
                string codeText = secondSpace < 0
                    ? line[(firstSpace + 1)..]
                    : line[(firstSpace + 1)..secondSpace];

                // 旧代码用 atoi：解析不出来就是 0，不当错误（下面按 0 走 UntilClose 分支）。
                responseHead.Status = int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int status) ? status : 0;

                if (secondSpace >= 0)
                    responseHead.StatusText = line[(secondSpace + 1)..];
            }

            for (; ; )
            {
                if (!session.TryReadLine(out line, out error))
                    return null;

                if (line.Length == 0)
                    break;

                // 没有冒号的行按坏头忽略（跟旧代码一致：继续读下一行，不报错）。
                int colon = line.IndexOf(':');
                if (colon < 0)
                    continue;

                responseHead.Headers.Add(new KeyValuePair<string, string>(
                    line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }

            // 决定 body 的读法。顺序不能换：Transfer-Encoding 优先于 Content-Length
            // （两者同时出现时前者才是权威），而没有这俩头时先看「本来就不该有 body」
            // 的状态码，剩下的才按「读到关闭」处理。
            string transferEncoding = (responseHead.HeaderValue("Transfer-Encoding") ?? string.Empty).ToLowerInvariant();
            if (transferEncoding.Contains("chunked", StringComparison.Ordinal))
            {
                responseHead.Framing = HttpFraming.Chunked;
            }
            else if (responseHead.HasHeader("Content-Length"))
            {
                responseHead.Framing = HttpFraming.Length;

                // 跟旧代码的 strtoll 一致：解析不出来就是 0（不是错误——header 已经
                // 声明了长度，我们能做的是把它当 0 长度，而不是让整条响应失败）。
                responseHead.ContentLength = long.TryParse(responseHead.HeaderValue("Content-Length"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out long contentLength)
                    ? contentLength
                    : 0;
            }
            else if (responseHead.Status is 204 or 304 || (responseHead.Status >= 100 && responseHead.Status < 200))
            {
                responseHead.Framing = HttpFraming.None;
            }
            else
            {
                responseHead.Framing = HttpFraming.UntilClose;
            }

            session._framing = responseHead.Framing;
            session._remaining = responseHead.ContentLength;
            head = responseHead;
            return session;
        }
        catch
        {
            session.Close();
            throw;
        }
        finally
        {
            // 走到失败分支时 head 保持 null，调用方据此知道「会话没建起来」。
            if (head is null)
                connection.Close();
        }
    }

    /// <summary>
    /// 读一段 body。
    /// </summary>
    /// <returns>
    /// <c>&gt; 0</c>：读到了数据；<c>0</c>：body 结束；<c>&lt; 0</c>：出错
    /// （<paramref name="error"/> 里带原因）。
    /// </returns>
    /// <remarks>
    /// 三种 framing 的读法差异是旧代码的核心，注释里的两个 bug 修复必须保留——见
    /// <see cref="HttpFraming.Chunked"/> 分支。
    /// </remarks>
    public int ReadBody(Span<byte> destination, out string? error)
    {
        error = null;

        int length = destination.Length;
        if (length <= 0)
            return 0;

        switch (_framing)
        {
            case HttpFraming.None:
                // 1xx / 204 / 304：本来就没有 body，直接当结束。
                return 0;

            case HttpFraming.Length:
            {
                // 阻塞到读出数据、body 结束、或出错（调用方是工作线程，阻塞没关系）。
                for (; ; )
                {
                    if (_remaining <= 0)
                        return 0;

                    // 一次最多只取 remaining 字节：Kestrel 可能在同一个连接上接着发
                    // 下一段东西，多取就会读过头。
                    int want = (int)Math.Min(_remaining, length);
                    int got = Take(destination, want);
                    if (got > 0)
                    {
                        _remaining -= got;
                        return got;
                    }

                    if (_eof)
                    {
                        // 头里写了长度却提前断开：这是响应被截断，必须报错，
                        // 否则 CEF 会把半截 HTML 当完整文档渲染。
                        error = "the Kestrel endpoint closed the connection before the whole body was read";
                        return -1;
                    }

                    if (!Fill(out error))
                        return -1;
                }
            }

            case HttpFraming.UntilClose:
            {
                for (; ; )
                {
                    int got = Take(destination, length);
                    if (got > 0)
                        return got;

                    if (_eof)
                        return 0; // 这个 framing 下「对端关闭」就是正常的结束

                    if (!Fill(out error))
                        return -1;
                }
            }

            case HttpFraming.Chunked:
            {
                if (_chunkDone)
                    return 0;

                for (; ; )
                {
                    // 还有分块数据没读完：先把它搬出去。
                    //
                    // 注意顺序：Take() 只能在 _remaining > 0（已经读过分块长度行、知道
                    // 本块还剩多少字节）时调用。否则缓冲区里的 "1464\r\n<html...>"
                    // 会被原样当正文吐出去——页面开头多出来的一串数字就是这个
                    // （旧代码实际踩过并修掉的第一个 bug）。
                    if (_remaining > 0)
                    {
                        // 一次最多只取「本块还剩的字节数」：Take() 不知道分块边界，
                        // 让它随便读会把下一块的 "26\r\n" 也搬进正文。
                        int want = (int)Math.Min(_remaining, length);
                        int got = Take(destination, want);
                        if (got > 0)
                        {
                            _remaining -= got;
                            if (_remaining == 0)
                                _pendingChunkCrlf = true; // 这一块读完了：下次先吃掉尾随 CRLF

                            return got;
                        }

                        if (_eof)
                        {
                            error = "the Kestrel endpoint closed the connection mid-chunk";
                            return -1;
                        }

                        if (!Fill(out error))
                            return -1;

                        continue;
                    }

                    // 一块刚读完：跳过块数据后面的 CRLF。
                    //
                    // HTTP 分块的格式是 "<size>CRLF<data>CRLF..."，那个尾随 CRLF 不属于
                    // 正文。不显式吃掉它的话，下一次读长度行会读到一个空行（现在的
                    // continue 能跳过），但如果它恰好和下一块的数据一起被缓冲，就可能
                    // 混进正文——实测尾部多出的 "\r\n" 就是这个（旧代码修掉的第二个 bug）。
                    if (_pendingChunkCrlf)
                    {
                        if (!SkipChunkTrailingCrlf(out error))
                            return -1;

                        continue;
                    }

                    if (_eof)
                    {
                        error = "the Kestrel endpoint closed the connection mid-chunk";
                        return -1;
                    }

                    // 读分块长度行（可能带 ";" 扩展）。
                    if (!TryReadLine(out string line, out error))
                        return -1;

                    // 扩展（";name=value"）跟长度无关，取分号之前再 Trim。
                    int semicolon = line.IndexOf(';');
                    string sizeText = (semicolon < 0 ? line : line[..semicolon]).Trim();

                    // 空行（比如尾随 CRLF 被当成了一行）不算错：跳过继续读下一行。
                    if (sizeText.Length == 0)
                        continue;

                    if (!TryParseChunkSize(sizeText, out long size))
                    {
                        error = "malformed chunk size: " + sizeText;
                        return -1;
                    }

                    if (size == 0)
                    {
                        // 0 号分块之后是 trailer，读到空行为止。
                        for (; ; )
                        {
                            if (!TryReadLine(out string trailer, out error))
                                return -1;

                            if (trailer.Length == 0)
                                break;
                        }

                        _chunkDone = true;
                        return 0;
                    }

                    // 记下本块还剩多少「真数据」，下一轮循环直接 Take()。
                    // 读完这一块之后还要吃掉它尾随的 CRLF（见 _pendingChunkCrlf）。
                    _remaining = size;
                }
            }

            default:
                return 0;
        }
    }

    /// <summary>
    /// 关闭连接（可以重复调用；可以从别的线程调，用来打断阻塞中的读）。
    /// </summary>
    public void Close() => _connection.Close();

    /// <inheritdoc />
    public void Dispose() => Close();

    /// <summary>
    /// 按旧 <c>KestrelSession::Start</c> 的规则组请求报文。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Host / Connection / Accept-Encoding / Content-Length 由我们自己写，所以原请求里的
    /// 同名头必须滤掉：留着会出现两份（HTTP 语义上冲突），而 <c>Accept-Encoding</c>
    /// 带过去更麻烦——Kestrel 可能压缩，我们却没有解压。
    /// </para>
    /// <para>
    /// <c>Connection: close</c> 是刻意的：每个请求一条连接，读完就关，这样
    /// 「读到关闭」这种 framing 才能工作，也不用处理连接复用带来的状态残留。
    /// </para>
    /// </remarks>
    private static byte[] BuildRequestBytes(HttpRequest request)
    {
        using var stream = new MemoryStream(512);

        void Write(string text) => stream.Write(Encoding.UTF8.GetBytes(text));

        Write(string.IsNullOrEmpty(request.Method) ? "GET" : request.Method);
        Write(" ");
        Write(string.IsNullOrEmpty(request.Target) ? "/" : request.Target);
        Write(" HTTP/1.1\r\nHost: ");
        Write(string.IsNullOrEmpty(request.Host) ? "obsharp" : request.Host);
        Write("\r\nConnection: close\r\nAccept-Encoding: identity\r\n");

        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            if (IsManagedHeader(header.Key))
                continue;

            Write(header.Key);
            Write(": ");
            Write(header.Value);
            Write("\r\n");
        }

        // POST / PUT / PATCH 即使 body 为空也要带 Content-Length: 0：
        // Kestrel 对这几个方法需要显式长度，否则会一直等 body（表现为请求挂住）。
        if (request.Body.Length > 0 || request.Method is "POST" or "PUT" or "PATCH")
        {
            Write("Content-Length: ");
            Write(request.Body.Length.ToString(CultureInfo.InvariantCulture));
            Write("\r\n");
        }

        Write("\r\n");
        stream.Write(request.Body);

        return stream.ToArray();
    }

    /// <summary>
    /// 我们自己设置的、不该从原请求里带过去的头。
    /// </summary>
    private static bool IsManagedHeader(string name)
        => string.Equals(name, "host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "connection", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "accept-encoding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "content-length", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从连接往内部缓冲追加数据。返回 <see langword="false"/> 表示出错。
    /// </summary>
    /// <remarks>
    /// 读回 0 字节 = 对端关闭，把 <see cref="_eof"/> 立起来后返回成功：
    /// 由调用方决定「这是正常结束还是截断」。
    /// </remarks>
    private bool Fill(out string? error)
    {
        error = null;

        if (!_connection.TryRead(_scratch, out int bytesRead, out error))
            return false;

        if (bytesRead == 0)
        {
            _eof = true;
            return true;
        }

        Append(_scratch.AsSpan(0, bytesRead));
        return true;
    }

    /// <summary>
    /// 从缓冲里读一行（不含 CRLF）。缓冲不足时继续 <see cref="Fill"/>。
    /// </summary>
    private bool TryReadLine(out string line, out string? error)
    {
        line = string.Empty;
        error = null;

        for (; ; )
        {
            int position = Array.IndexOf(_raw, (byte)'\n', _rawPos, _rawLength - _rawPos);
            if (position >= 0)
            {
                int end = position;
                if (end > _rawPos && _raw[end - 1] == (byte)'\r')
                    --end;

                line = Encoding.UTF8.GetString(_raw, _rawPos, end - _rawPos);
                _rawPos = position + 1;
                Shrink();
                return true;
            }

            if (_eof)
            {
                // 没有换行就结束了：把剩下的当最后一行（对端可能不发最后一个 CRLF）。
                if (_rawPos < _rawLength)
                {
                    line = Encoding.UTF8.GetString(_raw, _rawPos, _rawLength - _rawPos);
                    _rawPos = _rawLength;
                    Shrink();
                    return true;
                }

                error = "unexpected end of response";
                return false;
            }

            if (!Fill(out error))
                return false;
        }
    }

    /// <summary>
    /// 跳过块数据后面的 CRLF（不属于正文）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分块格式是 <c>"&lt;size&gt;CRLF&lt;data&gt;CRLF"</c>，那个尾随 CRLF 是协议开销。
    /// 不显式消费它的话，它会被当成下一个「分块长度行」的开头——而读长度行遇到空行会
    /// <c>continue</c>，表面上看没事；但如果它和下一块数据挤在同一批数据里，
    /// 就会漏进正文（实测尾部多出的 <c>\r\n</c> 就是）。
    /// </para>
    /// <para>
    /// 宽容处理：有的实现只在块之间发 LF，也接受（少认个字节比整个响应失败好）。
    /// </para>
    /// </remarks>
    private bool SkipChunkTrailingCrlf(out string? error)
    {
        error = null;

        for (; ; )
        {
            // 缓冲区里有字节就消费掉 CRLF。
            if (_rawPos < _rawLength)
            {
                if (_raw[_rawPos] == (byte)'\r' && _rawPos + 1 < _rawLength && _raw[_rawPos + 1] == (byte)'\n')
                {
                    _rawPos += 2;
                    Shrink();
                    _pendingChunkCrlf = false;
                    return true;
                }

                // 只到 '\r'、'\n' 还没来：等更多数据（不能把半个 CRLF 当正文）。
                if (_raw[_rawPos] == (byte)'\r' && _rawPos + 1 == _rawLength)
                {
                    if (_eof)
                    {
                        error = "unexpected end of response inside chunk trailer";
                        return false;
                    }

                    if (!Fill(out error))
                        return false;

                    continue;
                }

                if (_raw[_rawPos] == (byte)'\n')
                {
                    ++_rawPos;
                    Shrink();
                    _pendingChunkCrlf = false;
                    return true;
                }

                error = "malformed chunk terminator";
                return false;
            }

            if (_eof)
            {
                error = "unexpected end of response inside chunk trailer";
                return false;
            }

            if (!Fill(out error))
                return false;
        }
    }

    /// <summary>
    /// 把缓冲里已有的数据搬到 <paramref name="destination"/>（最多 <paramref name="length"/> 字节）。
    /// </summary>
    private int Take(Span<byte> destination, int length)
    {
        int available = _rawLength - _rawPos;
        if (available == 0)
            return 0;

        int count = Math.Min(available, length);
        _raw.AsSpan(_rawPos, count).CopyTo(destination);
        _rawPos += count;
        Shrink();
        return count;
    }

    /// <summary>
    /// 压缩已消费的前缀（旧 <c>Shrink</c> 的语义）。
    /// </summary>
    private void Shrink()
    {
        if (_rawPos <= 0 || (_rawPos < _rawLength && _rawPos <= ShrinkThreshold))
            return;

        int remaining = _rawLength - _rawPos;
        if (remaining > 0)
            Buffer.BlockCopy(_raw, _rawPos, _raw, 0, remaining);

        _rawLength = remaining;
        _rawPos = 0;
    }

    /// <summary>
    /// 往缓冲尾部追加数据，必要时压缩前缀 / 扩容。
    /// </summary>
    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        if (_rawLength + data.Length > _raw.Length && _rawPos > 0)
        {
            // 空间不够时无条件把已消费的前缀搬掉：旧代码靠 std::string 自己 realloc，
            // 这里显式压缩，省掉一次扩容（数据内容不变，语义等价）。
            int remaining = _rawLength - _rawPos;
            if (remaining > 0)
                Buffer.BlockCopy(_raw, _rawPos, _raw, 0, remaining);

            _rawLength = remaining;
            _rawPos = 0;
        }

        if (_rawLength + data.Length > _raw.Length)
            Array.Resize(ref _raw, Math.Max(_raw.Length * 2, _rawLength + data.Length));

        data.CopyTo(_raw.AsSpan(_rawLength));
        _rawLength += data.Length;
    }

    /// <summary>
    /// 解析分块长度（十六进制）。
    /// </summary>
    /// <remarks>
    /// 刻意模仿旧代码 <c>strtoll(line, &amp;end, 16)</c> 的宽容度：允许前导空白、正负号、
    /// <c>0x</c> 前缀，并且<b>忽略</b>第一个非法字符之后的内容（旧代码只判
    /// <c>end == line.c_str() || size &lt; 0</c>，没要求整行都消费完）。
    /// 这里如果比旧代码严格，本来能跑通的响应就会变成硬错误。
    /// </remarks>
    private static bool TryParseChunkSize(string text, out long size)
    {
        size = 0;

        int index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        bool negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            index++;
        }

        if (index + 1 < text.Length && text[index] == '0' && (text[index + 1] == 'x' || text[index + 1] == 'X'))
            index += 2;

        int start = index;
        long value = 0;

        for (; index < text.Length; index++)
        {
            int digit = HexDigitValue(text[index]);
            if (digit < 0)
                break;

            // strtoll 溢出时返回 LLONG_MAX 并置 ERANGE；这里保持同样的结果，
            // 因为下面只会拿它当「一大段数据」，不会因为溢出而拒绝响应。
            if (value > (long.MaxValue - digit) / 16)
                value = long.MaxValue;
            else
                value = value * 16 + digit;
        }

        // 一个十六进制数字都没解析出来 == 旧代码的 end == line.c_str()。
        if (index == start)
            return false;

        size = negative ? -value : value;
        return size >= 0;
    }

    /// <summary>
    /// 单个十六进制数字的值；不是十六进制字符时返回 -1。
    /// </summary>
    private static int HexDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

/// <summary>
/// 端点探测：连上即关。
/// </summary>
/// <remarks>
/// 对应旧 <c>obsharp_browser_native_probe_endpoint</c> 里
/// <c>KestrelConnection::Connect</c> + <c>Close</c> 那两步——探测的是「连得上吗」，
/// 不发请求，所以不会打扰 Kestrel 的请求处理逻辑。
/// </remarks>
internal static class KestrelProbe
{
    /// <summary>
    /// 能不能连上 <paramref name="endpoint"/>。失败返回 <see langword="false"/> 并写
    /// <paramref name="error"/>。
    /// </summary>
    public static bool TryProbe(Endpoint endpoint, out string? error)
    {
        KestrelConnection? connection = KestrelConnection.Connect(endpoint, out error);
        if (connection is null)
            return false;

        connection.Close();
        return true;
    }
}
