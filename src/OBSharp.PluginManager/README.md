# OBSharp.PluginManager

插件管理器：启用 / 停用 / 移除 / 添加插件。它自己就是一个 OBSharp 插件
（[`PluginManagerPlugin`](PluginManagerPlugin.cs)，基类 `ObsPlugin`）——用插件的
机制管理插件，页面是 Blazor 交互式服务器渲染，宿主的插件管理 API 通过友元程序集
（`InternalsVisibleTo`）访问。

## 入口

**只有 OBS 的「工具」菜单一个入口，没有停靠面板。**

[`PluginManagerMenuItem`](PluginManagerMenuItem.cs)（`IHostedService`）启动时用生成绑定
`ObsFrontend.AddToolsMenuItem` 挂上菜单项。窗口**在点击回调里才创建**，之后的点击在
隐藏 / 显示之间切换。

不做停靠面板是刻意的：CEF 的 OSR 在 dock 关闭再重新显示后不再出帧、页面也会停，
所以窗口形态比面板形态可靠。

## 窗口与页面

[`PluginManagerWindow`](PluginManagerWindow.cs)：`Window` + 内嵌 `WebView`
（Host = `plugin-manager`），960×640。关闭时先 `SetContent(0)` 摘掉 WebView 再
`Dispose`；`Show()` 重建 WebView 再显示。

页面（[`Components/Pages/Home.razor`](Components/Pages/Home.razor)）提供四个动作：

| 动作 | 行为 |
| --- | --- |
| 添加插件 | 添加后**默认停用**，点「启用」才会加载 |
| 启用 / 停用 | 即时生效；停用自己会先断掉自己这条 SignalR 连接 |
| 移除 | 移除登记 |

列表不缓存：每次现问宿主（`OBSharpPluginManager`），因为 `plugins.ini` 可能被别的
途径改过。操作提示走单例 [`PluginManagerState`](PluginManagerState.cs)（`Changed`
事件驱动页面刷新）。

## 传输链

* 面板的 HTTP 全部从插件自己监听的命名管道 / Unix socket 走（`ObsPlugin.CreateBuilder`
  预配的 Kestrel 端点）。
* SignalR（Blazor 交互式渲染的 `/_blazor`）**限制为 SSE + 长轮询，禁用 WebSocket**：
  原生 `KestrelClient` 是「一次请求一次连接、`Connection: close`」，接不住 WebSocket
  的 `Upgrade`。实现见 `PluginManagerPlugin.RestrictBlazorHubTransports`——
  `AddInteractiveServerRenderMode()` 不暴露传输选项，只能启动前遍历端点改
  `HttpConnectionDispatcherOptions`。
* 静态资源：`MapStaticAssets` 需要显式喂清单路径（默认会去宿主目录找，插件的清单在
  插件自己旁边）。

## 工程形态

| 配置 | 值 | 原因 |
| --- | --- | --- |
| `TargetFramework` | `net10.0` | |
| `OutputType` | `Exe` | SDK 的 `_AddBlazorFrameworkStaticWebAssets` 只在 `Exe` 下才把 `blazor.web.js` 登记成静态资源；交互式渲染的页面必须有它（`Library` 的清单里没有，`<script>` 会 404） |
| `Program.cs` | 占位入口 | 只为满足「Exe 要有入口点」，打印一行提示后退出，**不会被运行** |
| `UseAppHost` | `false` | 插件是 dll，不要 apphost exe |
| `GenerateRuntimeConfigurationFiles` | `false` | 运行时配置由宿主提供 |

工程引用 `OBSharp.SDK`（插件基类）和 `OBSharp.Runtime`（`Private=false`：编译期用友元
访问 internal 的 `OBSharpPluginManager`，运行时用宿主加载好的那份），并
`Import OBSharp.SDK.targets` 把宿主提供的程序集从输出里剔掉。
