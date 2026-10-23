using System.Diagnostics.CodeAnalysis;

namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 一次 HTTP 响应：响应头 + body 流。
/// </summary>
/// <remarks>
/// <para>
/// 这是 <c>HttpResponseHead</c>（响应头）和旧 <c>NativeRequestState</c>（每请求共享状态）
/// 的合体：头在构造时定死，body 由 <see cref="KestrelConnection.SendAsync"/> 起的后台泵
/// 持续灌进缓冲，CEF 的 IO 线程从缓冲往外读（<see cref="Read"/> / <see cref="TrySkip"/>）。
/// </para>
/// <para>
/// 对应旧 C++ 的 <c>Stream</c>。缓冲与状态都由 <see cref="_lock"/> 保护；
/// <c>pendingSkip*</c> 只在 CEF IO 线程上读写（后台任务只负责「发现够了就续上」）。
/// </para>
/// <para>
/// <see cref="Progress"/> 是进度通知：每批 body 就绪 / 结束时调一次，scheme handler 用它
/// 唤醒挂起的 CEF 读。挂上钩子之前漏掉的通知由挂好后的那一次补唤醒兜住（见 OnOpen）。
/// </para>
/// </remarks>
internal sealed class HttpResponse : IDisposable
{
    // ---- 响应头（构造时定死）----

    /// <summary>状态码；解析不出来时是 0。</summary>
    public required int Status { get; init; }

    /// <summary>状态描述（<c>"OK"</c> / <c>"Not Found"</c>……）；没有时是空串。</summary>
    public required string StatusText { get; init; } = "";

    /// <summary>body 的读法。</summary>
    public required HttpFraming Framing { get; init; }

    /// <summary>响应头，保持到达顺序，名字大小写保持原样。</summary>
    public List<KeyValuePair<string, string>> Headers { get; } = [];

    /// <summary><see cref="HttpFraming.Length"/> 时的 body 长度；其它 framing 为 <see langword="null"/>。</summary>
    public long? ContentLength { get; init; }

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

    /// <summary>
    /// 尝试按名字获取第一个匹配的头值（大小写不敏感）。
    /// 成功时返回 <see langword="true"/> 并通过 <paramref name="value"/> 输出值，失败时返回 <see langword="false"/> 并将 <paramref name="value"/> 置为 <see langword="null"/>。
    /// </summary>
    /// <param name="name">要查找的头名。</param>
    /// <param name="value">输出匹配的头值，如果没有匹配则为 <see langword="null"/>。</param>
    /// <returns>如果找到匹配的头则返回 <see langword="true"/>，否则返回 <see langword="false"/>。</returns>
    public bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
    {
        foreach (KeyValuePair<string, string> header in Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    // ---- body 流（后台泵写 / CEF 读）----

    /// <summary>保护下面所有字段的锁。</summary>
    private readonly Lock _lock = new();

    /// <summary>body 的来源（<see cref="Cancel"/> / <see cref="Dispose"/> 时关掉它，泵随之结束）。</summary>
    private KestrelConnection? _connection;

    /// <summary>已经缓冲、还没交给 CEF 的 body 字节。</summary>
    private byte[] _buffer = [];
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

    /// <summary>
    /// 进度通知：每批 body 就绪 / body 结束时调（由 scheme handler 挂 CEF 唤醒）。
    /// 设置一次、之后只读；挂上之前漏掉的通知由调用方补一次唤醒兜住。
    /// </summary>
    public Action? Progress { get; set; }

    /// <summary>当前错误原因（没有则为 <see langword="null"/>）。</summary>
    public string? Error
    {
        get
        {
            lock (_lock)
                return _error;
        }
    }

    /// <summary>body 是否已经结束（正常结束或出错都算）。</summary>
    public bool IsFinished
    {
        get
        {
            lock (_lock)
                return _finished;
        }
    }

    /// <summary>是否已经被取消。</summary>
    public bool IsCancelled
    {
        get
        {
            lock (_lock)
                return _cancelled;
        }
    }

    // ---- 工厂 ----

    /// <summary>
    /// 错误响应（502 / 504）：不需要连接，body 就是错误文本，构造完即完整就绪。
    /// </summary>
    public static HttpResponse FromError(int status, string message)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(message);

        return new HttpResponse
        {
            Status = status,
            StatusText = status == 504 ? "Gateway Timeout" : "Bad Gateway",
            Framing = HttpFraming.Length,
            ContentLength = body.Length,
        }.WithBody(body);
    }

    /// <summary>
    /// 直接给一个完整响应（204 No Content 那条路径）：没有连接，body 一次性就位。
    /// </summary>
    public static HttpResponse Completed(int status, string statusText, HttpFraming framing, byte[] body)
    {
        return new HttpResponse
        {
            Status = status,
            StatusText = statusText,
            Framing = framing,
            ContentLength = framing == HttpFraming.Length ? body.Length : null,
        }.WithBody(body);
    }

    /// <summary>灌入一次性就位的 body 并标记完成（工厂专用）。</summary>
    private HttpResponse WithBody(byte[] body)
    {
        lock (_lock)
        {
            _buffer = body;
            _length = body.Length;
            _offset = 0;
            _finished = true;
            _error = null;
        }

        return this;
    }

    /// <summary>
    /// 接管 body 的来源连接（由 <see cref="KestrelConnection.SendAsync"/> 调用）。
    /// </summary>
    public void AttachConnection(KestrelConnection connection)
    {
        lock (_lock)
            _connection = connection;
    }

    // ---- 泵侧（后台任务用）----

    /// <summary>追加后台读到的一段 body。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        lock (_lock)
        {
            if (_cancelled || _finished)
                return;

            if (_length + data.Length > _buffer.Length)
            {
                int grow = Math.Max(_buffer.Length * 2, _length + data.Length);
                Array.Resize(ref _buffer, Math.Max(grow, 16 * 1024));
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }
    }

    /// <summary>body 正常结束。</summary>
    public void Finish()
    {
        lock (_lock)
            _finished = true;
    }

    /// <summary>body 出错结束。</summary>
    public void Fail(string? error)
    {
        lock (_lock)
        {
            _error = error;
            _finished = true;
        }
    }

    // ---- CEF 侧（IO 线程用）----

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
    public int Read(Span<byte> destination)
    {
        lock (_lock)
        {
            // 只有「被 CEF 取消」才立刻结束。FromError / Completed 设的 _finished 不算：
            // 502/504 的正文还是要读给页面看的。
            if (_cancelled)
                return -1;

            int available = _length - _offset;
            if (available > 0)
            {
                int count = Math.Min(available, destination.Length);
                _buffer.AsSpan(_offset, count).CopyTo(destination);
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

        lock (_lock)
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
            lock (_lock)
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

        lock (_lock)
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
        lock (_lock)
            return Math.Min(_length - _offset, int.MaxValue);
    }

    /// <summary>
    /// 取消：标记 + 关连接。关连接放在锁外面——它会打断正在阻塞的读，
    /// 期间不持有锁可以避免和后台任务的 <see cref="Fail"/> 抢锁。
    /// </summary>
    public void Cancel()
    {
        KestrelConnection? connection;
        lock (_lock)
        {
            _cancelled = true;
            connection = _connection;
            _connection = null;
        }

        connection?.Close();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        KestrelConnection? connection;

        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _cancelled = true;
            connection = _connection;
            _connection = null;

            _buffer = [];
            _length = 0;
            _offset = 0;
        }

        connection?.Close();
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
