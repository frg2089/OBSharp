# OBSharp.Interop.Native

OBSharp 的原生底座：一个不含业务逻辑的 C ABI 转发层。它把 Qt 与 CEF 的 C++ 接口压成
一行式 C 函数，供 `OBSharp.Interop` 的托管绑定 P/Invoke。

本库只负责「C# 碰不到的那一层」——C++ 虚函数调用、Qt Widget 指针操作、跨线程封送。
产品行为（协议、路由、状态机）全部在 C#。

> 本项目由 AI 编写。提交前请先阅读代码，尤其是线程与所有权相关的部分。

## 职责边界

边界画在「语言能力」上，不画在「功能模块」上：

| 归属 | 判据 | 例子 |
| --- | --- | --- |
| **原生（本库）** | C# / C 干不了，或干起来很难看 | C++ 虚函数调用、Qt 指针操作、线程封送 |
| **C#** | 只跟「怎么想」有关，跟「谁去调」无关 | URL 解析、消息路由、状态管理、编排 |

一句话：**原生层不理解任何应用层语义。**

## 导出面

所有导出经 `OBSHARP_API`（Windows 下 `extern "C" __declspec(dllexport)`），调用约定
`OBSHARP_CALL`（`__cdecl`）。按头文件分组：

| 头 | 导出 |
| --- | --- |
| [`src/obsharp.h`](src/obsharp.h) | `obsharp_get_version` |
| [`src/host.h`](src/host.h) | `obsharp_find_module` / `obsharp_find_symbol`（只查已加载模块，查找不触发加载） |
| [`src/logger.h`](src/logger.h) | `obsharp_set_logger` + `obsharp_log_level` / `obsharp_log_fn` |
| [`src/QT/thread.h`](src/QT/thread.h) | `obsharp_is_ui_thread` / `obsharp_ui_invoke` |
| [`src/QT/application.h`](src/QT/application.h) | 应用级操作：事件循环、活动窗口、样式表、字体、提示音 |
| [`src/QT/widget.h`](src/QT/widget.h) | `obsharp_widget_*`：创建/销毁、几何、可见性、焦点、父子、样式、事件过滤器 |
| [`src/QT/window.h`](src/QT/window.h) | `obsharp_window_create`（带关闭回调的顶层窗口容器） |
| [`src/QT/filepicker.h`](src/QT/filepicker.h) | `obsharp_filepicker_*`：文件对话框的创建、配置、选择结果 |
| [`src/CEF/qcef.h`](src/CEF/qcef.h) | `obsharp_qcef_*`（QCef 初始化、cookie 管理器、建 widget）、`obsharp_cef_widget_*`（URL、脚本、缩放、关闭） |
| [`src/CEF/scheme.h`](src/CEF/scheme.h) | CEF scheme handler 桥：`obsharp_scheme_register`、`obsharp_response_*`、`obsharp_resource_handler_resume_*`、请求头与回调表结构体 |

`src/QT/qt.h`（QString/UTF-8 转换）、`src/CEF/widgetsignals.h`（QObject 信号适配器）是
纯 C++ 辅助，不产生 C ABI 导出。

托管绑定由生成器照这些头产出（`OBSharp.Interop/Generated/native`，见
[`../OBSharp.Interop.Generator/README.md`](../OBSharp.Interop.Generator/README.md)）；
手写胶水在 [`../OBSharp.Interop/Abi/`](../OBSharp.Interop/Abi/)。

## 线程

| 函数 | 线程要求 |
| --- | --- |
| `obsharp_get_version` / `obsharp_set_logger` / `obsharp_find_module` / `obsharp_find_symbol` / `obsharp_is_ui_thread` | 任意线程 |
| `obsharp_ui_invoke` | 任意线程；已在 UI 线程时直接同步调用，否则阻塞投递（投递失败返回 `false`） |
| `obsharp_scheme_register` | 任意线程；内部切到 CEF UI 线程并等它跑完 |
| `obsharp_resource_handler_resume_read` / `obsharp_resource_handler_resume_skip` | 任意线程；内部封送到 CEF IO 线程 |
| 其余全部（QT/*、CEF/qcef） | Qt 主线程 |

托管侧一律经 `AbiApi.InvokeUI` 切线程，调用方不用自己判。

## 所有权与字符串约定

* 字符串一律 NUL 结尾的 UTF-8。返回的 `const char*` 是**借用指针**，在下一次同类调用后
  失效；托管侧只复制、不释放。
* 返回的 Qt / CEF 对象指针是借用的，销毁时机由调用方决定。例外是
  `ResourceHandler`——它的生命周期由 CEF 的引用计数管理。
* `obsharp_window_create` / `obsharp_qcef_create_widget` 拿到的对象归调用方销毁，
  销毁走 `obsharp_widget_destroy`（内部是 `deleteLater`，栈上安全）。
* 销毁窗口/面板有顺序要求（详见 `window.h` / `qcef.h` 注释）：先
  `obsharp_widget_set_content(..., NULL)` 摘内容、`obsharp_widget_hide`，
  最后 `obsharp_widget_destroy`。CEF widget 拆除前先
  `obsharp_cef_widget_detach_callbacks` 断回调，再关浏览器。
* `obsharp_widget_install_event_filter` 返回的过滤器句柄是借用的，配对
  `obsharp_widget_remove_event_filter` 注销（注销时内部 `deleteLater`）。

## CEF scheme handler 桥

有些东西必须用 C++ 类实现——CEF 和 Qt 要求它：`CefResourceHandler` /
`CefSchemeHandlerFactory` 是纯虚接口，Qt 信号只能连到 `QObject`。这些类是**零判断
适配器**：每个回调原样转给 C# 函数指针，返回值也听 C# 的，本库不解析、不缓存业务
状态。

`obsharp_scheme_register(scheme, domain, callbacks)` 给任意 `scheme://domain` 注册
handler factory。请求到达时，`on_open` 拿到的是拆平的裸数据：方法、完整 URL、请求头
数组、POST body 原始字节；URL 解析、消息语义判断全在 C#。

唯一保留的机械状态是 CEF 异步契约的两个挂起槽位：

* `read` 暂时没数据时，CEF 要求「记下回调、`bytes_read = 0`、返回 true」——返回 false
  会被当成响应结束。本库挂起这次读，等 C# 调 `obsharp_resource_handler_resume_read` 续上。
* `skip` 同理，`obsharp_resource_handler_resume_skip` 的参数是**累计**已跳过字节数
  （不是增量）。

## 依赖（CEF / Qt6）

由 [`cmake/ObSharpDeps.cmake`](../../cmake/ObSharpDeps.cmake) 负责：缺 CEF / Qt6 时按
obs-studio 自己的清单下载到 `lib/deps/`（已就位则跳过）。用现成的就不下载：给
`-DCEF_ROOT_DIR=…`，或让 `CMAKE_PREFIX_PATH` 里已有 Qt6；整体关掉自动下载用
`-DOBSHARP_FETCH_DEPS=OFF`。

链接面只有 `Qt6::Widgets`、`CEF::Wrapper`、`CEF::Library`。几个约束：

* **不链接 libobs**。`browser-panel.hpp` 需要 libobs 的头文件（`obs-module.h` 等），
  但只是头文件引用；运行期用到的 `blog` / `obs_get_module` 等符号一律走
  `obsharp_find_module` / `obsharp_find_symbol`。libobs 的 `obsconfig.h` 是构建期生成物，
  由 CMake 生成一个最小替身，只为解析通过。
* **静态 CRT**：CEF 预编译的 `cef_dll_wrapper` 是静态 CRT，本库必须跟着 `/MT` / `/MTd`
  （`CMakeLists.txt` 里设死），否则跨 DLL 传对象会踩坏堆。
* `Q_OBJECT` 的类（`widgetsignals.h`）需要 moc，CMake 里已开 `AUTOMOC`。

## 构建

产物在 `out/native/<arch>/<config>/`：

```text
Windows   OBSharp.Interop.Native.dll + 同名 .pdb
Linux     libOBSharp.Interop.Native.so
macOS     libOBSharp.Interop.Native.dylib
```

```pwsh
# 从仓库根
cmake --preset win-x64
cmake --build --preset win-x64-release
# Debug：
cmake --build out/build/win-x64 --config Debug --target OBSharp.Interop.Native
```

Visual Studio 里可用 [`OBSharp.Interop.Native.vcxproj`](OBSharp.Interop.Native.vcxproj)：
MSBuild 外壳，只把 Build / Clean / Rebuild 转成 CMake 调用。整条发布链路（原生库 →
托管 publish → 摆插件目录）见 [`scripts/Build-DevDist.ps1`](../../scripts/Build-DevDist.ps1)。

### 调试符号（PDB）与可复现构建

Windows 上 Debug / Release 都产出完整 PDB，与 DLL 同目录。调试符号与 `/Brepro`
的可复现构建不冲突，分工如下：

| 环节 | 配置 | 说明 |
| --- | --- | --- |
| 编译 | `/Z7` | 调试信息内嵌 `.obj`，没有共享编译器 PDB（`/Zi`）；`/ZI` 与 `/Brepro` 互斥（D8016），不用 |
| 链接 | `/DEBUG:FULL` | 把 obj 里的调试信息汇总成 PDB，所有配置都产出 |
| 链接 | `/PDBALTPATH:%_PDB%` + `/Brepro` | DLL 只嵌 PDB 文件名（不带构建机路径），时间戳换成内容哈希 |

`/DEBUG` 会把 `/OPT` 默认值翻成 `NOREF` / `NOICF`，非 Debug 配置显式写回
`/OPT:REF /OPT:ICF`（须排在 `/DEBUG` 之后）。

可复现性边界：同工具链 + 同源码，DLL 逐字节一致；PDB 只承诺符号完整、可随产物分发
（调试器靠 DLL 里的 `%_PDB%` 找同名 PDB），不承诺逐字节一致。开关见
[`CMakeLists.txt`](CMakeLists.txt)。

## 源码

| 路径 | 内容 |
| --- | --- |
| `src/*.h` | C ABI 导出面 |
| `src/*.cpp` | 一行式转发、零判断适配器、平台胶水 |
| `src/QT/qt.h` / `src/CEF/widgetsignals.h` | 纯 C++ 辅助，无 C ABI 导出 |
| [`CMakeLists.txt`](CMakeLists.txt) | 构建规则、依赖发现、工具链与可复现配置 |
| [`OBSharp.Interop.Native.vcxproj`](OBSharp.Interop.Native.vcxproj) | MSBuild 外壳，转调 CMake |
