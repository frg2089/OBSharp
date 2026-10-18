#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace obsharp {

enum class EndpointKind {
	Auto = 0,
	NamedPipe = 1,
	UnixSocket = 2,
};

// 归一化后的连接目标。
struct Endpoint {
	EndpointKind kind = EndpointKind::Auto;
	std::string address; // Windows: 命名管道名（带 \\.\pipe\ 前缀）；Unix: UDS 路径
};

// 把调用方给的 endpoint 归一到平台实际的连接目标。kind 是公开 ABI 的
// obsharp_endpoint_kind 数值，Auto 时按平台推断（Windows = 命名管道，其余 = UDS）。
bool NormalizeEndpoint(const std::string &endpoint, int kind, Endpoint &out, std::string &error);

// 一条全双工的字节连接（Windows 命名管道 / Unix socket）。
class KestrelConnection {
public:
	~KestrelConnection();

	KestrelConnection(const KestrelConnection &) = delete;
	KestrelConnection &operator=(const KestrelConnection &) = delete;

	static std::unique_ptr<KestrelConnection> Connect(const Endpoint &endpoint, std::string &error);

	// 全部写完返回 true；失败写 error。全局变量写失败按硬错误处理。
	bool WriteAll(const char *data, size_t length, std::string &error);
	// 返回实际读到的字节数；0 表示对端关闭；返回 false 表示出错。
	bool ReadSome(char *data, size_t length, size_t &bytes_read, std::string &error);

	void Close();

private:
	KestrelConnection() = default;

#if defined(_WIN32)
	void *handle_ = nullptr; // HANDLE
#else
	int fd_ = -1;
#endif
	bool open_ = false;
};

// 要转给 Kestrel 的请求（已经拆成 HTTP/1.1 需要的样子）。
struct HttpRequest {
	std::string method = "GET";
	std::string target = "/"; // path + query
	std::string host;         // Host 头
	std::vector<std::pair<std::string, std::string>> headers;
	std::string body;
};

// 响应头 + body 的传输方式。
struct HttpResponseHead {
	enum class Framing {
		None,       // 没有 body（1xx / 204 / 304）
		Length,     // Content-Length
		Chunked,    // Transfer-Encoding: chunked
		UntilClose, // 读到对端关闭
	};

	int status = 0;
	std::string status_text;
	std::vector<std::pair<std::string, std::string>> headers;
	Framing framing = Framing::None;
	int64_t content_length = 0;

	std::string HeaderValue(const std::string &name) const;
	bool HasHeader(const std::string &name) const;
};

// 一次「发请求 -> 收响应」的会话：Start() 返回时响应头已经就绪，之后用
// ReadBody() 增量取 body。连接在析构时关闭。
class KestrelSession {
public:
	~KestrelSession();

	KestrelSession(const KestrelSession &) = delete;
	KestrelSession &operator=(const KestrelSession &) = delete;

	static std::unique_ptr<KestrelSession> Start(const Endpoint &endpoint, const HttpRequest &request,
						     HttpResponseHead &head, std::string &error);

	// >0：读到了数据；0：body 结束；<0：出错（error 里带原因）。
	int ReadBody(char *data, int length, std::string &error);

	void Close();

private:
	KestrelSession() = default;

	// 从连接往 raw_ 追加数据；返回新增字节数，0 表示对端关闭。
	bool Fill(std::string &error);
	// 从 raw_ 里取一行（不含 CRLF）。缓冲不足时继续 Fill。
	bool ReadLine(std::string &line, std::string &error);
	// 把 raw_ 里已有的数据搬到 data（最多 length 字节），返回搬运字节数。
	int Take(char *data, int length);
	void Shrink();

	std::unique_ptr<KestrelConnection> connection_;
	std::string raw_;
	size_t rawPos_ = 0;

	HttpResponseHead::Framing framing_ = HttpResponseHead::Framing::None;
	int64_t remaining_ = 0; // Length / Chunked 模式下剩余未读字节
	bool chunkDone_ = false; // Chunked 模式下已经读到 0 号分块
	bool eof_ = false;
};

} // namespace obsharp
