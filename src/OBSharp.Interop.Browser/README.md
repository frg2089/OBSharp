# OBSharp.Interop.Browser

`OBSharp.Interop.Browser.Native`（原生库）的托管绑定，命名空间 `OBSharp.Interop.Browser`：
把 obs-browser 已经初始化好的 CEF 包成 Qt Widget，交给 OB# 插件当普通 `QWidget*` 用。ABI 版本
2 起还提供停靠窗口和工具窗口的托管对象。

只在**加载进 OBS 进程**时有意义（也就是 OB# 宿主所在的进程）：CEF、Qt6、obs-browser 都是
OBS 自己那份。另起一个进程引用它，`OBSharpNative.Initialize` 只会失败。

> 原生库怎么编、C ABI 长什么样、线程规则和已知限制，都写在
> [`../OBSharp.Interop.Browser.Native/README.md`](../OBSharp.Interop.Browser.Native/README.md) 里。

## 公开类型

| 类型 | 作用 |
| --- | --- |
| `OBSharpNative` | 库级入口：`AbiVersion` / `Initialize` / `IsAvailable` / `Version` / `LastError` / `IsUiThread` / `InvokeUi` / `ProbeEndpoint` |
| `CefBrowser` | 一个浏览器实例（`IDisposable`）：`Widget` / `Load` / `Reload` / `Zoom` / `ExecuteScript` / `PostMessage` / `Resize` / `Move` / `SetVisible` 等 |
| `CefWindow` | 一个普通顶层窗口容器（`IDisposable`，**不含浏览器**）：`Create` / `Content` / `Widget` / `Title` / `Size` / `IsVisible` / `Show` / `Hide` / `SetSize` / `CloseRequested` / `Dispose` |
| `BrowserOptions` | 建浏览器时的参数：`Url` / `Host` / `Scheme` / `Domain` / `Endpoint` / `EndpointKind` / `StartupScript` / `Transparent` / `BackgroundColor` / `ParentWidget` / `Callbacks` |
| `WindowOptions` | 建窗口时的参数：`Title` / `Width` / `Height` / `Visible` / `Topmost` / `Resizable` / `Callbacks` |
| `WindowOptions` | 建窗口时的参数：`Title` / `Width` / `Height` / `Visible` / `Topmost` / `Resizable` / `Callbacks` |
| `BrowserCallbacks` | 浏览器回调：`TitleChanged` / `UrlChanged` / `LoadEnd` / `ConsoleMessage` / `Message` |
| `WindowCallbacks` | 窗口回调：`CloseRequested` |
| `OBSharpResult` / `OBSharpNativeException` | 返回码，以及失败时抛的异常 |
| `OBSharpConsoleLevel` / `OBSharpEndpointKind` | 上面两个用到的枚举 |

`NativeMethods`、`NativeLibraryLoader`、`CallbackSink`、`WindowCallbackSink`、
`OBSharpWindowCallbacks` 这些是内部实现，不对外。

## 引用

插件引用 `OBSharp.SDK` 就顺带拿到它（跟 `OBSharp.Interop` 一样是传递引用）；宿主会带着
这个程序集和原生库，`build/OBSharp.SDK.targets` 会把它从插件自己的输出里去掉。不想要
SDK 的话直接引用本工程：

```xml
<ProjectReference Include="..\OBSharp.Interop.Browser\OBSharp.Interop.Browser.csproj" />
```

原生库（Windows 上是 `OBSharp.Interop.Browser.Native.dll`，Unix 上是
`libOBSharp.Interop.Browser.Native.so` / `.dylib`）由本工程按 `RuntimeIdentifier` 从
`out/native/<arch>/<Configuration>/` 复制到输出目录，`<arch>` 是 `x64` / `arm64`；
也可以用环境变量 `OBSHARP_NATIVE_DIR` 指到别处。没编过原生库也能编译（那几条复制项是
条件包含的），只是运行时找不到入口点。

本工程是 AOT 友好的（`IsAotCompatible`）：`OBSharp.Loader` 是 NativeAOT，用不到这份绑定时
会被裁掉。

## host

host 是「一个名字 + 一台 Kestrel」：插件看到的地址是 `obsharp://<host>/...`，原生侧把它
映射成 `https://<host>.obsharp/...` 再交给 CEF。端点由 SDK 起监听：Windows 是命名管道
`\\.\pipe\obsharp\<host>`，Unix 是 `/tmp/obsharp/<host>.socket`。

用 `https` 是因为它让页面落在**安全上下文**里（`crypto.subtle` / `serviceWorker` / `clipboard`
这些只在安全上下文开放的 API 才能用）。但**并没有加密**：`https` 是 CEF 的内置 scheme，我们给它
注册了 scheme handler factory，factory 总是返回 handler，所以请求被直接转给 IPC 端点——不进网络栈、
不做 TLS 握手。`http` 是 chromium 早就注册好的 standard scheme，看起来更"现成"，但它让页面落在
**不安全上下文**里，那些 API 全部不可用，所以默认改成 `https`。

`BrowserOptions.Host` 给了 host 之后：

- `Endpoint` 为空时按平台从 host 推导，不用自己拼管道名；
- `Url` 为空时用 `obsharp://<host>/`；
- `Url` 以 `obsharp://` 开头时，里面的 host 必须与 `Host` 一致，否则抛
  `OBSharpNativeException`（`InvalidArgument`）；
- 页面里的引导脚本多出 `window.obsharp.host` / `window.obsharp.url`。

**WebSocket 不通**：`ws` / `wss` 不经过 scheme handler（Chromium 的 WebSocket 走独立 connector，
会自己做 TLS 握手），所以 `wss://<host>.obsharp/` 连不上。需要双向实时通道时用
`window.obsharp.post()` / `CustomEvent("obsharp-message")`，或者让 Kestrel 另开真实 TCP 监听。

host 和窗口彼此独立：可以只注册 host 不开窗口，也可以开窗口指向普通地址（这时 `Host`
留空）。

## 用法

```csharp
using OBSharp.Interop.Browser;

// 1. 解析 obs-browser、开始等 CEF 就绪；幂等，可以在任意线程调。
OBSharpNative.Initialize("MyPlugin");

// 两块东西是分开的：浏览器（页面）和窗口（容器）。
// 建浏览器会等 CEF 就绪（可能阻塞几秒），建议在后台线程调。
using var browser = CefBrowser.Create(new BrowserOptions
{
    Host = "myplugin",           // → obsharp://myplugin/，端点自动推导
    Callbacks = new BrowserCallbacks
    {
        Message = json => Console.WriteLine($"page: {json}"),
    },
});

// 用法 A：交给 OBS 当停靠面板。
// OBS 会自己建 QDockWidget、挂进主窗口，**并且自动加「停靠窗口」菜单项**
//（add_dock_by_id 内部走 AddDockWidget，那条路径会 insert toggleViewAction）。
OBSharpNative.InvokeUi(() => ObsFrontend.AddDockById("myplugin-panel", "My Plugin", browser.Widget));

// 撤掉时用 RemoveDock：Widget 已经交给 OBS，不能再 Dispose 浏览器本身。
OBSharpNative.InvokeUi(() => ObsFrontend.RemoveDock("myplugin-panel"));
```

### 普通窗口

`CefWindow` 是一个**纯容器**：原生侧是 `Qt::Window`（普通标题栏、能最小化 / 最大化、
进任务栏、可以单独 Alt+Tab），**不含浏览器**。它**不是** Qt / Windows 意义上的
tool window（那对应 `Qt::Window | Qt::Tool` / `WS_EX_TOOLWINDOW`）。

```csharp
using OBSharp.Interop.Browser;

using var page = CefBrowser.Create(new BrowserOptions
{
    Host = "myplugin",
    Url = "obsharp://myplugin/inspector",
});

using var window = CefWindow.Create(new WindowOptions
{
    Title = "Inspector",
    Width = 720,
    Height = 480,
    Topmost = true,              // 置顶
});

window.Content = page.Widget;    // 把浏览器放进去，铺满
window.Show();

// 用户点关闭：窗口只是隐藏，不销毁；事件在 Qt 主线程上触发。
window.CloseRequested += (_, _) => Console.WriteLine("用户关掉了窗口");
window.Size = (900, 600);
window.Title = "Inspector 2";

page.PostMessage("""{"type":"ping"}""");
```

窗口**不拥有**内容：销毁窗口不会销毁浏览器，两者各自 `Dispose`
（先窗口后浏览器更稳妥，因为内容的父级是窗口）。
不在 OBS 的 Qt 主线程上调用时，托管层会用 `OBSharpNative.InvokeUi` 自动切过去；
`CefBrowser` / `CefWindow` 的成员都能直接调，不用自己判线程。只有
`Create` 可能长时间阻塞（等 CEF 就绪），建议放到后台线程，并 catch
`OBSharpNativeException`（进程里没有 obs-browser 时会抛，`Result` 是
`BrowserUnavailable` / `NotInitialized`）。
