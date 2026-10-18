# OBSharp.Interop.Browser.Native

一个跨平台原生动态库：把 **obs-browser 已经初始化好的 CEF** 包成 Qt Widget，交给
OB# 的宿主插件用；页面里跑的是 Kestrel 通过命名管道 / Unix domain socket 送过来的
HTTP 流。

它自己不初始化 CEF、不创建 `CefApp`，也不带自己的 Chromium —— 全部复用 OBS 里
obs-browser 的那一份（CEF 二进制 + `cef_dll_wrapper`）和 OBS 自己带的 Qt6。因此它
**只能被加载进 OBS 进程**（也就是 OB# 宿主所在的进程）。

> **注意**：本项目完全由 AI 编写。提交前请先阅读代码，尤其是线程与所有权相关的部分。

## 为什么是这个形状

| 目标 | 做法 |
| --- | --- |
| 不 vendored 新 CEF | 复用 obs-browser 的 `CEF::Library` / `CEF::Wrapper`（见 `lib/obs-studio/cmake/finders/FindCEF.cmake`）和它已经跑起来的 CEF 实例 |
| 不重复创建 `CefApp` | 调 obs-browser 的 `QCef::init_browser()`（它内部是幂等的 `obs_browser_initialize()`），而不是 `CefInitialize` |
| 把 ASP.NET Core 的流喂给 CEF | 注册一个 scheme handler：`http://obsharp/...` 的请求由它转成对 Kestrel endpoint 的连接 |
| 只要 `QWidget*`，不要 dock 封装 | `obsharp_browser_widget()` 直接把底层 `QCefWidget` 交出去，父级 / dock 由调用方决定 |

## 架构

```
                OB# 宿主进程（OBS.exe）
├─ OBSharp.Loader
│    └─ OBSharp.Interop.Browser（托管）
│         ├─ OBSharpNative      P/Invoke ──▶
│         └─ CefBrowser
├─ OBSharp.Interop.Browser.Native（本库，C++）
│    ├─ RuntimeHost    运行时解析 obs-browser 模块里的 QCef
│    ├─ HostApi        obs-browser 面板接口（QCef/QCefWidget）声明
│    ├─ BrowserWidget  QCefWidget 子类 + 引导脚本注入
│    ├─ SchemeHandler  http://obsharp/* → Kestrel
│    └─ KestrelClient  命名管道 / UDS 上的最小 HTTP/1.1 客户端
│         │
│         │  跨进程 / 跨线程：命名管道（Win）或 UDS（Unix）
│         ▼
├─ ASP.NET Core Kestrel（OB# 被托管的那一侧，跑 HTTP）
│
└─ Qt6 + CEF + obs-browser —— 全部来自 OBS 自带的运行时
```

页面方向：

* **页面 → 原生**：引导脚本把 `window.obsharp.post(json)` 变成
  `POST http://obsharp/__obsharp/message`；scheme handler 拦下来，转给当前浏览器。
  页面 `console.*` 和文档 `load` 事件也走同一个控制通道（JSON 里带保留字段
  `$obsharp`）。
* **原生 → 页面**：`obsharp_browser_post_message()` 把 JSON 用
  `executeJavaScript` 派发成页面里的 `CustomEvent("obsharp-message")`。

## 依赖从哪来（CEF / Qt6）

configure 时由 **`cmake/ObSharpDeps.cmake`** 负责：缺 CEF / Qt6 就按 obs-studio 自己的
清单下到 `lib/deps/`（已经就位就跳过）。版本 / 地址 / SHA-256 **全从清单里读**，不用
手动维护，跟着 `lib/obs-studio` 子模块走。

| 平台 | CEF（`dependencies.cef`） | Qt6（`dependencies.qt6`） |
| --- | --- | --- |
| Windows x64 | 自动下 `cef_binary_<版本>_windows_x64_v<修订>.zip`（≈286 MB） | 自动下 obs-deps 的 `windows-deps-qt6-<版本>-x64.zip`（≈249 MB） |
| Windows arm64 | 自动下 `cef_binary_<版本>_windows_arm64.zip`（≈279 MB；这份清单里没有修订号，文件名里就没有 `_v2`） | **两份**：目标那份 `…-arm64.zip`（≈243 MB），外加 x64 那份当 host tools |
| Linux（x86_64 / arm64） | 自动下 `cef_binary_<版本>_linux_{x86_64,aarch64}_v<修订>.tar.xz`（≈310 MB） | **用发行版包**，我们一份都不下（装 `qt6-base-dev` 就行） |
| macOS（arm64 / x86_64） | 按 `CMAKE_OSX_ARCHITECTURES` 下 `cef_binary_<版本>_macos_{arm64,x86_64}_v<修订>.tar.xz`（≈85/95 MB） | 自动下 obs-deps 的 universal 包 `macos-deps-qt6-<版本>-universal.tar.xz`（≈28 MB） |

Windows arm64 是「x64 主机编 arm64 目标」：ARM64 那份 Qt6 里的 `moc` / `rcc` / `uic` 是
arm64 可执行文件，在 x64 机器上跑不动，所以模块会把 x64 那份也下下来设成
`QT_HOST_PATH`（跟 obs-studio 的 `_handle_qt_cross_compile` 一个路子，两个包版本必须一致，
清单里本来就是同一个版本）。自己给的 Qt6 时它不去动你的设置，只提示一句要设 `QT_HOST_PATH`。

压缩包留在 `lib/deps/` 当缓存（下次 configure 命中就不重下；CI 里这份压缩包还会按
「系统 + 架构」缓存，见下面「CI」），下载后先校验 SHA-256 再解压。解压时会自动处理「包里多套一层目录」的情况（Linux 的 CEF tar.xz 就是这样），所以
`lib/deps/<包名>/` 下面直接就是 `include/`、`lib/` 这些。

> 文件名里带不带架构目录、iOS 那种多套一层的包，都按「解压到 `<目录>.unpack` → 找标志
> 文件（`include/cef_version.h` / `lib/cmake/Qt6/Qt6Config.cmake`）在第几层 → 搬上来」
> 处理，所以 obs-studio 哪天改了包结构，报错会明说标志文件没找到。

想用现成的 CEF / Qt6 就不下载：给 `-DCEF_ROOT_DIR=…`，或让 `CMAKE_PREFIX_PATH` 里
已经有 Qt6。整体关掉自动下载用 `-DOBSHARP_FETCH_DEPS=OFF`。

## 工具链（和 obs-studio 一样）

本库最后是要被 `LoadLibrary` / `dlopen` 进 **OBS 进程**的，跟 obs-browser 的 CEF、OBS
自己的 Qt6 共处一个地址空间。工具链差一代就可能在这些交界处踩到 ABI 问题（异常、类型
布局、CRT 堆）。所以三个平台都照 obs-studio 32.2.2 自己的 CI 来：

| 平台 | 镜像 | 编译器 / 工具链 | SDK / 依赖 |
| --- | --- | --- | --- |
| Windows x64 | `windows-2025-vs2026` | Visual Studio 18 2026（MSVC **v145**） | Windows SDK **10.0.26100.0** |
| Windows arm64 | 同上（x64 机器上交叉编 arm64） | 同上 + VS 的「C++ ARM64 生成工具」组件 | 同上 + ARM64 / x64 两份 Qt6 |
| Linux x86_64 | `ubuntu-26.04` | 发行版 GCC | 发行版 Qt6（`qt6-base-dev`） |
| Linux arm64 | `ubuntu-26.04-arm`（GitHub 原生的 arm64 runner） | 同上 | 同上（apt 装下来的就是 arm64 那份） |
| macOS arm64 / x86_64 | `macos-26`（arm64 runner，两个 RID 都跑在它上面，x86_64 是交叉编出来的） | Xcode 26（AppleClang） | obs-deps 的 universal Qt6 |

Windows 上对应的是预设 `win-x64`（x64）和 `win-arm64`（arm64，架构串
`ARM64,version=10.0.26100.0`），生成器 `Visual Studio 18 2026` 跟 obs-studio 的
`windows-x64` / `windows-arm64` 预设逐字一致。预设里的生成器名是写死的，没有 VS 2026 就
用不了预设，照「方式三」手动 configure，把生成器换成 `Visual Studio 17 2022`
（MSVC v143）即可。Linux / macOS 的预设名带架构（`linux-x64` / `linux-arm64` /
`osx-arm64` / `osx-x64`），按机器或目标架构选，预设条件里已经限定了 host 系统。

> obs-deps 那份 Qt6 其实是 MSVC 19.44（VS 2022 17.14）编出来的，所以拿 v143 编也不
> 算「错」——只是 OBS 本体用的是 v145，能用 VS 2026 就跟它完全对齐。

CRT 上有个坑：CEF 预编译的 `cef_dll_wrapper` 是**静态 CRT**（`/MT`），所以本库也必须
`/MT` / `/MTd`（`CMakeLists.txt` 里用 `CMAKE_MSVC_RUNTIME_LIBRARY` 设死），否则跨 DLL
传 `std::string`、释放对方内存会踩坏堆（obs-browser 也是这么干的）。obs-deps 的 Qt6 则是
**动态 CRT**（依赖 `MSVCP140.dll`），但这不冲突：跟 Qt 之间只传 Qt 自己的类型，各自管
各自的堆。

## 前置条件

| 需要什么 | 说明 |
| --- | --- |
| 编译器（见上表） | Windows 要 VS 2026（或 VS 2022）+ MSVC；Linux 要 `build-essential`；macOS 要 Xcode |
| CMake ≥ 3.20 | 构建系统；用 `win-x64` / `win-arm64` 预设的话还得是 VS 2026 自带的那份（4.2+，3.31 不认识这个生成器） |
| CEF 二进制 + Qt6 | 不用管：configure 时会自动下（见上）；Linux 的 Qt6 用发行版包，只会下 CEF |
| 能访问外网 | 第一次 configure 要下几百 MB；离线就自己备好 CEF / Qt6 并用 `CEF_ROOT_DIR` / `CMAKE_PREFIX_PATH` 指过去 |
| `lib/obs-studio` 子模块 | 提供 `cmake/finders/FindCEF.cmake` 和依赖清单（`git submodule update --init lib/obs-studio`） |

> 本库**不链接 libobs**：`blog` / `obs_get_module` / `obs_get_module_lib` /
> `obs_get_nix_platform` 都是运行时从已经加载的模块里取符号的（见
> `src/RuntimeHost.cpp`）。链接的只有 `Qt6::Widgets`、`CEF::Wrapper`、`CEF::Library`。

## 构建

产物固定落在仓库根的 `out/native/<arch>/<config>/`（`<arch>` 为 `x64` / `arm64` / `x86`
——由 `cmake/ObSharpDeps.cmake` 按生成器、`CMAKE_OSX_ARCHITECTURES` 或指针宽度算出来，
`<config>` 为 `Debug`/`Release`），跟托管项目 `OBSharp.Interop.Browser.csproj` 里复制原生库
的目标目录对得上。

### 方式一：在 Visual Studio 里直接构建（推荐给 Windows）

`OBSharp.Interop.Browser.Native.vcxproj` 是个 MSBuild 外壳，本身不带编译规则，只是把
Build / Clean / Rebuild 转成对 CMake 的调用，并把 `$(Configuration)` 和
`$(Platform)` 原样传下去。依赖由 CMake 那侧自动准备，装上 MSVC 后直接构建就行。

1. 把 `OBSharp.Interop.Browser.Native.vcxproj` 加进解决方案（或用「打开本地文件夹」）。
2. 选 Debug/Release × x64 / Win32 / ARM64 直接构建（ARM64 是在 x64 机器上交叉编，
   要装 VS 的「C++ ARM64 生成工具」）；第一次 configure 会下依赖到 `lib/deps/`。

> 已经有自己的 CEF / Qt6 时，给它设属性 `CEFRootDir` / `Qt6Dir`（或环境变量
> `CEF_ROOT_DIR` / `OBS_QT_DIR`），就不会触发下载。

> 这个 `.vcxproj` 故意**没有**放进 `OBSharp.slnx`：不想让只关心托管代码的人
> （`dotnet build`）因为缺 CEF/Qt6 而失败。

### 方式二：CMake 预设

预设不用手填路径，缺依赖会在 configure 时自动下到 `lib/deps/`：

```sh
# Windows x64（VS 2026）
cmake --preset win-x64
cmake --build --preset win-x64-release

# Windows arm64（在 x64 机器上交叉编，要装 VS 的「C++ ARM64 生成工具」；
# Qt6 会自动下 ARM64 + x64 两份，x64 那份当 host tools）
cmake --preset win-arm64
cmake --build --preset win-arm64-release

# Linux（先 apt install qt6-base-dev；预设按架构分，编哪份看机器）
cmake --preset linux-x64             # arm64 机器上换成 linux-arm64
cmake --build --preset linux-x64-release

# macOS
cmake --preset osx-arm64             # Intel 机器换成 osx-x64
cmake --build --preset osx-arm64-release
```

Windows 那两个预设还有 `-debug` 变体（`win-arm64-debug` 之类）。Linux / macOS
只有 Release，要 Debug 就在 configure 时自己加 `-DCMAKE_BUILD_TYPE=Debug`。

要换成自己的 CEF / Qt6，加 `-DCEF_ROOT_DIR=... -DCMAKE_PREFIX_PATH=...`。

### 方式三：单独配置（不经过仓库根）

本目录的 `CMakeLists.txt` 里也 include 了 `cmake/ObSharpDeps.cmake`，所以单独配置一样会
自动准备依赖：

```sh
cmake -S src/OBSharp.Interop.Browser.Native -B out/build/native
cmake --build out/build/native --config Release
```

## CI

`.github/workflows/build.yml` 是整条流水线，分四段（产物按段传）：

| 段 | job | 跑在 | 产物 artifact |
| --- | --- | --- | --- |
| 1. .NET 构建 | `dotnet` | `windows-2025-vs2026` | `managed-assemblies`（Runtime 那份托管程序集）、`build-output`（`Directory.Version.props` + `artifacts/` + `src/OBSharp.Interop/Generated/`） |
| 2. Native 矩阵 | `native` | 6 个 RID 各自挑 runner：`windows-2025-vs2026`（win-x64 / win-arm64）、`ubuntu-26.04`（linux-x64）、`ubuntu-26.04-arm`（linux-arm64）、`macos-26`（osx-x64 / osx-arm64） | `OBSharp.Interop.Browser.Native-<rid>`（原生库）、`OBSharp.Loader-<rid>`（Loader 的 publish） |
| 3. 收 Runtime | `runtime` | `ubuntu-26.04` | `runtime`：`any/` 放托管程序集，`native/<rid>/` 放 6 个 RID 的原生库 |
| 4. 组装插件 | `bundle` | `ubuntu-26.04`（6 个 RID 并行） | `OBSharp-<rid>`：摆好的插件目录（Windows / Linux 那份里面是 `OBSharp/`，macOS 是 `OBSharp.plugin/`） |

第 1 段的产物要喂给第 2 段，是因为 `src/OBSharp.Interop/Generated/` 是 git 忽略的
（`OBSharp.Interop.Generator` 的产出），干净 checkout 上不存在：`dotnet` job 跑一次生成器
（ClangSharp 解析 libobs 头文件），原生矩阵直接下载现成的，省掉 6 次解析。

同一个 artifact 里还带着 `Directory.Version.props`（GitVersion 按 git 历史算出来的版本号），
它会落到仓库根，`Directory.Build.props` 里那句 `Import` 管的就是它；本地没这个文件，版本号
就是 SDK 的默认 1.0.0。

第 2 段的 6 个 job 还会把 `lib/deps/` 里的压缩包按「系统 + 架构」缓存起来（`actions/cache`，
key 里带着 `lib/obs-studio` 的依赖清单和 `cmake/ObSharpDeps.cmake` 的哈希）。只缓存压缩包、
不缓存解压出来的目录：6 个 RID 的依赖解压后十几 GB，会把仓库 10 GB 的缓存额度顶穿，而解压
本身很快。压缩包下完还要过一次 SHA-256，所以缓存过期最多是多下一次，不会用错版本。

它同时也是「怎么编译」的活文档：每个 job 里的 `cmake --preset …` 和
`dotnet restore / build / publish …` 就是本地要敲的命令。公开头里的 `obsharp_*` 跟托管侧那些
`EntryPoint` 是不是一一对应，是本地拿 `dumpbin /exports`（Linux / macOS 上 `nm -D`）核的，
CI 里没有这一步。

托管侧各 RID 单独发布 Loader，因为 AOT 是「一个 RID 一份原生二进制」；Runtime 只发一份，
它是框架依赖的普通程序集，跟架构无关：

```sh
# 1. 绑定（dotnet job 的前半段）：生成器只要跑一次
dotnet tool restore
dotnet restore OBSharp.slnx --graph --artifacts-path artifacts
dotnet build src/OBSharp.Interop.Generator --graph --artifacts-path artifacts --configuration Release --framework net10.0 --no-restore
dotnet run --project src/OBSharp.Interop.Generator --artifacts-path artifacts --configuration Release --framework net10.0 --no-restore --no-build
# → src/OBSharp.Interop/Generated/

# 2. 原生库（每个 RID，见上面「本地怎么编」）
cmake --preset win-x64
cmake --build --preset win-x64-release
# → out/native/x64/Release/

# 3. Loader（每个 RID 三段）
dotnet restore src/OBSharp.Loader --runtime win-x64 --graph --artifacts-path artifacts
dotnet build src/OBSharp.Loader --runtime win-x64 --graph --artifacts-path artifacts --configuration Release --framework net10.0 --no-restore
dotnet publish src/OBSharp.Loader --runtime win-x64 --graph --artifacts-path artifacts --configuration Release --framework net10.0 --no-restore --no-build
# → artifacts/publish/OBSharp.Loader/release_net10.0_win-x64/

# 4. Runtime：跨平台那一份（不带 -r），跟在整解决方案的 build 后面
dotnet build OBSharp.slnx --graph --artifacts-path artifacts --configuration Release --framework net10.0 --no-restore
dotnet publish src/OBSharp.Runtime --graph --artifacts-path artifacts --configuration Release --framework net10.0 --no-restore --no-build
# → artifacts/publish/OBSharp.Runtime/release_net10.0/
```

* `-f/--framework net10.0` 不能省：`Directory.Build.props` 里写的是复数 `TargetFrameworks`
  （现在是 net10.0 + net11.0），不带 `-f` 的 build / publish 会报 `NETSDK1129`。
* 产物一律落 `artifacts/`（`--artifacts-path artifacts`），CI 上传 artifact 的路径就是照
  `artifacts/{bin,obj,publish}/<工程>/<配置>_<tfm>[_<rid>]/` 拼的。
* restore 没有 `-c` / `--framework` 开关（写了会被 MSBuild 当未知开关报 MSB1001）；仓库是多目标，
  `dotnet restore` 把两个 TFM 都还原，发哪份由后面的 `-f net10.0` 定。**每个 RID 那份必须带
  `--runtime <rid>` 还原**，否则 `--no-restore` 的构建会 `NETSDK1047`（AssetsFile 里没有
  `net10.0/<rid>` 这个目标）。
* **`OBSharp.Loader` 是 NativeAOT**，所以发布机器上得有本地工具链：Windows 要 MSVC 的
  `link.exe`（进 `vcvarsall x64` / `x64_arm64` 之后的环境）；Linux 要 `clang` + `zlib1g-dev`；
  macOS 要 Xcode。跨架构（`win-x64` → `win-arm64`、`osx-arm64` → `osx-x64`）可以，跨系统不行。
* `OBSharp.Runtime` 是**框架依赖**的普通程序集（不带 `-r` 发出来就是），跑的时候由
  Loader 用 hostfxr 拉起来，机器上要有 .NET 10 运行时。
* 原生库按 RID 自动找：`OBSharp.Interop.Browser.csproj` 从 `out/native/<arch>/<Configuration>` 复制，
  `<arch>` 跟着 `RuntimeIdentifier` 走（`win-arm64` → `arm64`），也可以 `-p:OBSharpNativeArch=`
  显式覆盖。

### 插件目录怎么摆

artifact 里那份是照着 OBS 的加载规则摆好的（`libobs/obs-module.c` 找模块、
`obs-{windows,nix,cocoa}.c` 给搜索路径）：

```text
Windows / Linux：                       macOS：
OBSharp/                                OBSharp.plugin/
  bin/64bit/OBSharp.{dll,so}  ← 宿主      Contents/MacOS/OBSharp          ← 宿主（去掉 .dylib 后缀）
  bin/any/OBSharp.Runtime.dll ← 托管      Contents/any/OBSharp.Runtime.dll ← 托管
  bin/any/OBSharp.Interop…                Contents/any/OBSharp.Interop…
  data/appsettings.json 等                Contents/Resources/appsettings.json 等
  bin/64bit/OBSharp.Interop.Browser.Native.*      Contents/MacOS/OBSharp.Interop.Browser.Native.*
```

几个容易踩的点：

* `bin` 下面那个目录名**不跟架构走**：OBS 在 Windows / Linux 上写死的就叫 `64bit`
  （`cmake/windows/defaults.cmake` 的 `OBS_PLUGIN_DESTINATION`、`obs-windows.c` 里的
  `"../../obs-plugins/64bit"`），arm64 也是这个名字。
* `bin/64bit` 和 `bin/any` 必须是兄弟目录：Loader 是拿模块自己的路径往上退两级算
  `…/bin/any` 的，托管侧就住在那儿。
* `OBSharp.Interop.Browser.Native` 两处都放：托管侧的 `OBSharp.Interop.Browser.dll` 在 `any` 里
  按 `assembly.Location` 找同目录的原生库；AOT 出来的 Loader 里 `Assembly.Location` 是空的，
  只能靠系统搜索路径找自己旁边那份（Windows 上 OBS 用
  `LOAD_WITH_ALTERED_SEARCH_PATH` 加载插件，Linux / macOS 上是 NativeAOT 给的
  `$ORIGIN` rpath）。

## 托管侧怎么用

托管绑定在单独一个工程 `src/OBSharp.Interop.Browser/` 里，命名空间
`OBSharp.Interop.Browser`，公开 `OBSharpNative`、`CefBrowser`、`BrowserOptions`、
`BrowserCallbacks` 这几个类型。`OBSharp.SDK` 引用着它，所以插件只要引用 SDK 就能拿到
这几个类型（宿主当然也会把程序集和原生库一起发布）；不碰浏览器的话，这份绑定会跟着
`OBSharp.SDK.targets` 从插件输出里被去掉。

`OBSharp.Interop.Browser.csproj` 会在
`out/native/$(OBSharpNativeArch)/$(Configuration)` 存在时把原生库复制到输出目录
（也可以用环境变量 `OBSHARP_NATIVE_DIR` 指到别处）；`OBSharpNativeArch` 默认 `x64`，
`dotnet publish -r win-arm64` 这类会按 RID 自动切成 `arm64`。

原生库的名字在 Unix 上带 `lib` 前缀（`libOBSharp.Interop.Browser.Native.so` / `.dylib`），Windows
上是 `OBSharp.Interop.Browser.Native.dll` —— `NativeLibraryLoader` 和 `OBSharp.Interop.Browser.csproj` 都
按这个名字找，CMake 在 Unix 上默认就加前缀，所以 `CMakeLists.txt` 里**不要**把 `PREFIX`
设成空的。

```csharp
using OBSharp.Interop.Browser;

// 1. 解析 obs-browser、开始等 CEF；幂等，可在任意线程调用。
OBSharpNative.Initialize("MyPlugin");

// 2. 等 CEF 就绪（IsAvailable 为 true）后再建浏览器。
using var browser = CefBrowser.Create(new BrowserOptions
{
    Endpoint = "obsharp-myplugin",                 // Kestrel 监听的命名管道名 / UDS 路径
    Url = "http://obsharp/",                        // 省略时按 <scheme>://<domain>/
    StartupScript = "console.log('from native')",
    Callbacks = new BrowserCallbacks
    {
        TitleChanged  = t => Console.WriteLine($"title: {t}"),
        Message       = json => Console.WriteLine($"page: {json}"),
        ConsoleMessage = (level, msg, src, line) => Console.WriteLine($"[{level}] {msg}"),
    },
});

// 3. 把 Widget 塞进 dock / setParent —— 但不要自己 delete，Dispose 时会一起销毁。
//    Widget 就是个 QWidget*：交给 obs_frontend_add_dock_by_id()，或者自己 setParent。
nint widget = browser.Widget;

browser.PostMessage("""{"hello":"page"}""");
```

## C ABI 命名

* **模块级**（库自己的初始化 / 版本 / 线程辅助）：前缀就是库名，`obsharp_browser_native_*`，
  宏 `OBSHARP_BROWSER_NATIVE_{API,CALL,BUILD,ABI_VERSION}`。
* **对象级**（一个浏览器实例上的操作）：`obsharp_browser_*`，句柄类型 `obsharp_browser`。
* 返回码、枚举走短名字（`OBSHARP_ERR_*` / `obsharp_result`），不挂前缀。

## 线程规则

* 除了 `obsharp_browser_native_initialize` / `obsharp_browser_native_is_available` /
  `obsharp_browser_native_abi_version` / `obsharp_browser_native_version` /
  `obsharp_browser_native_last_error` / `obsharp_browser_native_is_ui_thread`，其余函数都会碰 Qt，
  **必须跑在 OBS 的 Qt 主线程**。
* 不在主线程时，托管层会自动用 `OBSharpNative.InvokeUi` 切过去（已经在主线程时是
  直调）。原生侧也提供 `obsharp_browser_native_invoke_ui()` 给 C 调用方。
* 回调（title / url / load-end / console / message）**全部在 Qt 主线程上**触发。
  scheme handler 的 message 回调实际跑在 CEF 的 UI 线程（Windows/Linux 上跟 Qt 线程
  不是同一个），原生侧会用 `QMetaObject::invokeMethod(..., Qt::QueuedConnection)`
  切回 Qt 线程再派发。
* `obsharp_browser_destroy()` 会自动切到 Qt 线程（同步 `BlockingQueuedConnection`）后再
  销毁，避免泄漏；托管 `CefBrowser` 的终结器在 `Environment.HasShutdownStarted`
  时会跳过原生销毁，避免进程退出时把终结器线程卡死。

## 已知限制

* **WebSocket 不走 scheme handler**。CEF 的 `CefSchemeHandlerFactory` 只覆盖普通
  请求；页面的 WebSocket 连接不会经过我们的 Kestrel 客户端。需要双向实时通道时用
  `window.obsharp.post()` / `CustomEvent("obsharp-message")` 这条消息通道，或者让
  Kestrel 另开一个真实的 TCP 监听。
* **同一个 scheme+host 只能有一个 factory**。scheme handler 是全局的，本库用
  `g_activeBrowser` 把消息派给当前浏览器；同进程里同时开多个浏览器时，页面消息会被
  路由到最近一次创建/激活的那个。
* **load-end / console 事件是合成的**。`BROWSER_PANEL_VERSION` 3 的面板接口没有暴露
  `CefLoadHandler` / `CefDisplayHandler`，所以这两个事件由页面里的引导脚本上报：
  `load-end` 的 status 恒为 200、error 恒为 0；`console` 的 `source` 是当前 URL、
  `line` 恒为 0。只在注册了 `endpoint` 时才有。
* **`CEF::Wrapper` 是静态库**。它会被静态链进本库，于是同一进程里就出现了第二份
  `cef_dll_wrapper`（obs-browser 自己那份也在）。CEF 的 wrapper 主要靠全局对象和
  `CefInitialize` 之后的单例，重复静态副本在目前用法下可用，但值得留意；如果将来出现
  符号/初始化异常，优先怀疑这里。
* **`background_color` 是尽力而为**：CEF 用无窗口渲染，页面自己画的背景会盖住
  Widget 底色；`transparent` 优先于 `background_color`。
* **Wayland 下不可用**：跟 obs-browser 一致，`obs_get_nix_platform()` 返回
  `OBS_NIX_PLATFORM_WAYLAND` 时浏览器面板被禁用。
* **`obsharp_browser_options.size`** 必须填 `sizeof(obsharp_browser_options)`；校验是
  `size < sizeof(...)` 才算非法，留了将来往后追加可选字段的余地。
* **Linux / macOS 还没在真机上编过**。Windows 那几条路都实际跑过（VS 2026、VS 2022，
  导出符号也对着公开头核过，都在本地做的）；Linux / macOS 目前只有平台适配代码，
  第一次真编是 GitHub CI 干的活，CI 红的话请看日志。
* **Windows arm64 要装 VS 的「C++ ARM64 生成工具」组件**，而且是「x64 主机编 arm64
  目标」：Qt6 会下两份（ARM64 那份 + x64 那份当 `QT_HOST_PATH`），CEF 下 arm64 那份，
  磁盘占用也跟着翻倍。没装那个组件时 configure 会过、编译才炸，看到 `cl.exe` 找不到
  先回来看这条。（CI 的 `windows-2025-vs2026` 镜像里是带的。）
* **macOS 上「能不能被 OBS 加载」没验证**。链接 CEF 用的是 framework 自己的 install name
  （`@rpath/…`），本库并没有自己设 `LC_RPATH`；如果 OBS 进程里已经加载了同一个
  framework，dyld 按 install name 复用应该没问题，但这条路径没在真机上验过。

## 目录结构

| 路径 | 内容 |
| --- | --- |
| `include/OBSharp.Interop.Browser.Native.h` | 公开 C ABI；托管绑定照着它写，改一边要同步另一边 |
| `src/OBSharp.Interop.Browser.Native.cpp` | `obsharp_*` 的实现：线程切换、scheme 注册、各浏览器操作 |
| `src/RuntimeHost.{h,cpp}` | 运行时解析 obs-browser 模块、取 `blog` / `obs_*` 符号 |
| `src/HostApi.{h,cpp}` | 复刻 obs-browser 的面板接口（`QCef` / `QCefWidget` 虚表） |
| `src/BrowserWidget.{h,cpp}` | `QCefWidget` 子类 + 引导脚本（`$obsharp` 控制通道） |
| `src/SchemeHandler.{h,cpp}` | `http://<scheme-host>/*` → Kestrel |
| `src/KestrelClient.{h,cpp}` | 命名管道 / UDS 上的最小 HTTP/1.1 客户端 |
| `src/Log.{h,cpp}` | 转发到 libobs 的 `blog` |
| `CMakeLists.txt` | CMake 构建 |
| `OBSharp.Interop.Browser.Native.vcxproj` | MSBuild 外壳，转调 CMake |
| `../../src/OBSharp.Interop.Browser/` | 托管绑定（`OBSharpNative` / `CefBrowser` 等） |
| `../../CMakePresets.json` | 各平台的 CMake 预设（CI 用的就是这些） |
| `../../Directory.Build.props` | 公共 MSBuild 属性；旁边有 `Directory.Version.props`（CI 生成、不入库）就 import 进来，版本号从这儿进 |
| `../../.github/workflows/build.yml` | CI：.NET 构建 → 6 个 RID 的原生矩阵（原生库 + Loader）→ 收集成跨平台 Runtime 包 → 组装插件目录 |
| `../../scripts/Build-DevSymbolicLink.ps1` | 开发用：在 `out/plugin/<Rid>` 摆出上面那份目录结构（模块目录是真实文件，`bin/any`、`data` 是目录链接，改托管侧 / 数据不用重摆），再往 OBS 的插件目录里放一个目录链接指过来（Windows 用 Junction，Linux / macOS 用符号链接）—— 不用往 ProgramData / 安装目录里拷文件 |
| `../../cmake/ObSharpDeps.cmake` | 缺 CEF / Qt6 时按 obs-studio 清单下载 |
| `../../lib/deps/` | 下载的 CEF / Qt6（不入库；压缩包兼作本地与 CI 的缓存） |

## 许可证

本仓库是 MIT。但 OBS Studio 与 obs-browser 是 GPL-2.0：本库在运行时依赖它们、并复刻
了 obs-browser 的面板接口（没有直接 `#include` 它的 GPL 头文件）。分发时请遵守
相应许可证。
