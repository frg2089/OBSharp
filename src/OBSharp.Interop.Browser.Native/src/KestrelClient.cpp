#include "KestrelClient.h"

#include "Log.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#if defined(_WIN32)
#include <windows.h>
#else
#include <errno.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#endif

namespace obsharp {
namespace {

std::string ToLower(std::string value) {
	std::transform(value.begin(), value.end(), value.begin(),
		       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
	return value;
}

std::string Trim(const std::string &value) {
	size_t begin = 0;
	while (begin < value.size() && std::isspace(static_cast<unsigned char>(value[begin])))
		++begin;

	size_t end = value.size();
	while (end > begin && std::isspace(static_cast<unsigned char>(value[end - 1])))
		--end;

	return value.substr(begin, end - begin);
}

bool EqualsIgnoreCase(const std::string &a, const std::string &b) {
	return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(), [](char x, char y) {
		       return std::tolower(static_cast<unsigned char>(x)) == std::tolower(static_cast<unsigned char>(y));
	       });
}

// 我们自己设置的、不该从原请求里带过去的头。
bool IsManagedHeader(const std::string &name) {
	return EqualsIgnoreCase(name, "host") || EqualsIgnoreCase(name, "connection") ||
	       EqualsIgnoreCase(name, "accept-encoding") || EqualsIgnoreCase(name, "content-length");
}

#if defined(_WIN32)
std::wstring Utf8ToWide(const std::string &text) {
	if (text.empty())
		return std::wstring();

	int length = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
	std::wstring wide(static_cast<size_t>(length), L'\0');
	MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), wide.data(), length);
	return wide;
}
#endif

} // namespace

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

bool NormalizeEndpoint(const std::string &endpoint, int kind, Endpoint &out, std::string &error) {
	if (endpoint.empty()) {
		error = "endpoint is empty";
		return false;
	}

	EndpointKind resolved = static_cast<EndpointKind>(kind);
	if (resolved == EndpointKind::Auto) {
#if defined(_WIN32)
		resolved = EndpointKind::NamedPipe;
#else
		resolved = EndpointKind::UnixSocket;
#endif
	}

	out.kind = resolved;

	if (resolved == EndpointKind::NamedPipe) {
#if defined(_WIN32)
		// Kestrel 的 ListenNamedPipe("foo") 实际是 \\.\pipe\foo。
		if (endpoint.rfind("\\\\", 0) == 0 || endpoint.rfind("//", 0) == 0)
			out.address = endpoint;
		else
			out.address = "\\\\.\\pipe\\" + endpoint;
		return true;
#else
		error = "named pipes are only available on Windows";
		return false;
#endif
	}

	if (resolved == EndpointKind::UnixSocket) {
#if defined(_WIN32)
		error = "unix domain sockets are not available on Windows";
		return false;
#else
		out.address = endpoint;
		return true;
#endif
	}

	error = "unknown endpoint kind";
	return false;
}

// ---------------------------------------------------------------------------
// KestrelConnection
// ---------------------------------------------------------------------------

KestrelConnection::~KestrelConnection() {
	Close();
}

std::unique_ptr<KestrelConnection> KestrelConnection::Connect(const Endpoint &endpoint, std::string &error) {
	auto connection = std::unique_ptr<KestrelConnection>(new KestrelConnection());

	if (endpoint.kind == EndpointKind::NamedPipe) {
#if defined(_WIN32)
		std::wstring path = Utf8ToWide(endpoint.address);
		HANDLE handle = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
		if (handle == INVALID_HANDLE_VALUE) {
			error = "CreateFileW(" + endpoint.address + ") failed: " + std::to_string(GetLastError());
			return nullptr;
		}

		connection->handle_ = handle;
		connection->open_ = true;
		return connection;
#else
		error = "named pipes are only available on Windows";
		return nullptr;
#endif
	}

#if defined(_WIN32)
	error = "unix domain sockets are not available on Windows";
	return nullptr;
#else
	sockaddr_un address{};
	address.sun_family = AF_UNIX;
	if (endpoint.address.size() >= sizeof(address.sun_path)) {
		error = "unix socket path is too long: " + endpoint.address;
		return nullptr;
	}

	std::memcpy(address.sun_path, endpoint.address.c_str(), endpoint.address.size() + 1);

	int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
	if (fd < 0) {
		error = std::string("socket() failed: ") + std::strerror(errno);
		return nullptr;
	}

	if (::connect(fd, reinterpret_cast<sockaddr *>(&address), sizeof(address)) != 0) {
		error = "connect(" + endpoint.address + ") failed: " + std::strerror(errno);
		::close(fd);
		return nullptr;
	}

	connection->fd_ = fd;
	connection->open_ = true;
	return connection;
#endif
}

bool KestrelConnection::WriteAll(const char *data, size_t length, std::string &error) {
	size_t written = 0;
	while (written < length) {
#if defined(_WIN32)
		DWORD chunk = 0;
		DWORD toWrite = static_cast<DWORD>(std::min<size_t>(length - written, 1u << 20));
		if (!WriteFile(reinterpret_cast<HANDLE>(handle_), data + written, toWrite, &chunk, nullptr)) {
			DWORD code = GetLastError();
			if (code == ERROR_BROKEN_PIPE || code == ERROR_NO_DATA) {
				error = "the Kestrel endpoint closed the connection while writing";
				return false;
			}

			error = "WriteFile failed: " + std::to_string(code);
			return false;
		}

		if (chunk == 0) {
			error = "short write to the Kestrel endpoint";
			return false;
		}

		written += chunk;
#else
		ssize_t result = ::write(fd_, data + written, length - written);
		if (result < 0) {
			if (errno == EINTR)
				continue;

			error = std::string("write() failed: ") + std::strerror(errno);
			return false;
		}

		if (result == 0) {
			error = "short write to the Kestrel endpoint";
			return false;
		}

		written += static_cast<size_t>(result);
#endif
	}

	return true;
}

bool KestrelConnection::ReadSome(char *data, size_t length, size_t &bytes_read, std::string &error) {
	bytes_read = 0;

#if defined(_WIN32)
	DWORD chunk = 0;
	DWORD toRead = static_cast<DWORD>(std::min<size_t>(length, 1u << 20));
	if (!ReadFile(reinterpret_cast<HANDLE>(handle_), data, toRead, &chunk, nullptr)) {
		DWORD code = GetLastError();
		if (code == ERROR_BROKEN_PIPE || code == ERROR_HANDLE_EOF || code == ERROR_PIPE_NOT_CONNECTED) {
			bytes_read = 0; // 对端关闭
			return true;
		}

		error = "ReadFile failed: " + std::to_string(code);
		return false;
	}

	bytes_read = chunk;
	return true;
#else
	for (;;) {
		ssize_t result = ::read(fd_, data, length);
		if (result < 0) {
			if (errno == EINTR)
				continue;

			error = std::string("read() failed: ") + std::strerror(errno);
			return false;
		}

		bytes_read = static_cast<size_t>(result);
		return true;
	}
#endif
}

void KestrelConnection::Close() {
	if (!open_)
		return;

#if defined(_WIN32)
	CloseHandle(reinterpret_cast<HANDLE>(handle_));
	handle_ = nullptr;
#else
	::close(fd_);
	fd_ = -1;
#endif
	open_ = false;
}

// ---------------------------------------------------------------------------
// HttpResponseHead
// ---------------------------------------------------------------------------

std::string HttpResponseHead::HeaderValue(const std::string &name) const {
	for (const auto &header : headers) {
		if (EqualsIgnoreCase(header.first, name))
			return header.second;
	}

	return std::string();
}

bool HttpResponseHead::HasHeader(const std::string &name) const {
	for (const auto &header : headers) {
		if (EqualsIgnoreCase(header.first, name))
			return true;
	}

	return false;
}

// ---------------------------------------------------------------------------
// KestrelSession
// ---------------------------------------------------------------------------

KestrelSession::~KestrelSession() {
	Close();
}

void KestrelSession::Close() {
	if (connection_)
		connection_->Close();
}

void KestrelSession::Shrink() {
	// 消费掉的前缀攒到一定程度就压缩一次，避免长连接里反复 memmove。
	if (rawPos_ > 0 && (rawPos_ >= raw_.size() || rawPos_ > 64 * 1024)) {
		raw_.erase(0, rawPos_);
		rawPos_ = 0;
	}
}

bool KestrelSession::Fill(std::string &error) {
	char buffer[16 * 1024];
	size_t bytes_read = 0;
	if (!connection_->ReadSome(buffer, sizeof(buffer), bytes_read, error))
		return false;

	if (bytes_read == 0) {
		eof_ = true;
		return true;
	}

	raw_.append(buffer, bytes_read);
	return true;
}

bool KestrelSession::ReadLine(std::string &line, std::string &error) {
	for (;;) {
		size_t position = raw_.find('\n', rawPos_);
		if (position != std::string::npos) {
			size_t end = position;
			if (end > rawPos_ && raw_[end - 1] == '\r')
				--end;

			line = raw_.substr(rawPos_, end - rawPos_);
			rawPos_ = position + 1;
			Shrink();
			return true;
		}

		if (eof_) {
			// 没有换行就结束了：把剩下的当最后一行。
			if (rawPos_ < raw_.size()) {
				line = raw_.substr(rawPos_);
				rawPos_ = raw_.size();
				Shrink();
				return true;
			}

			error = "unexpected end of response";
			return false;
		}

		if (!Fill(error))
			return false;
	}
}

int KestrelSession::Take(char *data, int length) {
	size_t available = raw_.size() - rawPos_;
	if (available == 0)
		return 0;

	size_t count = std::min<size_t>(available, static_cast<size_t>(length));
	std::memcpy(data, raw_.data() + rawPos_, count);
	rawPos_ += count;
	Shrink();
	return static_cast<int>(count);
}

std::unique_ptr<KestrelSession> KestrelSession::Start(const Endpoint &endpoint, const HttpRequest &request,
						      HttpResponseHead &head, std::string &error) {
	auto connection = KestrelConnection::Connect(endpoint, error);
	if (!connection)
		return nullptr;

	auto session = std::unique_ptr<KestrelSession>(new KestrelSession());
	session->connection_ = std::move(connection);

	// 组请求。Host / Connection / Accept-Encoding / Content-Length 我们自己写，
	// 免得出现两份或压缩了却解不开。
	std::string text;
	text.reserve(512);
	text += request.method.empty() ? "GET" : request.method;
	text += ' ';
	text += request.target.empty() ? "/" : request.target;
	text += " HTTP/1.1\r\nHost: ";
	text += request.host.empty() ? "obsharp" : request.host;
	text += "\r\nConnection: close\r\nAccept-Encoding: identity\r\n";

	for (const auto &header : request.headers) {
		if (IsManagedHeader(header.first))
			continue;

		text += header.first;
		text += ": ";
		text += header.second;
		text += "\r\n";
	}

	if (!request.body.empty() || request.method == "POST" || request.method == "PUT" ||
	    request.method == "PATCH") {
		text += "Content-Length: ";
		text += std::to_string(request.body.size());
		text += "\r\n";
	}

	text += "\r\n";
	text += request.body;

	if (!session->connection_->WriteAll(text.data(), text.size(), error)) {
		error = "failed to send the request to the Kestrel endpoint: " + error;
		return nullptr;
	}

	// 读状态行 + 响应头。
	std::string line;
	if (!session->ReadLine(line, error))
		return nullptr;

	// 状态行形如 "HTTP/1.1 200 OK"。
	{
		size_t firstSpace = line.find(' ');
		if (firstSpace == std::string::npos) {
			error = "malformed HTTP status line: " + line;
			return nullptr;
		}

		size_t secondSpace = line.find(' ', firstSpace + 1);
		std::string codeText = line.substr(firstSpace + 1, secondSpace - firstSpace - 1);
		head.status = std::atoi(codeText.c_str());
		if (secondSpace != std::string::npos)
			head.status_text = line.substr(secondSpace + 1);
	}

	for (;;) {
		if (!session->ReadLine(line, error))
			return nullptr;

		if (line.empty())
			break;

		size_t colon = line.find(':');
		if (colon == std::string::npos)
			continue;

		head.headers.emplace_back(Trim(line.substr(0, colon)), Trim(line.substr(colon + 1)));
	}

	// 决定 body 的读法。
	std::string transferEncoding = ToLower(head.HeaderValue("Transfer-Encoding"));
	if (transferEncoding.find("chunked") != std::string::npos) {
		head.framing = HttpResponseHead::Framing::Chunked;
	} else if (head.HasHeader("Content-Length")) {
		head.framing = HttpResponseHead::Framing::Length;
		head.content_length = std::strtoll(head.HeaderValue("Content-Length").c_str(), nullptr, 10);
	} else if (head.status == 204 || head.status == 304 || (head.status >= 100 && head.status < 200)) {
		head.framing = HttpResponseHead::Framing::None;
	} else {
		head.framing = HttpResponseHead::Framing::UntilClose;
	}

	session->framing_ = head.framing;
	session->remaining_ = head.content_length;
	return session;
}

int KestrelSession::ReadBody(char *data, int length, std::string &error) {
	if (length <= 0)
		return 0;

	switch (framing_) {
	case HttpResponseHead::Framing::None:
		return 0;

	case HttpResponseHead::Framing::Length: {
		// 阻塞到读出数据、body 结束、或出错（调用方是工作线程，阻塞没关系）。
		for (;;) {
			if (remaining_ <= 0)
				return 0;

			int want = static_cast<int>(std::min<int64_t>(remaining_, length));
			int got = Take(data, want);
			if (got > 0) {
				remaining_ -= got;
				return got;
			}

			if (eof_) {
				error = "the Kestrel endpoint closed the connection before the whole body was read";
				return -1;
			}

			if (!Fill(error))
				return -1;
		}
	}

	case HttpResponseHead::Framing::UntilClose: {
		for (;;) {
			int got = Take(data, length);
			if (got > 0)
				return got;

			if (eof_)
				return 0;

			if (!Fill(error))
				return -1;
		}
	}

	case HttpResponseHead::Framing::Chunked: {
		if (chunkDone_)
			return 0;

		for (;;) {
			// 先把缓冲里的数据搬出去。
			int got = Take(data, length);
			if (got > 0)
				return got;

			if (remaining_ > 0) {
				// 分块数据还没读完。
				if (!Fill(error))
					return -1;

				continue;
			}

			if (eof_) {
				error = "the Kestrel endpoint closed the connection mid-chunk";
				return -1;
			}

			// 读分块长度行（可能带 ";" 扩展）。
			std::string line;
			if (!ReadLine(line, error))
				return -1;

			line = Trim(line.substr(0, line.find(';')));
			if (line.empty())
				continue;

			char *end = nullptr;
			long long size = std::strtoll(line.c_str(), &end, 16);
			if (end == line.c_str() || size < 0) {
				error = "malformed chunk size: " + line;
				return -1;
			}

			if (size == 0) {
				// 0 号分块之后是 trailer，读到空行为止。
				for (;;) {
					std::string trailer;
					if (!ReadLine(trailer, error))
						return -1;

					if (trailer.empty())
						break;
				}

				chunkDone_ = true;
				return 0;
			}

			remaining_ = size;
		}
	}
	}

	return 0;
}

} // namespace obsharp
