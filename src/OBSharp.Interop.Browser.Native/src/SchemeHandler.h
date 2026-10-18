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

// 把 <scheme>://<domain>/... 上的请求通过命名管道 / UDS 转给 Kestrel。
//
// 为什么不注册一个自己的 scheme：obs-browser 的 CefApp 已经把 "http" 注册成
// standard + CORS enabled，并且用 CefRegisterSchemeHandlerFactory("http",
// "absolute", ...) 给自己留了后门。我们照做，用一个不会撞车的 host 名
// （默认 "obsharp"）挂自己的 factory，就能直接复用已有的 scheme 注册，不必
// 自己搞一个 CefApp。
//
// 注意：CefScaleHandlerFactory 是按 scheme + host 注册的，所以 MessageHandler
// 是全局的；多个浏览器 instance 会共用它（OB# 一般只会开一个）。
bool InstallSchemeHandler(const std::string &scheme, const std::string &domain, const Endpoint &endpoint,
			  MessageHandler onMessage, std::string &error);

} // namespace obsharp
