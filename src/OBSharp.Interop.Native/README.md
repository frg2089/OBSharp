# OBSharp.Interop.Native

OB# 的原生底座。**一个不含任何业务逻辑的 C ABI 转发层**：把 Qt 与 CEF 的 C++
接口压成一行式 C 函数，供 `OBSharp.Interop` 里的托管绑定 P/Invoke。

它不是「OB# 的浏览器后端」——那个现在完全由 C# 实现（`OBSharp.Interop.UI` / `OBSharp.Interop.UI.Internal`）。
这个库只负责「C# 碰不到的那一层」：C++ 虚函数调用、Qt Widget 的指针操作、
可变参数函数、以及跨线程封送。

> **注意**：本项目完全由 AI 编写。提交前请先阅读代码，尤其是线程与所有权相关的部分。

## 为什么要这么切

旧版 `OBSharp.Interop.Browser.Native` 是一个「胖原生库」：它在 C++ 里实现了
HTTP/1.1 客户端（命名管道 / UDS）、分块解码、URL 与 host 映射、引导脚本拼装、
`$obsharp` 控制消息解析、scheme handler 的全部状态机，以及窗口与文件对话框的编排。
大约 3000 行 C++，其中绝大部分是**业务判断**，不是平台胶水。

问题在于：这些逻辑属于 OB# 的产品行为，却住在 C++ 里。改一条 URL 规则要动 C++、
重编原生库、重启 OBS；单元测试无从下手；托管侧只能通过一个粗粒度 ABI 间接观察它的行为。

所以这一版把边界重新画在「语言能力」上，而不是「功能模块」上：

| 归属 | 判据 | 例子 |
| --- | --- | --- |
| **原生（本库）** | C# / C 干不了，或干起来很难看 | C++ 虚函数调用、Qt 指针操作、可变参数 `blog()`、线程封送 |
| **C#（`OBSharp.Interop.UI.Internal`）** | 只跟「怎么想」有关，跟「谁去调」无关 | URL 映射、端点推导、引导脚本、JSON 派发、HTTP 分帧、注册去重 |

一句话：**原生层不知道 `obsharp://` 是什么意思。**

## 架构

```
                OB# 宿主进程（OBS.exe）
├─ OBSharp.Interop（托管：Native 绑定 + UI 层，全部业务逻辑）
│    ├─ Native/  QcefHost      解析 obs-browser 面板接口
│    │           EndpointResolver / UrlMapping / BootstrapScript
│    │           BridgeDispatcher / KestrelClient / SchemeHandlerBridge
│    ├─ WebView / Window / FilePicker   公开 API
│    └─ P/Invoke ──▶ OBSharp.Interop（Native/ 目录，托管绑定）
│                        │
│                        ▼
├─ OBSharp.Interop.Native（本库，C++）── 只有转发，没有判断
│    ├─ 一行式转发    resize / show / set_url / set_startup_script ……
│    ├─ 零判断适配器  CefResourceHandler / QObject 信号 / eventFilter / closeEvent
│    └─ 平台胶水      blog() 包装、符号查找、Qt 主线程与 CEF IO 线程封送
│
└─ Qt6 + CEF + obs-browser —— 全部来自 OBS 自带的运行时
```

数据流（与旧版一致，只是控制权换了地方）：

* **页面 → C#**：引导脚本（现在由 C# 生成）把 `window.obsharp.post(json)` 变成
  `POST <scheme>://<domain>/__obsharp/message`。CEF 命中 scheme handler，本库把请求
  原样拆成裸数据交给 C# 的 `on_open`；「这是控制消息还是普通消息」「要不要连 Kestrel」
  全由 C# 决定。
* **C# → 页面**：`obsharp_cef_widget_execute_script()` 派发
  `CustomEvent("obsharp-message")`，脚本内容由 C# 拼。

## 「不含逻辑」到底怎么落地

有些东西**必须**用 C++ 类实现，因为 CEF 和 Qt 要求它：

* `CefResourceHandler` / `CefSchemeHandlerFactory` 是纯虚接口，得有人填 vtable；
* Qt 信号只能连到 `QObject`；
* `eventFilter` / `closeEvent` 是 `QObject` / `QWidget` 的虚函数。

这些地方放的是**零判断适配器**：它们不解析、不缓存业务状态、不做决定，每个回调
原样转给 C# 函数指针，连返回值都听 C# 的。比如事件过滤器：

```c
/* C# 决定「这个事件要不要吃掉」；原生侧只是把 return 值往回传。 */
typedef int32_t(OBSHARP_NATIVE_CALL* obsharp_filter_event_fn)(
  void* user, void* widget, int32_t event_type);
```

所以「OBS 关停靠面板时会发一个 `QEvent::User + QEvent::Close`（1019），我们得拦下来
否则 CEF 从此报废」这条知识，现在写在 C# 里（`WebView` 的事件过滤器回调），
不在 C++ 里。

唯一保留的机械状态是两个 **CEF 异步契约的挂起槽位**：

* `Read` 暂时没数据时，CEF 要求「记住回调、置 `bytes_read = 0`、**返回 true**」；
  返回 false 会被当成响应结束，页面被截断。原生侧记下这个回调，等 C# 调
  `obsharp_resource_handler_resume_read()` 再续上。
* `Skip` 同理。

这是「怎么跟 CEF 说话」而不是「该说什么」，所以留在原生侧。

## ABI

公开头：[`include/OBSharp.Interop.Native.h`](include/OBSharp.Interop.Native.h)。
托管绑定：[`../OBSharp.Interop/Native/`](../OBSharp.Interop/Native/)。两边改动要同步。

* 字符串一律 NUL 结尾的 UTF-8。
* 返回的 `const char*` 是**借用指针**，下一次同类调用后失效；托管侧用
  `OBSStringMarshaller` 只复制、不释放。
* 返回的 `void*` 是借用的 Qt / CEF 指针；销毁时机由 C# 决定，本库不加引用计数
  （`CefResourceHandler` 除外，它本来就被 CEF 引用计数管理）。
* 版本：`obsharp_abi_version()` 必须等于头文件里的 `OBSHARP_NATIVE_ABI_VERSION`；
  托管侧启动时比对，不匹配直接报错。

### 线程

| 函数 | 线程要求 |
| --- | --- |
| `obsharp_abi_version` / `obsharp_version` / `obsharp_log` / `obsharp_is_ui_thread` / `obsharp_find_module` / `obsharp_find_symbol` | 任意线程 |
| `obsharp_invoke_ui` | 任意线程（不在主线程时阻塞投递） |
| `obsharp_resource_handler_resume_read` / `_resume_skip` | 任意线程（内部封送到 CEF IO 线程） |
| 其余全部 | Qt 主线程 |

托管侧一律经 `NativeApi.InvokeUI` 切线程，调用方不用自己判。

## 依赖从哪来（CEF / Qt6）

跟旧版一模一样，由 [`cmake/ObSharpDeps.cmake`](../../cmake/ObSharpDeps.cmake) 负责：
缺 CEF / Qt6 就按 obs-studio 自己的清单下到 `lib/deps/`（已经就位就跳过）。版本 /
地址 / SHA-256 全从 `lib/obs-studio` 的清单里读，不手写。

| 平台 | CEF | Qt6 |
| --- | --- | --- |
| Windows x64 / arm64 | 自动下 | 自动下（arm64 交叉编时另需 x64 的 host tools，脚本会自动设 `QT_HOST_PATH`） |
| Linux | 自动下 | 用发行版包（`qt6-base-dev`） |
| macOS | 自动下 | 自动下 universal 包 |

想用现成的就不下载：给 `-DCEF_ROOT_DIR=…`，或让 `CMAKE_PREFIX_PATH` 里已经有 Qt6。
整体关掉自动下载用 `-DOBSHARP_FETCH_DEPS=OFF`。

## 工具链（和 obs-studio 一样）

本库最后要被 `LoadLibrary` / `dlopen` 进 **OBS 进程**，跟 obs-browser 的 CEF、OBS
自己的 Qt6 共处一个地址空间。工具链差一代就可能在这些交界处踩到 ABI 问题，所以照
obs-studio 32 自己的 CI 来：Windows 用 VS 2026（MSVC v145）+ Windows SDK
10.0.26100.0，Linux 用发行版 GCC，macOS 用 Xcode 26。

CRT 上有个坑：CEF 预编译的 `cef_dll_wrapper` 是**静态 CRT**（`/MT`），所以本库也必须
`/MT` / `/MTd`（`CMakeLists.txt` 里用 `CMAKE_MSVC_RUNTIME_LIBRARY` 设死），否则跨 DLL
传对象、释放对方内存会踩坏堆。

本库**不链接 libobs**：`blog` / `obs_get_module` / `obs_get_module_lib` /
`obs_get_nix_platform` 都是运行时从已经加载的模块里取符号的
（`obsharp_find_module` / `obsharp_find_symbol`，绝不因为查找而加载新模块）。
链接的只有 `Qt6::Widgets`、`CEF::Wrapper`、`CEF::Library`。

## 构建

产物落在 `out/native/<arch>/<config>/`（`<arch>` 为 `x64` / `arm64` / `x86`，
`<config>` 为 `Debug` / `Release`）：

```
Windows   OBSharp.Interop.Native.dll
Linux     libOBSharp.Interop.Native.so
macOS     libOBSharp.Interop.Native.dylib
```

```pwsh
# 从仓库根
cmake --preset win-x64
cmake --build --preset win-x64-release
```

Debug 配置用 `--config Debug`：

```pwsh
cmake --build out/build/win-x64 --config Debug --target OBSharp.Interop.Native
```

在 Visual Studio 里也可以直接用
[`OBSharp.Interop.Native.vcxproj`](OBSharp.Interop.Native.vcxproj)：它是个 MSBuild 外壳，
本身不带编译规则，只把 Build / Clean / Rebuild 转成对 CMake 的调用。

整条发布链路（原生库 → 托管 publish → 摆插件目录）走
[`scripts/Build-Dist.ps1`](../../scripts/Build-Dist.ps1)。

## 源码

| 文件 | 内容 |
| --- | --- |
| [`include/OBSharp.Interop.Native.h`](include/OBSharp.Interop.Native.h) | 公开 C ABI；托管绑定照着它写 |
| `src/` | 实现：一行式转发、零判断适配器、平台胶水 |
| [`CMakeLists.txt`](CMakeLists.txt) | 构建规则、CEF / Qt6 发现、工具链设置 |
| [`OBSharp.Interop.Native.vcxproj`](OBSharp.Interop.Native.vcxproj) | MSBuild 外壳，转调 CMake |

## 已知限制

* **WebSocket 不经过 scheme handler**：CEF 的 WebSocket 走独立的 connector，会自己
  去做 TLS 握手，所以 `wss://<host>.obsharp/` 连不上。页面要双向实时通道请用
  `window.obsharp.post()` / `CustomEvent("obsharp-message")`，或者自己起一个真实的
  TCP 监听。
* **停止依赖面板这条路径仍有风险**：CEF 用无窗口渲染（OSR），面板隐藏期间页面可能
  停掉。本库只提供 `obsharp_widget_resize` / `obsharp_widget_update` 这些原语让 C#
  在重新显示时「踢一下」重绘；具体策略在 C# 侧。
