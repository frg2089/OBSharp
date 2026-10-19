#pragma once

#include "KestrelClient.h"

#include <include/cef_scheme.h>

#include <functional>
#include <string>

namespace obsharp {

// 页面通过 POST <scheme>://<domain>/__obsharp/message 发来的 JSON。
//
// 注意：回调跑在 CEF 的 UI 线程（TID_UI）上。Windows / Linux 上 CEF 的 UI 线程
// 跟 Qt 主线程不是同一个，所以想碰 Qt 得自己切线程（本库用 PostToQtThread）。
using MessageHandler = std::function<void(const std::string &json)>;

// ---- host 与地址映射 -------------------------------------------------------
//
// 插件看到的规范地址是 obsharp://<host>/<path>；CEF 里实际访问的是
// https://<host>.obsharp/<path>。下面几个函数就是这两者之间的换算。
//
// 为什么是 https 而不是 http：https 是安全上下文，页面才能用 crypto.subtle /
// serviceWorker / clipboard 这些只在安全上下文开放的 API。而这里的 https 请求
// 由我们的 scheme handler 接住直接转给命名管道 / UDS，**不做 TLS、不走网络栈**，
// 所以没有加解密开销。CEF 允许给内置 scheme 注册 factory，只有 factory 返回
// nullptr 时才回落到真正的网络传输（见 cef_scheme.h 的说明）。

// OB# 的 host 名：^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$，即小写字母 / 数字 /
// 连字符，1~48 个字符，首尾不能是连字符。
bool IsValidHostName(const std::string &host);

// url 是不是以 "obsharp://" 开头（不看里面有没有 host）。
bool HasObsharpScheme(const std::string &url);

// 把规范地址 obsharp://<host>/path?query 拆成 host 与 "/path?query"。
// url 不是 obsharp:// 形式、或者里面没有 host 时返回 false（host / rest 不动）。
bool SplitObsharpUrl(const std::string &url, std::string &host, std::string &rest);

// 映射成 CEF 里实际访问的地址：<scheme>://<host>.obsharp<rest>。
// rest 为空时补 "/"，不以 '/' 开头时也补一个；scheme 为空时用 "https"。
std::string MapToCefUrl(const std::string &scheme, const std::string &host, const std::string &rest);

// host 对应的 scheme handler domain："<host>.obsharp"。
std::string HostDomain(const std::string &host);

// host 推导出来的 Kestrel 端点（还没归一化）：
//   Windows "\\.\pipe\obsharp\<host>"，Unix "/tmp/obsharp/<host>.socket"。
std::string DefaultEndpointForHost(const std::string &host);

// 把一个 obsharp://<host>/... 地址按浏览器实例的 host 映射成 CEF 形式。
//   * url 不是 obsharp:// 时原样写进 mapped，返回 true（普通网络地址）；
//   * 浏览器没有 host，或者 url 里的 host 跟它不一致 -> 返回 false + error。
bool MapObsharpUrlForBrowser(const std::string &browserHost, const std::string &url,
			     const std::string &scheme, std::string &mapped, std::string &error);

// ---- 注册 ------------------------------------------------------------------

// 把 <scheme>://<domain>/... 上的请求通过命名管道 / UDS 转给 Kestrel。
//
// 有 host 时 scheme 是 "https"、domain 是 "<host>.obsharp"：https 是 CEF / Chromium
// 的内置 scheme，**可以**给它注册 scheme handler factory —— cef_scheme.h 写明
// 「如果 scheme 是内置的，且 factory 没返回 handler，才回落到内置的 scheme handler」。
// 我们的 factory 总是返回 handler，所以请求根本不会进网络栈，也就没有 TLS 握手。
// 这样页面拿到的来源是 https（安全上下文），实际数据走的是明文 IPC。
//
// 没有 host 的老式调用仍然用调用方给的 <scheme>://<domain>（默认 https + obsharp）。
//
// 必须在 CEF 的 UI 线程上调用（CefRegisterSchemeHandlerFactory 的要求）。
//
// 同一个 (scheme, domain) 只注册一次：CEF 里这个键全局唯一，重复注册会失败，
// 所以本函数自己记账——第二次调用直接当成功，端点不一致时记警告并沿用第一次
// 注册的那个端点（factory 一旦注册就改不了端点）。
//
// 注意：MessageHandler 也是按 factory 全局唯一的；多个浏览器共用本库在
// OBSharp.Interop.Browser.Native.cpp 里的那个派发器，所以同一个页面消息不会
// 因为建了第二个浏览器就丢。
//
// 已知限制：WebSocket 不经过 scheme handler（CEF 的 WebSocket 走独立的 connector，
// 会自己去做 TLS 握手），所以 wss://<host>.obsharp/ 连不上。见 README「已知限制」。
bool InstallSchemeHandler(const std::string &scheme, const std::string &domain, const Endpoint &endpoint,
			  MessageHandler onMessage, std::string &error);

} // namespace obsharp
