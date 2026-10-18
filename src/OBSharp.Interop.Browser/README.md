# OBSharp.Interop.Browser

`OBSharp.Interop.Browser.Native`（原生库）的托管绑定，命名空间 `OBSharp.Interop.Browser`：
把 obs-browser 已经初始化好的 CEF 包成 Qt Widget，交给 OB# 插件当普通 `QWidget*` 用。

只在**加载进 OBS 进程**时有意义（也就是 OB# 宿主所在的进程）：CEF、Qt6、obs-browser 都是
OBS 自己那份。另起一个进程引用它，`OBSharpNative.Initialize` 只会失败。

> 原生库怎么编、C ABI 长什么样、线程规则和已知限制，都写在
> [`../OBSharp.Interop.Browser.Native/README.md`](../OBSharp.Interop.Browser.Native/README.md) 里。

## 公开类型

| 类型 | 作用 |
| --- | --- |
| `OBSharpNative` | 库级入口：`Initialize` / `IsAvailable` / `Version` / `LastError` / `IsUiThread` / `InvokeUi` / `ProbeEndpoint` |
| `CefBrowser` | 一个浏览器实例（`IDisposable`）：`Widget` / `Load` / `Reload` / `Zoom` / `ExecuteScript` / `PostMessage` / `Resize` / `Move` / `SetVisible` 等 |
| `BrowserOptions` | 建浏览器时的参数：`Url` / `Scheme` / `Domain` / `Endpoint` / `EndpointKind` / `StartupScript` / `Transparent` / `BackgroundColor` / `ParentWidget` / `Callbacks` |
| `BrowserCallbacks` | 回调：`TitleChanged` / `UrlChanged` / `LoadEnd` / `ConsoleMessage` / `Message` |
| `OBSharpResult` / `OBSharpNativeException` | 返回码，以及失败时抛的异常 |
| `OBSharpConsoleLevel` / `OBSharpEndpointKind` | 上面两个用到的枚举 |

`NativeMethods`、`NativeLibraryLoader`、`CallbackSink`、`OBSharpBrowserOptions` 这些是内部
实现，不对外。

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

## 用法

```csharp
using OBSharp.Interop.Browser;

// 1. 解析 obs-browser、开始等 CEF 就绪；幂等，可以在任意线程调。
OBSharpNative.Initialize("MyPlugin");

// 2. 等 OBSharpNative.IsAvailable 变成 true（CEF 起来得花点时间）再建浏览器。
using var browser = CefBrowser.Create(new BrowserOptions
{
    Endpoint = "obsharp-myplugin",  // Kestrel 监听的命名管道名 / UDS 路径
    Url = "http://obsharp/",        // 省略时按 <scheme>://<domain>/
    Callbacks = new BrowserCallbacks
    {
        Message = json => Console.WriteLine($"page: {json}"),
    },
});

// 3. Widget 就是个 QWidget*：交给 obs_frontend_add_dock_by_id()，或者自己 setParent。
//    不要自己 delete，CefBrowser.Dispose() 会一起销毁。
nint widget = browser.Widget;
```

不在 OBS 的 Qt 主线程上调用时，托管层会用 `OBSharpNative.InvokeUi` 自动切过去；
`CefBrowser` 的成员都能直接调，不用自己判线程。
