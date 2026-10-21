# OBSharp.PluginManager

OB# 的插件管理器：把宿主的插件管理 API（`OBSharp.Runtime` 的 internal 成员）做成一个
Blazor 窗口，列出全部插件，支持启用 / 停用 / 移除 / 添加。

它自己也**是普通插件**，但走的是 SDK 里显式的那条路，而不是便捷写法：

```csharp
public override bool Load()
{
    ObsHost host = Context.RegisterHost("plugin-manager");
    // … 配服务 / 中间件 / 路由 …
    host.Start();

    // 「工具」菜单项：**只加菜单项**，窗口在回调里懒创建。
    host.AddToolsMenuItem("OB# 插件管理器", ToggleWindow);

    // 停靠面板：OBS 自己建 QDockWidget、挂进主窗口、并自动加「停靠窗口」菜单项。
    _ = Task.Run(() =>
    {
        ObsBrowser browser = Context.CreateBrowser(host);
        host.AddDockPanel("obsharp-plugin-manager", "OB# 插件管理器", browser);
    });

    return true;
}
```

页面地址是 `https://plugin-manager.obsharp/`——**安全上下文**（`crypto.subtle` 之类的
API 可用），但没有任何 TLS：请求由托管侧注册的 scheme handler 直接转进命名管道。
## 两个入口：停靠面板 + 工具菜单

同一个页面（同一台 Kestrel、同一个 URL）在两处出现：

- **停靠面板**：`host.AddDockPanel(id, title, browser)`，底层 `obs_frontend_add_dock_by_id()`。
  OBS 自己建 `QDockWidget`、挂进主窗口、**并自动加「停靠窗口」菜单项**。
- **工具菜单**：`host.AddToolsMenuItem(text, onClick)`，底层 `obs_frontend_add_tools_menu_item()`。

`Load()` 里<b>没有</b>任何窗口：它只起 Kestrel、注册菜单项、把停靠面板丢后台挂上去。
工具窗口是**点菜单时才创建**的（`ObsWindow` + `ObsBrowser`），关掉就两者一起销毁，
下次点再重建——插件加载时不白占一个 CEF 实例。

### 为什么关窗要销毁

关掉的原生窗口不能复用（Qt 那边资源已经回收），复用只会得到一块没有 CEF 的空壳。
所以 `CloseRequested` 里直接销毁窗口和它那块浏览器：

```csharp
window.CloseRequested += (_, _) =>
{
    _window = null;
    _ = Task.Run(() =>
    {
        window.Dispose();     // 先窗口
        browser.Dispose();    // 再内容（内容的父级是窗口）
    });
};
```

### 菜单回调的野指针问题（SDK 已处理）

`obs_frontend_add_tools_menu_item` **没有**对应的「移除菜单项」API：那一项会永远留在
OBS 的「工具」菜单里。而插件的加载上下文是**可卸载**的
（`OBSharpPluginLoadContext` 传了 `isCollectible: true`），插件停用后实例就没了。

所以回调不能是实例方法、也不能用 `private_data` 传 `this`（那会变成野指针）。
SDK 的 `ObsHost.AddToolsMenuItem` 已经处理好了：回调是 <b>SDK 里的静态方法</b>
（默认上下文，不随插件卸载），`private_data` 传递增的整数 id，按 id 查表；
插件卸载后查不到，安全地什么都不做。插件侧只需要给一个普通的 `Action`。
## 页面

**交互式服务器渲染**（SignalR 长连接），不是静态 SSR。

- `/`：插件列表 + 添加框；每行有启用 / 停用 / 移除，点完就地刷新，不整页回发。
- `/error`、`/not-found`：状态码页。

「停用自己」不给按钮：停用之后窗口就没了，这次会话里也要不回来。

### 为什么必须禁掉 WebSocket

面板的 HTTP 全部从命名管道 / UDS 过，而托管侧的 `KestrelClient` 是「一次请求一次连接、
`Connection: close`」，**接不住 WebSocket 的 `Upgrade`**。如果放任 SignalR 去协商，
它会优先选 WebSocket 然后连不上，页面一直卡在「正在连接」。

所以 `Load()` 里把传输限制成 SSE + 长轮询：

```csharp
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()     // 交互式组件要求有这套端点，缺了首页直接 500
   .WithStaticAssets(assetManifest);

RestrictBlazorHubTransports(app);       // 启动前改写 /_blazor 端点的 Transports
```

`RestrictBlazorHubTransports` 之所以要绕一圈：`AddInteractiveServerRenderMode()` 自己映射
`/_blazor`，不暴露 `HttpConnectionDispatcherOptions`，而 `HubOptions` 上**没有** `Transports`
（那是 per-endpoint 的选项）。所以在 `app.Start()` 之前遍历端点，把那个 options 实例的
`Transports` 改成 `ServerSentEvents | LongPolling`。

实测的 negotiate 结果（限制生效时只返回这两项）：

```json
{"availableTransports":[
  {"transport":"ServerSentEvents","transferFormats":["Text"]},
  {"transport":"LongPolling","transferFormats":["Text","Binary"]}]}
```

SSE 在命名管道上是**真流式**的（实测 `data:` 分批到达，不是攒完再发），所以交互可用。
长轮询作为后备。

### 为什么是 `OutputType=Exe` + `Program.cs`

交互式渲染的页面必须加载 `_framework/blazor.web.js`。SDK 里
`_AddBlazorFrameworkStaticWebAssets` 的硬条件是 `OutputType == Exe`——`Library` 的静态资源
清单里根本没有它，挂上去只会 404。

所以本工程把 `OutputType` 设成 `Exe`（于是 SDK 会自动把 `_framework/blazor.web.js`、
`blazor.server.js` 登记进静态资源），同时：

- `Program.cs` 只是一个占位入口点（`Console.WriteLine + return -1`），满足「Exe 要有 `Main`」，
  永远不会被运行；
- `UseAppHost=false` —— 不产出 apphost exe，插件仍然是 dll；
- `GenerateRuntimeConfigurationFiles=false` —— 运行时配置由宿主提供。

结果：`publish` 出来是 `wwwroot/_framework/blazor.web.js` + `OBSharp.PluginManager.dll`，
没有 exe，宿主照常当插件加载。

## 插件管理 API

`OBSharpPluginManager`（`OBSharp.Runtime` 的 internal 成员）负责读 `plugins.ini`、按需加载 /
卸载插件、把启停状态写回文件。本工程是 `OBSharp.Runtime` 的**友元程序集**
（`InternalsVisibleTo`），所以能引用它；普通插件拿不到。

`ProjectReference` 上的 `Private="false"` 很关键：运行时用的必须是宿主已经加载好的那一份
`OBSharp.Runtime`，不能把第二份拷进插件目录。

## 部署形态必须是 publish

`MapStaticAssets()` 的清单默认去 `AppContext.BaseDirectory`（那是宿主目录）找，所以
`PluginManagerPlugin.Load()` 会把插件自己旁边那份 `*.staticwebassets.endpoints.json` 的
**绝对路径**显式喂进去，`MapRazorComponents<App>()` 后面也跟一句 `WithStaticAssets(同一个路径)`。

但只喂清单还不够：`StaticAssetDevelopmentRuntimeHandler` 在 **Development** 环境下会去
`WebRootPath` 找源文件，而插件的数据目录里没有 `wwwroot`。所以插件必须按 **publish** 形态部署
（`dotnet publish` 会把 `wwwroot/`（含 `/wwwroot/_framework/`）一起摆出来），`dotnet build` 的
输出目录里没有 `wwwroot`：

```text
<数据目录>/OBSharp.PluginManager/
  OBSharp.PluginManager.dll
  OBSharp.PluginManager.deps.json
  OBSharp.PluginManager.staticwebassets.endpoints.json
  wwwroot/app.css  favicon.png  OBSharp.PluginManager.styles.css  …
  appsettings.json
```

`scripts/Build-DevDist.ps1` 已经按 publish 形态摆好，直接用它就不用操心这件事。

## 构建

```sh
dotnet build src/OBSharp.PluginManager           # 两套 TFM 都在 Directory.Build.props 里？不：本工程只有 net10.0
dotnet build OBSharp.slnx                        # 整个解决方案
dotnet publish src/OBSharp.PluginManager -c Release -o <out>
```

本工程把 TFM 写死成 `net10.0`（插件管理器的 Blazor 依赖那边最稳），跟宿主的 `net10.0/net11.0`
多目标不联动；`Build-DevDist.ps1` 里也按 publish 目录名认它，不再抄一遍 TFM。

## 目录

| 文件 | 内容 |
| --- | --- |
| `PluginManagerPlugin.cs` | 插件入口：注册 host、起 Kestrel、加工具菜单项、挂停靠面板 |
| `Components/App.razor` | HTML 外壳 |
| `Components/Routes.razor` | 路由（含 NotFoundPage） |
| `Components/Layout/MainLayout.razor` | 布局 |
| `Components/Pages/Home.razor` | 插件列表 + 添加 / 启用 / 停用 / 移除 |
| `Components/Pages/Error.razor`、`NotFound.razor` | 状态码页 |
| `wwwroot/app.css` | 样式（跟着系统深浅色走，不引前端框架） |


