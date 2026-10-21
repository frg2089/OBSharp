# OBSharp.SDK

写 OB# 插件要引用的就是这个项目：`ObsPlugin` 基类、宿主给插件的环境信息（`ObsPluginContext`）、
host / 浏览器 / 窗口（`ObsHost`、`ObsBrowser`、`ObsWindow`），加上传递引用的 `OBSharp.Interop`
（libobs 的全套绑定）、`OBSharp.Interop.UI`（浏览器控件 / 窗口 / 文件对话框，逻辑全在托管侧；底下只由
`OBSharp.Interop.Native` 提供一层不含判断的 C ABI 转发）和
`Microsoft.Extensions.Logging.Abstractions`（`ILogger`，不用另外加包，插件的日志已经接在 OBS 日志上了）。

## 引用

```xml
<PropertyGroup>
  <!-- 插件自己要读写指针时才需要，interop 绑定基本都是指针 -->
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="..\OBSharp.SDK\OBSharp.SDK.csproj" />
</ItemGroup>

<!-- 换成一个 SDK 包的话，改成 <PackageReference Include="OBSharp.SDK" Version="..." />，这个 Import 就不用了 -->
<Import Project="..\OBSharp.SDK\build\OBSharp.SDK.targets" />
```

这个 `Import` 把宿主会提供的程序集从插件的输出里去掉：`OBSharp.SDK`、`OBSharp.Interop`、
`Microsoft.Extensions.Logging.Abstractions` 和它带的
`Microsoft.Extensions.DependencyInjection.Abstractions`
（引用包时自动生效，不用写 `Import`）。它们只在编译时有用，运行时宿主自己带着（见「部署」）；
不拷进 `bin`、不写进插件自己的 `deps.json`，`dotnet publish` 也一样。插件自己的第三方依赖不受影响，
确实要带上它们的话设 `OBSharpExcludeHostAssembliesFromOutput=false`。

## 最小插件

```csharp
using OBSharp;
using OBSharp.Interop;

namespace MyPlugin;

public sealed class MyPlugin : ObsPlugin
{
    // 插件名，同时也是数据目录名；不写就用程序集名。
    public override string Name => "MyPlugin";
    public override string? Description => "我的第一个插件";

    public override bool Load()
    {
        // 直接写进 OBS 的日志
        ObsExtensions.Log(300, $"数据目录：{DataFolder}");
        return true;
    }

    public override void Unload()
    {
        // 释放插件自己持有的非托管资源
    }
}
```

一个程序集里只能有一个 `ObsPlugin` 子类，而且构造函数不能带参数：宿主用无参构造造出插件，再附加
`Context`，之后 `Load()` 里才拿得到东西。

## 生命周期

`Load()` → 所有插件都加载完再 `PostLoad()` → 卸载时 `Unload()`（实现了 `IDisposable` 的插件还会顺带 `Dispose()`）。

- 插件抛异常只会记一条日志，不影响别的插件。
- `Load()` 返回 `false` 或者抛异常表示加载失败：宿主记一条日志，然后回滚这个插件（`Unload()` 加卸掉加载上下文），
  不会留下半加载状态。插件列表里会显示成失败，可以再启用一次。

## 插件能拿到什么

| 成员 | 说明 |
| --- | --- |
| `Name` / `Description` | 插件名与描述，加载前就可用；宿主加载时读一次存下来，之后不再问插件 |
| `Context` | `ObsPluginContext`，`Load()` 之后才可用 |
| `DataFolder` | `<OB# 数据目录>/<插件名>`，附加之前已经创建好 |
| `CreateBuilder()` | 配置插件自己的 Web 应用；跑起来会挂成 OBS 的停靠面板 |
| `Context.RegisterHost()` / `Hosts` | 注册 / 查找 host（显式的 Kestrel，见「host 与窗口」） |
| `Context.CreateBrowser()` / `CreateWindow()` | 建浏览器 / 普通窗口（见「host、浏览器、窗口」） |

宿主固定跑在 ASP.NET Core 上（`Microsoft.NETCore.App` 和 `Microsoft.AspNetCore.App` 一起加载），插件要用
ASP.NET Core 里的类型，自己加 `<FrameworkReference Include="Microsoft.AspNetCore.App" />` 就能编译；那份程序集
由共享框架提供，不用拷进插件目录。

OB# 自己的东西（比如它作为 OBS 模块的 `obs_module_t*`）不会交给插件；插件需要什么，宿主都转成托管对象给。

## 调用 libobs

绑定都在 `OBSharp.Interop` 里。所有权会转移给调用方的返回值是 `SafeHandle`（出了作用域就释放），借来的指针还是裸指针：

```csharp
using OBSharp.Interop;

using var data = ObsDataExtensions.Create();
data?.SetString("name", "value");
string? json = data?.GetJson();

// 直接写 OBS 日志
ObsExtensions.Log(300, "hello from plugin");
```

## 日志

插件 Web 应用（`CreateBuilder()`）的日志接在 OBS 日志上：DI 里的 `ILogger` / `ILogger<T>` / `ILoggerFactory`
写出来的东西会落进 OBS 的日志，一行一条，以插件标识符开头，形状跟宿主自己的日志一致：

```
[MyPlugin] [INFO] MyPlugin.SomeService: 数据目录：C:\...\MyPlugin
[MyPlugin] [WARN] MyPlugin.SomeService: ConnectionId:0HN… RequestPath:/: 面板没能起来
```

- 级别映射到 libobs 的 log level（`Information` → `LOG_INFO`(300)，`Debug` → 400，`Warning` → 200，
  `Error` → 100，`Trace` → `LOG_LOG`(500)，`Critical` → 50）；`scope` 串在消息前面。
  `[LoggerMessage]`、异常、结构化日志参数这些 .NET 的写法照常。
- 过滤按 .NET 自己的规则走：`ContentRootPath` 就是数据目录，所以插件的 `appsettings.json` 里
  `Logging:LogLevel` 说了算。默认的 console provider 没去掉，从终端起 OBS 时终端里也能看到。
- 浏览器相关的日志也走同一条线，CEF 起不来之类的警告能在 OBS 日志里找到。
- 拿不到 `ILogger` 的地方（`Load()` 里、建 builder 之前、没有 Web 应用的插件）还是直接
  `ObsExtensions.Log(300, "…")`（见上一节）。

## 面板

插件用 `CreateBuilder()` 配一个 ASP.NET Core 应用（监听在以插件名命名的命名管道 / UDS 上），
把它跑起来（`Run` / `RunAsync`）之后，OB# 会把它挂成 OBS 的一个停靠面板：obsharp-browser 的
CEF 连上这个应用，Widget 交给 `obs_frontend_add_dock_by_id()`，面板标题就是插件名；应用停下
（插件被停用）时面板撤掉。

- 页面就是插件自己的 Kestrel 应用，`ContentRootPath` 是 `DataFolder`，`wwwroot` 在
  `<DataFolder>/wwwroot`。
- 面板里的 HTTP 是从命名管道 / UDS 上代理过来的，一个连接一个请求（`Connection: close`），
  过不了 WebSocket 升级：Blazor 用静态 SSR（表单回发），别用交互式渲染（SignalR 的
  WebSocket 用不上，只能指望 SSE / 长轮询回退）。
- 要在面板上跑 Blazor 这类带静态资源的应用，`MapStaticAssets()` 得显式喂清单的绝对路径：它默认
  去 `AppContext.BaseDirectory`（那是宿主目录，不是插件目录）找 `{程序集名}.staticwebassets.endpoints.json`，
  找不到会直接抛。资源本身按 `wwwroot` 解析，所以插件要按「发布」形态部署（`dotnet publish`），
  让清单和 `wwwroot` 都待在插件目录里。`MapRazorComponents<App>()` 后面跟一句
  `WithStaticAssets(同一个路径)`，`@Assets` 才拿得到带指纹的地址（清单里没有的 key 会原样返回，不抛）。
  注意 `dotnet build` 的输出目录里**没有** `wwwroot`，只有 publish 才有——用 `build` 的产物跑面板，
  静态资源会 500（`StaticAssetDevelopmentRuntimeHandler` 去 `WebRootPath` 找不到源文件）。
- 插件是 `Library` 项目（当 Exe 的那种 apphost 不是插件），而 SDK 的
  `_AddBlazorFrameworkStaticWebAssets` 只在 `OutputType=Exe` 时才把 `_framework/blazor.web.js`、
  `blazor.server.js` 加进静态资源：`Library` 插件的清单里没有它们，挂
  `<script src="_framework/blazor.web.js">` 只会 404。静态 SSR + 表单回发本来就不需要脚本
  （浏览器直接 POST、服务端整页重渲染）；要增强导航的话得自己把包里那份 `_framework` 拷进 `wwwroot`。
- 进程里没有 obs-browser、CEF 起不来，插件照常跑，只是没有面板（日志里会记一条）。

这个是「一个插件一个 Web 应用 + 一个面板」的便捷写法，面板 id 就是插件名。要多个面板、独立窗口、
多个 host，用下一节的 `RegisterHost` / `CreateBrowser` / `CreateWindow`。

## host、浏览器、窗口

SDK 刻意**不**发明「窗口」体系：三件事分开，各管各的。

| 概念 | 类型 | 管什么 |
| --- | --- | --- |
| host | `ObsHost` | 一个名字 + 一台只监听 IPC 端点的 Kestrel |
| 浏览器 | `ObsBrowser` | 一块 CEF 页面，**不知道**自己要挂到哪 |
| 停靠面板 | `ObsHost.AddDockPanel` | 把浏览器 Widget 交给 OBS（`obs_frontend_add_dock_by_id`） |
| 普通窗口 | `ObsWindow` | 一个 `Qt::Window` 容器，内容自己塞 |

这样拆的好处：同一块浏览器既能放进窗口、也能挂成停靠面板，不用为了换个位置重建。

```csharp
using Microsoft.AspNetCore.Builder;

using OBSharp;

public sealed class MyPlugin : ObsPlugin
{
    private ObsHost? _host;
    private ObsBrowser? _dockBrowser;
    private ObsWindow? _window;

    public override bool Load()
    {
        // 1. 注册 host（只注册、不启动）。同名幂等。
        ObsHost host = Context.RegisterHost("mypanel");
        _host = host;

        // 2. 配 Kestrel 并启动。Builder 只能取一次。
        WebApplication app = host.Builder.Build();
        app.MapGet("/api/ping", () => "pong");
        host.Start();

        // 3. 停靠面板：OBS 自己建 QDockWidget、挂进主窗口、并**自动**加
        //    「停靠窗口」菜单项。
        _ = Task.Run(() =>
        {
            _dockBrowser = Context.CreateBrowser(host);
            host.AddDockPanel("mypanel-dock", "我的面板", _dockBrowser);
        });

        // 4. 「工具」菜单：只加菜单项，窗口在回调里才建（点一次建一次）。
        _ = host.AddToolsMenuItem("我的面板", ShowWindow);

        return true;
    }

    private void ShowWindow()
    {
        _ = Task.Run(() =>
        {
            _window ??= CreateWindow();
            _window.Show();
        });
    }

    private ObsWindow CreateWindow()
    {
        ObsHost host = _host!;
        ObsWindow window = Context.CreateWindow(new ObsWindowOptions
        {
            Title = "我的面板",
            Width = 960,
            Height = 640,
        });

        ObsBrowser browser = Context.CreateBrowser(host);
        window.Content = browser.Widget;

        // 关掉就销毁，下次点菜单重建 —— 关掉的原生窗口不能复用。
        window.CloseRequested += (_, _) =>
        {
            _window = null;
            _ = Task.Run(() =>
            {
                window.Dispose();
                browser.Dispose();
            });
        };

        return window;
    }
}
```

### host

- `Context.RegisterHost(name)` 建一个 host（同名幂等），`Context.FindHost(name)` / `Hosts` 查找。
  host 名是 `^[a-z0-9]([a-z0-9-]{0,46}[a-z0-9])?$`。
- 注册**不启动**：`Start()` / `StartAsync()` 才让 Kestrel 开始监听。配置用 `Builder`
  （只能取一次，`Build` 之后取会抛），`Build()` / `Start()` 都是幂等的。
- Kestrel 只监听 IPC 端点，不占 TCP 端口：
  - Windows：命名管道 `\\.\pipe\obsharp\<name>`；
  - Unix：UDS `/tmp/obsharp/<name>.socket`（启动后权限 0600）。
- `ContentRootPath` 是插件数据目录，`wwwroot` 在 `<DataFolder>/wwwroot`，日志接
  `ObsPluginLoggerProvider`（落进 OBS 日志）。
- `Url` 是给插件和页面看的规范地址：`obsharp://<name>/`。

### 浏览器

- `Context.CreateBrowser(host, url = null)` 建一块浏览器并登记到插件名下（卸载时自动释放）。
  `url` 为 null 时用 host 的地址；相对地址（`/settings`）会拼在 host 地址后面。
- `Widget` 是底层 `QWidget*`，交给 OBS 或窗口用。
- 便宜的操作：`Load` / `Reload` / `PostMessage` / `ExecuteScript`。

### 停靠面板

- `host.AddDockPanel(id, title, browser)` → 底层 `obs_frontend_add_dock_by_id()`。
- **OBS 会自动加「停靠窗口」菜单项**：`add_dock_by_id` 内部走 OBS 自己的 `AddDockWidget`，
  那条路径会 `ui->menuDocks->addAction(toggleViewAction())`。不需要（也没有）额外的菜单 API。
- 挂上之后 Widget 归 OBS：**不要**再 `ObsBrowser.Dispose()`，撤面板用
  `host.RemoveDockPanel(id)`。

> **已知限制（实测）**：OBS 关闭停靠面板只是隐藏它，重新显示后 CEF 的 OSR 不再出帧，
> 而且页面会停掉 —— 日志显示重新显示后连一个 HTTP 请求都没有（第一次打开有 `/`、
> `*.css`、`blazor.web.js`，第二次之后是 0 个）。浏览器对象还活着，但页面已经死掉，
> 靠强制重绘救不回来。
>
> 这层耦合在 obs-browser 的 dock 生命周期里，不在本库能改的范围。所以
> **需要反复开关的页面请用工具窗口**（`Context.CreateWindow` + 工具菜单），关掉即销毁、
> 下次重建，没有这个问题。

### 普通窗口

- `Context.CreateWindow(options)` → `ObsWindow`，原生侧是 `Qt::Window`
  （普通标题栏、能最小化 / 最大化、进任务栏、可单独 Alt+Tab；**不是** Qt / Windows 意义上的
  tool window）。
- **纯容器**：`Content` 放什么就显示什么，SDK 不替你建浏览器。
- 用户点关闭只 `Hide()` 并触发 `CloseRequested`，窗口对象还在；要真正销毁就 `Dispose()`，
  之后要再显示得重新 `Create()`（关掉的原生窗口不能复用）。

### 「工具」菜单

- `host.AddToolsMenuItem(text, onClick)` → `obs_frontend_add_tools_menu_item()`。
- **OBS 没有「移除菜单项」的 API**：加进去的项会一直留在菜单里。SDK 用「静态回调 +
  递增 id 查表」处理，插件卸载后点它安全地什么都不做。
- **窗口应该在回调里懒创建**：点一次才建、关掉就销毁。插件加载时不占 CEF 实例。

### 生命周期

- 插件卸载时宿主调 `ObsPluginContext.Close()`：先撤停靠面板、销毁窗口和浏览器，再把所有
  host 丢到后台停，不阻塞 OBS 主线程。
- 自己 `Dispose()` 也是安全的，多次调用是空操作。
### 为什么 CEF 里实际是 `https://<host>.obsharp/`（但并没有加密）

`obsharp://` 不是 Chromium 认识的 scheme，没法直接加载，所以托管侧（`OBSharp.Interop.UI`）
在**创建浏览器时**做一次映射：

```
obsharp://<host>/path?query   →   https://<host>.obsharp/path?query
```

并按 host 注册 `CefRegisterSchemeHandlerFactory("https", "<host>.obsharp", …)`，factory 里带着该 host
的 IPC 端点。`https` 是 CEF / Chromium 的内置 scheme，**允许**挂 scheme handler factory——只有当
factory 返回 `nullptr` 时才会回落到真正的网络传输。我们的 factory 总是返回 handler，所以请求被直接
转给命名管道 / UDS，**压根不进网络栈、不做 TLS 握手、没有加解密开销**。

之所以选 `https` 而不是 `http`：只有 https（以及 localhost 之类的可信来源）才算
[安全上下文](https://developer.mozilla.org/docs/Web/Security/Secure_Contexts)。
选 `http` 的话 `isSecureContext` 是 `false`，`crypto.subtle`、`navigator.serviceWorker`、
`navigator.clipboard`、`PublicKeyCredential` 这些 API 全部不可用。用 `https` 之后它们都能用，
而传输仍然是明文 IPC。

注意事项：

- 页面里 `window.location.href` 是 `https://<host>.obsharp/...`；引导脚本另外给了
  `window.obsharp.host` 和 `window.obsharp.url = "obsharp://<host>/"`。
- 页面内导航请用**相对路径**，或者写 `https://<host>.obsharp/...`；**不要**把 `obsharp://...` 写进
  `<a href>` / `location.href`（Chromium 不认识这个 scheme，会当外部协议处理）。
- 插件 API 里一律用 `obsharp://<host>/...`，映射由 `OBSharp.Interop.UI` 统一做。
- 想退回明文来源（不安全上下文），可以在 `BrowserOptions` 里显式设 `Scheme = "http"`。
- **WebSocket 不通**：`ws` / `wss` 不经过 scheme handler（Chromium 的 WebSocket 走独立 connector，
  会自己做 TLS 握手），所以 `wss://<host>.obsharp/` 连不上。需要双向实时通道时用
  `window.obsharp.post()` / `CustomEvent("obsharp-message")`，或者让 Kestrel 另开一个真实的 TCP 监听
  （页面连 `ws://127.0.0.1:<port>/...`，回环地址不受混合内容限制）。

## 数据目录与本地化

插件的数据目录就是 `DataFolder`（`<OB# 数据目录>/<插件名>`），插件自己的配置、资源都放这里。

本地化直接用 .NET 自己的机制：字符串写成 `.resx` 资源（按 `Strings.zh-CN.resx` 这样的名字分语言），
用 `ResourceManager`（或 ASP.NET Core 的 `IStringLocalizer`）取。宿主把 OBS 的当前语言同步到了
`CultureInfo.CurrentUICulture`（由 `obs_module_set_locale` 触发，同时设了 `DefaultThreadCurrentUICulture`），
所以按 culture 取资源会自动跟着 OBS 的语言走，插件不用自己读 ini、也不用管 OBS 的
`obs_module_text`。

## 部署

1. 把插件 dll（连同它旁边的 `.deps.json` 和依赖）放进插件目录，比如 `<数据目录>/obs-plugins/OBSharp/MyPlugin/`。
2. 在 `<数据目录>/obs-plugins/OBSharp/plugins.ini` 里写一行，`=` 后面是 `true`：

   ```ini
   ; 插件列表：<程序集路径> = <true|false>，分号和 # 后面是注释
   C:\path\to\MyPlugin\MyPlugin.dll = true
   MyOther\MyOtherPlugin.dll = false
   ```

   路径可以写相对的，按这个文件所在目录（也就是 OB# 数据目录）解析。
3. 重启 OBS。

`= false` 的行不会加载，但会留在插件列表里，可以在插件管理器面板（`OBSharp.PluginManager` 这个插件）里启用。
启用或停用之后 OB# 会把列表写回这个文件，所以手写的注释会被覆盖、相对路径会变成绝对路径。

`OBSharp.SDK`、`OBSharp.Interop` 和 `Microsoft.Extensions.Logging.Abstractions` 由宿主提供（宿主自己那份 `OBSharp.Runtime` 也一样）：插件目录里就算有自己的一份副本也会被忽略（引用 SDK 时 `build/OBSharp.SDK.targets` 已经把它们从输出里去掉，编译时照样能引用）。跨边界的类型（`ObsPlugin`、`ILogger<T>`……）只有一份，插件才认得出来。插件加载在自己的 `AssemblyLoadContext` 里，可以卸载，卸载时宿主会把加载上下文也卸掉。

> 插件管理 API（`OBSharpPluginManager` 那一套）是 `OBSharp.Runtime` 的 `internal` 成员，只有插件管理器
> （`OBSharp.PluginManager`，宿主的友元程序集）能用，普通插件拿不到。

## 目录

| 文件 | 内容 |
| --- | --- |
| `ObsPlugin.cs` | 插件基类 |
| `ObsPluginContext.cs` | 宿主交给插件的环境信息、host 注册、浏览器 / 窗口创建 |
| `ObsHost.cs` | 一个 host：只监听 IPC 端点的 Kestrel + 停靠面板 / 工具菜单 API |
| `ObsWindowOptions.cs` | 建窗口的参数（标题、尺寸、置顶、可缩放） |
| `ObsWindow.cs` | 普通顶层窗口容器（`Qt::Window`，可置顶、`Resize`、`CloseRequested`） |
| `ObsBrowser.cs` | 一块 CEF 页面（可挂停靠面板或窗口） |
| `ObsPluginLogger.cs` | 插件日志的落地：写进 OBS 日志，带插件标识符前缀 |
| `ObsPluginLoggerProvider.cs` | 把上面那个接进插件 Web 应用 |
| `build/OBSharp.SDK.targets` | 插件工程 Import 它（引用包时自动导入）：把宿主提供的程序集从插件输出里去掉 |
