#include "SchemeHandler.h"

#include "Log.h"

#include <include/cef_browser.h>
#include <include/cef_frame.h>
#include <include/cef_parser.h>
#include <include/cef_request.h>
#include <include/cef_response.h>
#include <include/cef_task.h>
#include <include/wrapper/cef_helpers.h>

#include <algorithm>
#include <climits>
#include <condition_variable>
#include <cstring>
#include <functional>
#include <memory>
#include <mutex>
#include <thread>

namespace obsharp {
namespace {

bool EqualsIgnoreCaseAscii(const std::string &a, const char *b);

// 一次请求的共享状态：工作线程负责从 Kestrel 读，IO 线程负责交给 CEF。
//
// 缓冲与状态都由 mutex 保护；pendingRead / pendingSkip 只在 IO 线程上读写
// （工作线程只投递 ResumeTask，不直接碰它们）。
struct Stream {
	std::mutex mutex;
	std::condition_variable cv;

	bool headReady = false;
	bool finished = false;
	bool cancelled = false;
	std::string error;

	HttpResponseHead head;
	std::string buffer;
	size_t offset = 0;
	std::shared_ptr<KestrelSession> session;

	// IO 线程专用。
	CefRefPtr<CefResourceReadCallback> pendingRead;
	CefRefPtr<CefResourceSkipCallback> pendingSkip;
	int64_t pendingSkipBytes = 0;
	int64_t pendingSkipTotal = 0;

	bool HasBufferedLocked() const { return offset < buffer.size(); }

	void Append(const char *data, size_t length) {
		std::lock_guard<std::mutex> lock(mutex);
		buffer.append(data, length);
	}

	// 消费缓冲里消费掉的前缀。
	void CompactLocked() {
		if (offset > 0 && offset >= buffer.size()) {
			buffer.clear();
			offset = 0;
		} else if (offset > 64 * 1024) {
			buffer.erase(0, offset);
			offset = 0;
		}
	}
};

// 工作线程通知「有新数据 / 结束了」时，投递到 IO 线程上执行。
class ResumeTask : public CefTask {
public:
	explicit ResumeTask(std::shared_ptr<Stream> stream) : stream_(std::move(stream)) {}

	void Execute() override;
	void HandlerOnIoThread();

private:
	std::shared_ptr<Stream> stream_;
	IMPLEMENT_REFCOUNTING(ResumeTask);
};

// 把页面发来的消息转投到 UI 线程上交给调用方。
class MessageTask : public CefTask {
public:
	MessageTask(MessageHandler handler, std::string json) : handler_(std::move(handler)), json_(std::move(json)) {}

	void Execute() override {
		if (handler_)
			handler_(json_);
	}

private:
	MessageHandler handler_;
	std::string json_;
	IMPLEMENT_REFCOUNTING(MessageTask);
};

class KestrelResourceHandler : public CefResourceHandler {
public:
	KestrelResourceHandler(Endpoint endpoint, MessageHandler onMessage)
		: endpoint_(std::move(endpoint)), onMessage_(std::move(onMessage)) {}
	~KestrelResourceHandler() override;

	bool Open(CefRefPtr<CefRequest> request, bool &handleRequest, CefRefPtr<CefCallback> callback) override;
	void GetResponseHeaders(CefRefPtr<CefResponse> response, int64_t &responseLength, CefString &redirectUrl) override;
	bool Read(void *dataOut, int bytesToRead, int &bytesRead, CefRefPtr<CefResourceReadCallback> callback) override;
	bool Skip(int64_t bytesToSkip, int64_t &bytesSkipped, CefRefPtr<CefResourceSkipCallback> callback) override;
	void Cancel() override;

private:
	void StartWorker(HttpRequest request);
	void ServeError(int status, const std::string &message);

	Endpoint endpoint_;
	MessageHandler onMessage_;
	std::shared_ptr<Stream> stream_;
	std::thread worker_;
	bool workerStarted_ = false;

	IMPLEMENT_REFCOUNTING(KestrelResourceHandler);
	DISALLOW_COPY_AND_ASSIGN(KestrelResourceHandler);
};

// ---- 请求组装 -------------------------------------------------------------

HttpRequest BuildRequest(CefRefPtr<CefRequest> request) {
	HttpRequest out;
	out.method = request->GetMethod().ToString();

	std::string url = request->GetURL().ToString();

	CefURLParts parts;
	if (CefParseURL(url, parts)) {
		std::string path = CefString(&parts.path);
		std::string query = CefString(&parts.query);

		out.host = CefString(&parts.host);
		out.target = path.empty() ? "/" : path;
		if (!query.empty())
			out.target += "?" + query;
	} else {
		out.host = "obsharp";
		out.target = url;
	}

	CefRequest::HeaderMap headers;
	request->GetHeaderMap(headers);
	for (const auto &header : headers)
		out.headers.emplace_back(header.first.ToString(), header.second.ToString());

	if (CefRefPtr<CefPostData> post = request->GetPostData()) {
		CefPostData::ElementVector elements;
		post->GetElements(elements);
		for (const auto &element : elements) {
			if (element->GetType() != PDE_TYPE_BYTES)
				continue;

			size_t size = element->GetBytesCount();
			std::string bytes(size, '\0');
			if (size > 0)
				element->GetBytes(size, bytes.data());
			out.body += bytes;
		}
	}

	return out;
}

constexpr const char *kMessageTarget = "/__obsharp/message";

bool IsMessageRequest(const HttpRequest &request) {
	if (request.method != "POST")
		return false;

	std::string target = request.target;
	size_t query = target.find('?');
	if (query != std::string::npos)
		target.resize(query);

	return target == kMessageTarget;
}

// ---- 工作线程 -------------------------------------------------------------

void RunWorker(std::shared_ptr<Stream> stream, Endpoint endpoint, HttpRequest request,
	       std::function<void()> wake) {
	std::string error;
	HttpResponseHead head;

	std::unique_ptr<KestrelSession> session = KestrelSession::Start(endpoint, request, head, error);
	if (!session) {
		{
			std::lock_guard<std::mutex> lock(stream->mutex);
			stream->error = error;
			stream->head.status = 502;
			stream->head.status_text = "Bad Gateway";
			stream->head.framing = HttpResponseHead::Framing::Length;
			const std::string body = "Kestrel endpoint unavailable: " + error;
			stream->head.content_length = static_cast<int64_t>(body.size());
			stream->buffer = body;
			stream->finished = true;
			stream->headReady = true;
		}

		stream->cv.notify_all();
		wake();
		return;
	}

	{
		std::lock_guard<std::mutex> lock(stream->mutex);
		stream->session = std::shared_ptr<KestrelSession>(std::move(session));
		stream->head = head;
		stream->headReady = true;
	}

	stream->cv.notify_all();
	wake();

	char buffer[16 * 1024];
	for (;;) {
		{
			std::lock_guard<std::mutex> lock(stream->mutex);
			if (stream->cancelled)
				break;
		}

		int read = 0;
		{
			// session 可能被 Cancel() 关掉；拿一份 shared_ptr 保证生命周期。
			std::shared_ptr<KestrelSession> active;
			{
				std::lock_guard<std::mutex> lock(stream->mutex);
				active = stream->session;
			}

			if (!active)
				break;

			std::string readError;
			read = active->ReadBody(buffer, static_cast<int>(sizeof(buffer)), readError);
			if (read < 0) {
				std::lock_guard<std::mutex> lock(stream->mutex);
				stream->error = readError;
				break;
			}
		}

		if (read == 0)
			break;

		stream->Append(buffer, static_cast<size_t>(read));
		wake();
	}

	{
		std::lock_guard<std::mutex> lock(stream->mutex);
		stream->finished = true;
	}

	stream->cv.notify_all();
	wake();
}

// ---- 资源处理器 -----------------------------------------------------------

void ResumeTask::Execute() {
	HandlerOnIoThread();
}

void ResumeTask::HandlerOnIoThread() {
	// 到这儿已经在 IO 线程上了。stream_ 还在说明资源处理器还活着；直接唤醒它
	// 挂起的回调即可 —— 真正读数据是下一次 Read()。
	//
	// 跳过（Skip）需要在这里把数据真的吃掉，所以交给一个静态辅助：通过
	// pendingSkip 自己完成。由于 stream_ 是共享的，这里直接操作即可。
	std::lock_guard<std::mutex> lock(stream_->mutex);

	if (stream_->pendingSkipBytes > 0) {
		size_t available = stream_->buffer.size() - stream_->offset;
		int64_t take = static_cast<int64_t>(available);
		if (take > stream_->pendingSkipBytes)
			take = stream_->pendingSkipBytes;

		if (take > 0) {
			stream_->offset += static_cast<size_t>(take);
			stream_->pendingSkipBytes -= take;
			stream_->CompactLocked();
		}

		// 要么跳够了，要么数据已经结束（只能跳一部分，CEF 会按范围不可满足处理）。
		if (stream_->pendingSkipBytes == 0 || stream_->finished) {
			CefRefPtr<CefResourceSkipCallback> callback = stream_->pendingSkip;
			int64_t skipped = stream_->pendingSkipTotal - stream_->pendingSkipBytes;
			stream_->pendingSkip = nullptr;
			stream_->pendingSkipTotal = 0;
			stream_->pendingSkipBytes = 0;
			if (callback)
				callback->Continue(skipped);
		}

		return;
	}

	CefRefPtr<CefResourceReadCallback> callback = stream_->pendingRead;
	if (callback) {
		stream_->pendingRead = nullptr;
		size_t available = stream_->buffer.size() - stream_->offset;
		int report = available > static_cast<size_t>(INT_MAX) ? INT_MAX : static_cast<int>(available);
		callback->Continue(report);
	}
}

KestrelResourceHandler::~KestrelResourceHandler() {
	if (stream_) {
		{
			std::lock_guard<std::mutex> lock(stream_->mutex);
			stream_->cancelled = true;
		}

		if (stream_->session)
			stream_->session->Close();
	}

	if (workerStarted_ && worker_.joinable())
		worker_.join();
}

void KestrelResourceHandler::ServeError(int status, const std::string &message) {
	std::lock_guard<std::mutex> lock(stream_->mutex);
	stream_->head.status = status;
	stream_->head.status_text = status == 504 ? "Gateway Timeout" : "Bad Gateway";
	stream_->head.framing = HttpResponseHead::Framing::Length;
	stream_->head.content_length = static_cast<int64_t>(message.size());
	stream_->buffer = message;
	stream_->offset = 0;
	stream_->finished = true;
	stream_->headReady = true;
}

void KestrelResourceHandler::StartWorker(HttpRequest request) {
	std::shared_ptr<Stream> stream = stream_;
	Endpoint endpoint = endpoint_;

	workerStarted_ = true;
	worker_ = std::thread([stream, endpoint, request]() {
		RunWorker(stream, endpoint, request, [stream]() {
			// 这个 lambda 在两种线程上都会被调用（工作线程投递、Open 之前也可能）。
			CefPostTask(TID_IO, new ResumeTask(stream));
		});
	});
}

bool KestrelResourceHandler::Open(CefRefPtr<CefRequest> request, bool &handleRequest,
				  CefRefPtr<CefCallback> callback) {
	CEF_REQUIRE_IO_THREAD();

	stream_ = std::make_shared<Stream>();

	HttpRequest built = BuildRequest(request);

	// 页面 -> 原生 的消息通道：拦下 POST /__obsharp/message，不往 Kestrel 转。
	if (IsMessageRequest(built)) {
		if (onMessage_)
			CefPostTask(TID_UI, new MessageTask(onMessage_, built.body));

		{
			std::lock_guard<std::mutex> lock(stream_->mutex);
			stream_->head.status = 204;
			stream_->head.status_text = "No Content";
			stream_->head.framing = HttpResponseHead::Framing::None;
			stream_->head.content_length = 0;
			stream_->headReady = true;
			stream_->finished = true;
		}

		stream_->cv.notify_all();
		handleRequest = true;
		return true;
	}

	StartWorker(std::move(built));

	// 等响应头（本地管道，通常几毫秒）。超时就返回 504。
	bool timedOut = false;
	{
		std::unique_lock<std::mutex> lock(stream_->mutex);
		timedOut = !stream_->cv.wait_for(lock, std::chrono::seconds(30), [this]() {
			return stream_->headReady || stream_->cancelled;
		});
	}

	if (timedOut)
		ServeError(504, "Kestrel endpoint did not respond within 30s");

	// Open() 是同步完成的：|handle_request| 置真、返回 true，不用 callback。
	handleRequest = true;
	return true;
}

void KestrelResourceHandler::GetResponseHeaders(CefRefPtr<CefResponse> response, int64_t &responseLength,
						CefString &redirectUrl) {
	CEF_REQUIRE_IO_THREAD();

	HttpResponseHead head;
	{
		std::lock_guard<std::mutex> lock(stream_->mutex);
		head = stream_->head;
	}

	if (head.status == 0)
		head.status = 502;

	response->SetStatus(head.status);
	if (!head.status_text.empty())
		response->SetStatusText(head.status_text);

	CefResponse::HeaderMap map;
	for (const auto &header : head.headers) {
		if (EqualsIgnoreCaseAscii(header.first, "content-length") ||
		    EqualsIgnoreCaseAscii(header.first, "connection") ||
		    EqualsIgnoreCaseAscii(header.first, "transfer-encoding"))
			continue;

		map.insert({CefString(header.first), CefString(header.second)});
	}

	response->SetHeaderMap(map);

	if (head.framing == HttpResponseHead::Framing::Length)
		responseLength = head.content_length;
	else if (head.framing == HttpResponseHead::Framing::None)
		responseLength = 0;
	else
		responseLength = -1; // 未知长度：读到 Read() 返回 false 为止

	redirectUrl.clear();
}

bool KestrelResourceHandler::Read(void *dataOut, int bytesToRead, int &bytesRead,
				  CefRefPtr<CefResourceReadCallback> callback) {
	CEF_REQUIRE_IO_THREAD();

	bytesRead = 0;
	if (!stream_)
		return false;

	{
		std::lock_guard<std::mutex> lock(stream_->mutex);
		if (stream_->offset < stream_->buffer.size()) {
			size_t available = stream_->buffer.size() - stream_->offset;
			size_t count = bytesToRead <= 0 ? available
							: (std::min)(available, static_cast<size_t>(bytesToRead));
			std::memcpy(dataOut, stream_->buffer.data() + stream_->offset, count);
			stream_->offset += count;
			stream_->CompactLocked();
			bytesRead = static_cast<int>(count);
			return true;
		}

		if (stream_->finished)
			return false; // 结束

		// 现在没数据，等下一次 ResumeTask。
		stream_->pendingRead = callback;
	}

	return false;
}

bool KestrelResourceHandler::Skip(int64_t bytesToSkip, int64_t &bytesSkipped,
				  CefRefPtr<CefResourceSkipCallback> callback) {
	CEF_REQUIRE_IO_THREAD();

	bytesSkipped = 0;
	if (!stream_)
		return false;

	{
		std::lock_guard<std::mutex> lock(stream_->mutex);
		size_t available = stream_->buffer.size() - stream_->offset;
		int64_t take = (std::min)(static_cast<int64_t>(available), bytesToSkip);
		stream_->offset += static_cast<size_t>(take);
		stream_->CompactLocked();
		bytesSkipped = take;

		if (take >= bytesToSkip)
			return true;

		if (stream_->finished)
			return true; // 没得跳了，也算完成

		stream_->pendingSkip = callback;
		stream_->pendingSkipTotal = bytesToSkip;
		stream_->pendingSkipBytes = bytesToSkip - take;
	}

	return false;
}

void KestrelResourceHandler::Cancel() {
	if (!stream_)
		return;

	{
		std::lock_guard<std::mutex> lock(stream_->mutex);
		stream_->cancelled = true;
		if (stream_->session)
			stream_->session->Close();
	}

	stream_->cv.notify_all();
}

// ---- 工厂 -----------------------------------------------------------------

class KestrelSchemeHandlerFactory : public CefSchemeHandlerFactory {
public:
	KestrelSchemeHandlerFactory(Endpoint endpoint, MessageHandler onMessage)
		: endpoint_(std::move(endpoint)), onMessage_(std::move(onMessage)) {}

	CefRefPtr<CefResourceHandler> Create(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
					     const CefString &schemeName, CefRefPtr<CefRequest> request) override {
		return new KestrelResourceHandler(endpoint_, onMessage_);
	}

private:
	Endpoint endpoint_;
	MessageHandler onMessage_;
	IMPLEMENT_REFCOUNTING(KestrelSchemeHandlerFactory);
	DISALLOW_COPY_AND_ASSIGN(KestrelSchemeHandlerFactory);
};

bool EqualsIgnoreCaseAscii(const std::string &a, const char *b) {
	size_t length = std::strlen(b);
	if (a.size() != length)
		return false;

	for (size_t i = 0; i < length; ++i) {
		char x = a[i];
		char y = b[i];
		if (x >= 'A' && x <= 'Z')
			x = static_cast<char>(x - 'A' + 'a');
		if (y >= 'A' && y <= 'Z')
			y = static_cast<char>(y - 'A' + 'a');
		if (x != y)
			return false;
	}

	return true;
}

} // namespace

bool InstallSchemeHandler(const std::string &scheme, const std::string &domain, const Endpoint &endpoint,
			  MessageHandler onMessage, std::string &error) {
	if (scheme.empty() || domain.empty()) {
		error = "scheme and domain must not be empty";
		return false;
	}

	if (!CefRegisterSchemeHandlerFactory(CefString(scheme), CefString(domain),
					     new KestrelSchemeHandlerFactory(endpoint, std::move(onMessage)))) {
		error = "CefRegisterSchemeHandlerFactory(" + scheme + ", " + domain + ") failed";
		return false;
	}

	return true;
}

} // namespace obsharp
