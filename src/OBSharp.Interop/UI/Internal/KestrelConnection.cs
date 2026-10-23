
using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

using PipeOptions = System.IO.Pipes.PipeOptions;

namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 一条「发一次请求、收一次响应」的 Kestrel 连接（Windows 命名管道 / Unix domain socket）。
/// </summary>
/// <remarks>
/// <para>
/// 一个实例正好跑一个 HTTP/1.1 交换（请求固定带 <c>Connection: close</c>，每请求一条连接），
/// 取代旧 <c>KestrelConnection</c>（字节通道）+ <c>KestrelSession</c>（HTTP 会话）两层。
/// 读写全走 <see cref="PipeReader"/> / <see cref="PipeWriter"/>，不再手写
/// Fill / Take / Shrink / Append 那套缓冲管理。
/// </para>
/// <para>
/// body 按 framing <b>增量</b>读（<see cref="HttpFraming.None"/> /
/// <see cref="HttpFraming.Length"/> / <see cref="HttpFraming.Chunked"/> /
/// <see cref="HttpFraming.UntilClose"/>）：CEF 的挂起读是逐段喂的，Blazor SignalR 的
/// SSE 长流更是「边到边吐」，整 body 一次性读对长流永远不会返回。
/// </para>
/// <para>
/// <see cref="Close"/> 可以从别的线程调（Cancel 就是这么做的）：阻塞中的读会立刻带着
/// 异常返回。连接本体（管道 / socket）归本对象管，<see cref="Close"/> 时销毁。
/// </para>
/// </remarks>
internal sealed class KestrelConnection : IDisposable
{
    /// <summary>
    /// 连接超时（毫秒）。本地管道正常几毫秒就绪；给这么长只是为了避免
    /// 「管道存在但没有空闲实例」时无限挂住调用方的工作线程。
    /// </summary>
    private const int ConnectTimeoutMilliseconds = 5000;

    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();

    private const int MaxHeaderSize = 64 * 1024;

    /// <summary>body 泵每次向响应缓冲灌的段大小。</summary>
    private const int BodyChunkSize = 16 * 1024;

    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;

    /// <summary>
    /// 连接本体（命名管道 / 包着 UDS 的流），归本对象管。管道包装用
    /// <c>leaveOpen: true</c>：销毁统一在 <see cref="Close"/> 做一次。
    /// </summary>
    private readonly Stream _transport;

    private int _closed;

    /// <summary>body 的读法，由 <see cref="ReadResponseHeadAsync"/> 定下来。</summary>
    private HttpFraming _framing;

    private long _remaining;         // Length / Chunked 模式下剩余未读字节
    private bool _chunkDone;         // Chunked 模式下已经读到 0 号分块
    private bool _pendingChunkCrlf;  // 一块刚读完，下一次先消费它的尾随 CRLF

    private KestrelConnection(PipeReader reader, PipeWriter writer, Stream transport)
    {
        _reader = reader;
        _writer = writer;
        _transport = transport;
    }

    // ---- 建连接 ----

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

            return Create(pipe);
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

            return Create(socket);
        }

        error = "unknown endpoint kind";
        return null;
    }

    /// <summary>
    /// 用一条已连上的命名管道建会话。<paramref name="pipe"/> 的所有权移交本对象。
    /// </summary>
    public static KestrelConnection Create(NamedPipeClientStream pipe)
    {
        return new(
            PipeReader.Create(
                pipe,
                new(leaveOpen: true)),
            PipeWriter.Create(
                pipe,
                new(leaveOpen: true)),
            new PeerClosedAsEofStream(pipe));
    }

    /// <summary>
    /// 用一条已连上的 UDS 建会话。<paramref name="socket"/> 的所有权移交本对象。
    /// </summary>
    public static KestrelConnection Create(Socket socket)
    {
        NetworkStream stream = new(socket, ownsSocket: true);

        return new(
            PipeReader.Create(
                stream,
                new(leaveOpen: true)),
            PipeWriter.Create(
                stream,
                new(leaveOpen: true)),
            new PeerClosedAsEofStream(stream));
    }

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

    // ---- 发请求 ----

    /// <summary>
    /// 将 HTTP 请求写入底层流。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 报文形态与旧 <c>KestrelSession::Start</c> 的组包规则一致：
    /// Host / Connection / Accept-Encoding / Content-Length 由我们自己写，原请求里的同名
    /// 头必须滤掉——留着会出现两份（HTTP 语义上冲突），而 <c>Accept-Encoding</c> 带过去
    /// 更麻烦：Kestrel 可能压缩，我们却没有解压。
    /// </para>
    /// <para>
    /// <c>Connection: close</c> 是刻意的：每个请求一条连接，读完就关，这样
    /// 「读到关闭」这种 framing 才能工作，也不用处理连接复用带来的状态残留。
    /// </para>
    /// <para>
    /// POST / PUT / PATCH 即使 body 为空也要带 <c>Content-Length: 0</c>：Kestrel 对这几个
    /// 方法需要显式长度，否则会一直等 body（表现为请求挂住）。
    /// </para>
    /// </remarks>
    public async ValueTask WriteRequestAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            // 请求行。头文本按 UTF-8 出去（与旧组包一致），非 ASCII 不会变成 '?'。
            WriteLine($"{request.Method} {request.Target} HTTP/1.1");

            // Host
            WriteLine($"Host: {request.Host}");

            // 由我们托管的头
            WriteLine("Connection: close");
            WriteLine("Accept-Encoding: identity");

            // 其他请求头（滤掉托管头）
            foreach (var header in request.Headers)
            {
                if (IsManagedHeader(header.Key))
                    continue;

                WriteLine($"{header.Key}: {header.Value}");
            }

            // Content-Length：有 body、或方法是 POST / PUT / PATCH 时必须带。
            if (request.Body is { Length: > 0 } || request.Method is "POST" or "PUT" or "PATCH")
                WriteLine($"Content-Length: {request.Body.Length.ToString(CultureInfo.InvariantCulture)}");

            // Header 与 Body 之间的空行
            WriteLine("");

            // Body
            if (request.Body is { Length: > 0 })
                _writer.Write(request.Body);

            // 将缓冲的数据提交给底层传输层
            FlushResult result = await _writer.FlushAsync(cancellationToken);

            if (result.IsCanceled)
                throw new OperationCanceledException(cancellationToken);

            if (result.IsCompleted)
                throw new IOException("the Kestrel endpoint closed the connection before the request was sent");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or InvalidOperationException)
        {
            throw new IOException("failed to send the request to the Kestrel endpoint: " + exception.Message, exception);
        }

        void WriteLine(string line)
        {
            _writer.Write(Encoding.UTF8.GetBytes(line));
            _writer.Write("\r\n"u8);
        }
    }

    /// <summary>
    /// 我们自己设置的、不该从原请求里带过去的头。
    /// </summary>
    private static bool IsManagedHeader(string name)
        => string.Equals(name, "host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "connection", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "accept-encoding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "content-length", StringComparison.OrdinalIgnoreCase);

    // ---- 收响应头 ----

    /// <summary>
    /// 读取 HTTP 响应消息头，包含末尾的 CRLFCRLF。
    /// </summary>
    public async ValueTask<Memory<byte>> ReadHeaderAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;

            try
            {
                var sr = new SequenceReader<byte>(buffer);

                if (sr.TryReadTo(
                    out ReadOnlySequence<byte> header,
                    HeaderDelimiter,
                    advancePastDelimiter: true))
                {
                    long length = sr.Consumed;

                    if (length > MaxHeaderSize)
                        throw new InvalidDataException("HTTP 响应头过大。");

                    byte[] bytes = buffer.Slice(0, length).ToArray();

                    consumed = buffer.GetPosition(length);
                    examined = consumed;

                    return bytes;
                }

                if (buffer.Length > MaxHeaderSize)
                    throw new InvalidDataException("HTTP 响应头过大。");

                if (result.IsCompleted)
                    throw new EndOfStreamException("HTTP 响应头不完整。");
            }
            finally
            {
                _reader.AdvanceTo(consumed, examined);
            }
        }
    }

    /// <summary>
    /// 发请求收响应：「Request -&gt; Response」。响应头就绪即返回，body 由后台泵继续灌进
    /// 响应对象（见 <see cref="HttpResponse"/> 的缓冲）。响应对象持有本连接：
    /// <see cref="HttpResponse.Cancel"/> / <see cref="HttpResponse.Dispose"/> 会关掉它，泵随之结束。
    /// </summary>
    public async Task<HttpResponse> SendAsync(HttpRequest request)
    {
        try
        {
            await WriteRequestAsync(request);

            Memory<byte> header = await ReadHeaderAsync();
            HttpResponse response = ParseResponseHead(header);

            // body 的 framing 状态机定下来（Length / Chunked / UntilClose）。
            _framing = response.Framing;
            _remaining = response.ContentLength ?? 0;
            _chunkDone = false;
            _pendingChunkCrlf = false;

            response.AttachConnection(this);
            _ = PumpBodyAsync(response);
            return response;
        }
        catch
        {
            Close();
            throw;
        }
    }

    /// <summary>
    /// body 泵：逐段读 body 灌进响应缓冲，每批通知一次 <see cref="HttpResponse.Progress"/>。
    /// 出错记进响应（<see cref="HttpResponse.Fail"/>）而不是往外抛——响应对象就是终点。
    /// </summary>
    private async Task PumpBodyAsync(HttpResponse response)
    {
        byte[] buffer = new byte[BodyChunkSize];

        try
        {
            for (; ; )
            {
                if (response.IsCancelled)
                    return;

                int read = await ReadBodyAsync(buffer);
                if (read == 0)
                {
                    response.Finish();
                    return;
                }

                response.Append(buffer.AsSpan(0, read));
                response.Progress?.Invoke();
            }
        }
        catch (Exception exception)
        {
            // 连接在别的线程被关掉（Cancel）时会走到这里：记下原因就退出，
            // 不再把它当正常数据。
            if (!response.IsCancelled)
                response.Fail(exception.Message);
        }
        finally
        {
            // 结束（正常或出错）后再唤醒一次：挂起的 Read 会发现「没数据且 finished」
            // 从而返回 -1；挂起的 Skip 会带着累计值完成。
            response.Progress?.Invoke();
        }
    }

    /// <summary>
    /// 解析状态行 + 响应头（含末尾 CRLFCRLF 的原始头）成 <see cref="HttpResponse"/>，
    /// 并按旧规则定下 body 的 framing。
    /// </summary>
    private static HttpResponse ParseResponseHead(ReadOnlyMemory<byte> header)
    {
        int status = 0;
        string statusText = "";
        var headers = new List<KeyValuePair<string, string>>();
        bool first = true;

        foreach (ReadOnlyMemory<byte> lineMemory in SplitHeaderLines(header))
        {
            string line = Encoding.UTF8.GetString(lineMemory.Span);

            // 第一行是状态行，形如 "HTTP/1.1 200 OK"：第一个空格和第二个空格之间是状态码，
            // 之后是状态描述。没有第二个空格（"HTTP/1.1 200"）时状态描述留空。
            if (first)
            {
                first = false;

                int firstSpace = line.IndexOf(' ');
                if (firstSpace < 0)
                    throw new InvalidDataException("malformed HTTP status line: " + line);

                int secondSpace = line.IndexOf(' ', firstSpace + 1);
                string codeText = secondSpace < 0
                    ? line[(firstSpace + 1)..]
                    : line[(firstSpace + 1)..secondSpace];

                // 旧代码用 atoi：解析不出来就是 0，不当错误（下面按 0 走 UntilClose 分支）。
                status = int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int parsed) ? parsed : 0;

                if (secondSpace >= 0)
                    statusText = line[(secondSpace + 1)..];

                continue;
            }

            if (line.Length == 0)
                continue;

            // 没有冒号的行按坏头忽略（跟旧代码一致：继续读下一行，不报错）。
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            headers.Add(new KeyValuePair<string, string>(
                line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        // 决定 body 的读法。顺序不能换：Transfer-Encoding 优先于 Content-Length
        // （两者同时出现时前者才是权威），而没有这俩头时先看「本来就不该有 body」
        // 的状态码，剩下的才按「读到关闭」处理。
        HttpFraming framing;
        long? contentLength = null;

        string transferEncoding = (HeaderValue(headers, "Transfer-Encoding") ?? string.Empty).ToLowerInvariant();
        if (transferEncoding.Contains("chunked", StringComparison.Ordinal))
        {
            framing = HttpFraming.Chunked;
        }
        else if (HeaderValue(headers, "Content-Length") is not null)
        {
            framing = HttpFraming.Length;

            // 跟旧代码的 strtoll 一致：解析不出来就是 0（不是错误——header 已经
            // 声明了长度，我们能做的是把它当 0 长度，而不是让整条响应失败）。
            contentLength = TryParseContentLength(header, out long parsed) ? parsed : 0;
        }
        else if (status is 204 or 304 || (status >= 100 && status < 200))
        {
            framing = HttpFraming.None;
        }
        else
        {
            framing = HttpFraming.UntilClose;
        }

        var response = new HttpResponse
        {
            Status = status,
            StatusText = statusText,
            Framing = framing,
            ContentLength = contentLength,
        };

        foreach (KeyValuePair<string, string> header2 in headers)
            response.Headers.Add(header2);

        return response;
    }

    private static string? HeaderValue(List<KeyValuePair<string, string>> headers, string name)
    {
        foreach (KeyValuePair<string, string> header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }

        return null;
    }

    /// <summary>
    /// 按行拆原始头（兼容 CRLF 与裸 LF；末尾的空行自然产出空段，调用方跳过）。
    /// </summary>
    private static IEnumerable<ReadOnlyMemory<byte>> SplitHeaderLines(ReadOnlyMemory<byte> header)
    {
        ReadOnlyMemory<byte> rest = header;
        while (!rest.IsEmpty)
        {
            int index = rest.Span.IndexOf((byte)'\n');
            if (index < 0)
            {
                yield return TrimCr(rest);
                yield break;
            }

            yield return TrimCr(rest[..index]);
            rest = rest[(index + 1)..];
        }
    }

    private static ReadOnlyMemory<byte> TrimCr(ReadOnlyMemory<byte> line)
        => line.Span.EndsWith("\r"u8) ? line[..^1] : line;

    /// <summary>
    /// 尝试从 HTTP 头中解析 Content-Length。
    /// 返回是否成功解析。
    /// </summary>
    public static bool TryParseContentLength(ReadOnlyMemory<byte> header, out long contentLength)
    {
        foreach (var lineRange in header.Span.Split("\r\n"u8))
        {
            var line = header.Span[lineRange];

            // 不区分大小写

            if (line.StartsWith("Content-Length:"u8, CaseInsensitiveComparer.Instance))
            {
                var value = line[15..].Trim((byte)' ');
                if (long.TryParse(System.Text.Encoding.ASCII.GetString(value), out contentLength))
                    return true;
            }
        }

        contentLength = 0;
        return false;
    }

    // ---- 收 body（按 framing 增量读）----

    /// <summary>
    /// 读一段 body。
    /// </summary>
    /// <returns><c>&gt; 0</c>：读到了数据；<c>0</c>：body 结束。</returns>
    /// <exception cref="EndOfStreamException">响应被截断（该有数据却提前断开）。</exception>
    /// <exception cref="InvalidDataException">分块数据格式坏了。</exception>
    /// <remarks>
    /// 三种 framing 的读法差异是旧代码的核心，注释里的两个 bug 修复必须保留——见
    /// <see cref="HttpFraming.Chunked"/> 分支。出错一律抛异常（消息与旧的
    /// <c>error</c> 字符串一致），调用方按异常收尾。
    /// </remarks>
    public async ValueTask<int> ReadBodyAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length <= 0)
            return 0;

        switch (_framing)
        {
            case HttpFraming.None:
                // 1xx / 204 / 304：本来就没有 body，直接当结束。
                return 0;

            case HttpFraming.Length:
            {
                if (_remaining <= 0)
                    return 0;

                // 一次最多只取 remaining 字节：Kestrel 可能在同一个连接上接着发
                // 下一段东西，多取就会读过头。
                int want = (int)Math.Min(_remaining, destination.Length);
                int got = await ReadAtMostAsync(destination[..want], cancellationToken);
                if (got > 0)
                {
                    _remaining -= got;
                    return got;
                }

                // 头里写了长度却提前断开：这是响应被截断，必须报错，
                // 否则 CEF 会把半截 HTML 当完整文档渲染。
                throw new EndOfStreamException(
                    "the Kestrel endpoint closed the connection before the whole body was read");
            }

            case HttpFraming.UntilClose:
            {
                int got = await ReadAtMostAsync(destination, cancellationToken);
                return got; // 这个 framing 下「对端关闭」就是正常的结束
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
                        // 一次最多只取「本块还剩的字节数」：分块边界外的
                        // "26\r\n" 不能混进正文。
                        int want = (int)Math.Min(_remaining, destination.Length);
                        int got = await ReadAtMostAsync(destination[..want], cancellationToken);
                        if (got > 0)
                        {
                            _remaining -= got;
                            if (_remaining == 0)
                                _pendingChunkCrlf = true; // 这一块读完了：下次先吃掉尾随 CRLF

                            return got;
                        }

                        throw new EndOfStreamException("the Kestrel endpoint closed the connection mid-chunk");
                    }

                    // 一块刚读完：跳过块数据后面的 CRLF。
                    //
                    // HTTP 分块的格式是 "<size>CRLF<data>CRLF..."，那个尾随 CRLF 不属于
                    // 正文。不显式吃掉它的话，下一次读长度行会读到一个空行（现在的
                    // continue 能跳过），但如果它恰好和下一块的数据一起被缓冲，就可能
                    // 混进正文——实测尾部多出的 "\r\n" 就是这个（旧代码修掉的第二个 bug）。
                    if (_pendingChunkCrlf)
                    {
                        await SkipChunkTrailingCrlfAsync(cancellationToken);
                        continue;
                    }

                    // 读分块长度行（可能带 ";" 扩展）。
                    string line = await ReadLineAsync(cancellationToken);

                    // 扩展（";name=value"）跟长度无关，取分号之前再 Trim。
                    int semicolon = line.IndexOf(';');
                    string sizeText = (semicolon < 0 ? line : line[..semicolon]).Trim();

                    // 空行（比如尾随 CRLF 被当成了一行）不算错：跳过继续读下一行。
                    if (sizeText.Length == 0)
                        continue;

                    if (!TryParseChunkSize(sizeText, out long size))
                        throw new InvalidDataException("malformed chunk size: " + sizeText);

                    if (size == 0)
                    {
                        // 0 号分块之后是 trailer，读到空行为止。
                        for (; ; )
                        {
                            string trailer = await ReadLineAsync(cancellationToken);
                            if (trailer.Length == 0)
                                break;
                        }

                        _chunkDone = true;
                        return 0;
                    }

                    // 记下本块还剩多少「真数据」，下一轮循环直接搬。
                    // 读完这一块之后还要吃掉它尾随的 CRLF（见 _pendingChunkCrlf）。
                    _remaining = size;
                }
            }

            default:
                return 0;
        }
    }

    /// <summary>
    /// 从管道里读一段（最多 <paramref name="destination"/> 的容量）。返回 0 表示对端关闭。
    /// </summary>
    private async ValueTask<int> ReadAtMostAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        for (; ; )
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length > 0)
            {
                int count = (int)Math.Min(buffer.Length, destination.Length);
                buffer.Slice(0, count).CopyTo(destination.Span);
                _reader.AdvanceTo(buffer.GetPosition(count));
                return count;
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.End);
                return 0;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// 从管道里读一行（不含 CRLF）。
    /// </summary>
    /// <remarks>
    /// 对端不发最后一个 CRLF 时，剩余字节当最后一行返回（跟旧 TryReadLine 一致）；
    /// 什么都没剩就抛 <see cref="EndOfStreamException"/>。
    /// </remarks>
    private async ValueTask<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        for (; ; )
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;

            try
            {
                var sr = new SequenceReader<byte>(buffer);
                if (sr.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n', advancePastDelimiter: true))
                {
                    long length = sr.Consumed;
                    consumed = buffer.GetPosition(length);
                    examined = consumed;
                    return DecodeLine(buffer.Slice(0, length - 1));
                }

                if (result.IsCompleted)
                {
                    consumed = buffer.End;
                    if (buffer.Length > 0)
                        return DecodeLine(buffer);

                    throw new EndOfStreamException("unexpected end of response");
                }
            }
            finally
            {
                _reader.AdvanceTo(consumed, examined);
            }
        }
    }

    private static string DecodeLine(ReadOnlySequence<byte> line)
    {
        // 行尾可能是 CRLF 也可能是裸 LF；\n 已经去掉，这里再把 \r 剪掉。
        if (line.Length > 0 && line.Slice(line.Length - 1).FirstSpan[0] == (byte)'\r')
            line = line.Slice(0, line.Length - 1);

        if (line.Length == 0)
            return string.Empty;

        return Encoding.UTF8.GetString(line);
    }

    /// <summary>
    /// 跳过块数据后面的 CRLF（不属于正文）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分块格式是 <c>"&lt;size&gt;CRLF&lt;data&gt;CRLF"</c>，那个尾随 CRLF 是协议开销。
    /// 不显式消费它的话，它会被当成下一个「分块长度行」的开头——而读长度行遇到空行会
    /// 跳过，表面上看没事；但如果它和下一块数据挤在同一批数据里，就会漏进正文
    /// （实测尾部多出的 <c>\r\n</c> 就是）。
    /// </para>
    /// <para>
    /// 宽容处理：有的实现只在块之间发 LF，也接受（少认个字节比整个响应失败好）。
    /// </para>
    /// </remarks>
    private async ValueTask SkipChunkTrailingCrlfAsync(CancellationToken cancellationToken)
    {
        for (; ; )
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;

            try
            {
                if (buffer.Length > 0)
                {
                    // 偷看前两个字节（可能跨段，用 First / Slice(1).First 逐字节看）。
                    byte b0 = buffer.FirstSpan[0];

                    if (b0 == (byte)'\r')
                    {
                        // 只到 '\r'、'\n' 还没来：等更多数据（不能把半个 CRLF 当正文）。
                        if (buffer.Length < 2)
                        {
                            if (result.IsCompleted)
                                throw new EndOfStreamException("unexpected end of response inside chunk trailer");

                            continue; // examined = buffer.End，等下一批
                        }

                        if (buffer.Slice(1).FirstSpan[0] != (byte)'\n')
                            throw new InvalidDataException("malformed chunk terminator");

                        consumed = buffer.GetPosition(2);
                        examined = consumed;
                        _pendingChunkCrlf = false;
                        return;
                    }

                    if (b0 == (byte)'\n')
                    {
                        consumed = buffer.GetPosition(1);
                        examined = consumed;
                        _pendingChunkCrlf = false;
                        return;
                    }

                    throw new InvalidDataException("malformed chunk terminator");
                }

                if (result.IsCompleted)
                    throw new EndOfStreamException("unexpected end of response inside chunk trailer");
            }
            finally
            {
                _reader.AdvanceTo(consumed, examined);
            }
        }
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

    // ---- 关连接 ----

    /// <summary>
    /// 关闭连接（可以重复调用；可以从别的线程调，用来打断阻塞中的读）。
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        // 先销毁连接本体：阻塞中的读立刻带着异常返回（Cancel 就靠这一步打断工作线程）。
        _transport.Dispose();

        // 再收管道包装。此刻读多半已经被打断，Complete 撞上收尾就吞掉——销毁语义
        // 已经由上面那步保证，这里只是把缓冲区还回去。
        try
        {
            _reader.Complete();
            _writer.Complete();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose() => Close();
}

/// <summary>
/// 流包装：把「对端关闭」类 IO 异常（<see cref="IsPeerClosed"/> 那几个 Win32 错误码）
/// 翻译成干净的 EOF（读到 0 字节），其余异常原样抛。
/// </summary>
/// <remarks>
/// 这是旧 PipeConnection / SocketConnection 的 <c>TryRead</c> 语义
/// （「读返回 0 字节 = 对端正常关闭」）落到 <see cref="PipeReader"/> 上的正确位置：
/// 管道包装层只认「读到 0 = 流结束」，异常一律当错误。放在流包装里翻一次，
/// 上面所有读取逻辑就不需要为「断开」分叉了。
/// </remarks>
file sealed class PeerClosedAsEofStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (IOException exception) when (IsPeerClosed(exception))
        {
            return 0;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (IOException exception) when (IsPeerClosed(exception))
        {
            return 0;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException exception) when (IsPeerClosed(exception))
        {
            return 0;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }

    private static bool IsPeerClosed(IOException exception) => (exception.HResult & 0xFFFF) switch
    {
        109 => true, // ERROR_BROKEN_PIPE
        232 => true, // ERROR_NO_DATA
        38 => true,  // ERROR_HANDLE_EOF
        233 => true, // ERROR_PIPE_NOT_CONNECTED
        _ => false,
    };
}

file sealed class CaseInsensitiveComparer : IEqualityComparer<byte>
{
    public static readonly CaseInsensitiveComparer Instance = new();

    public bool Equals(byte x, byte y) => char.ToLowerInvariant((char)x) == char.ToLowerInvariant((char)y);

    public int GetHashCode([DisallowNull] byte obj) => char.ToLowerInvariant((char)obj).GetHashCode();
}
