# OBSharp

让 C# 插件跑进 OBS Studio 的框架：C++ 侧只做转发，业务逻辑全在托管侧。
插件用 [OBSharp.SDK](src/OBSharp.SDK) 写，宿主负责加载、生命周期和 UI（浏览器面板、窗口、文件对话框）。

## 仓库结构

| 目录 | 内容 |
| --- | --- |
| [src/OBSharp.Loader](src/OBSharp.Loader) | 原生入口（NativeAOT），被 OBS 当模块加载，负责点火托管运行时 |
| [src/OBSharp.Runtime](src/OBSharp.Runtime) | 插件宿主：发现、加载、卸载、invoke 生命周期，进程/日志基建 |
| [src/OBSharp.SDK](src/OBSharp.SDK) | 插件开发 SDK（`ObsPlugin`、日志、libobs 绑定入口） |
| [src/OBSharp.PluginManager](src/OBSharp.PluginManager) | 插件管理器（OBS 里的工具菜单窗口） |
| [src/OBSharp.Interop](src/OBSharp.Interop) | 托管绑定与 UI 层：libobs 绑定、Qt/CEF 薄封装、WebView / Window / FilePicker |
| [src/OBSharp.Interop.Native](src/OBSharp.Interop.Native) | C++ 转发库（Qt / CEF 桥），零判断、纯 ABI |
| [src/OBSharp.Interop.Generator](src/OBSharp.Interop.Generator) | C/C++ 头 → C# 绑定生成器（ClangSharp 库模式，产 `*.g.cs`） |
| `lib/` | 子模块（obs-studio、obs-browser）与依赖（Qt6、CEF，按 obs-deps 清单拉） |
| [scripts/Build-DevDist.ps1](scripts/Build-DevDist.ps1) | 开发分发构建：原生库 + 绑定生成 + 打包 |
| [.github/workflows/build.yml](.github/workflows/build.yml) | CI |

## 架构

```text
OBS 进程
├── OBSharp（NativeAOT 入口，Loader）
│     └── 托管运行时（Runtime）
│           ├── 插件们（SDK 写的 C# 程序集）
│           └── Interop
│                 ├── Generated/native + Generated/libobs   ← 生成器产出的绑定
│                 ├── Abi/                                  ← 线程封送、日志、借用句柄包装
│                 └── UI/（WebView、Window、FilePicker…）
│                       └── P/Invoke ▶ OBSharp.Interop.Native（C++，Qt/CEF 转发）
└── libobs / obs-browser（OBS 自己的模块）
```

## 构建

前提：

- .NET SDK（版本见 [global.json](global.json)），多目标 `net10.0;net11.0`
- 子模块：`git submodule update --init --recursive`
- Qt6 / CEF 依赖在 `lib/deps/`（缺了按 obs-deps 清单自动下载，见 [OBSharp.Interop.Native](src/OBSharp.Interop.Native)）
- C++ 构建走 CMake（[CMakePresets.json](CMakePresets.json)），Windows 另有 vcxproj 外壳

托管部分：

```powershell
dotnet build OBSharp.slnx
```

生成绑定（`Generated/` 是 gitignore 的，编译 Interop 前要先生成）：

```powershell
dotnet run --project src/OBSharp.Interop.Generator -- `
    --input <头文件...> --lib <库名> --namespace OBSharp.Interop --output <输出目录>
```

两个目标（native 与 libobs）的完整命令、类型映射、所有权规则见
[OBSharp.Interop.Generator](src/OBSharp.Interop.Generator)。

开发分发（原生库 + 绑定 + 打包，一把梭）：

```powershell
./scripts/Build-DevDist.ps1            # 参数：-Rid -Configuration -Framework -SkipNative -SkipGenerator …
```

## 文档索引

- [插件怎么写](src/OBSharp.SDK)
- [绑定生成器](src/OBSharp.Interop.Generator)（`--input/--lib/--namespace/--output`、映射表、降级语义）
- [原生转发库](src/OBSharp.Interop.Native)（职责边界、依赖下载、确定性构建）
- [插件管理器](src/OBSharp.PluginManager)
