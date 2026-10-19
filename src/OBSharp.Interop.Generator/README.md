# OBSharp.Interop.Generator

一个构建期命令行工具，用来把整个 libobs 互操作层（`libobs/`、`deps/w32-pthreads/`，以及
`frontend/api/obs-frontend-api.h` 那套前端 API）生成到 `src/OBSharp.Interop/Generated/`。
运行时不依赖它——只有当你改了 OBS 头文件或生成规则时，才需要手动跑一次。

> **注意**：本项目（以及它生成的所有绑定代码）完全由 AI 编写。提交前请先阅读代码，
> 并 review 重新生成后的 diff。

## 前置条件

| 需要什么 | 为什么 |
| --- | --- |
| `global.json` 里指定的 .NET SDK | 编译和运行本生成器 |
| `lib/obs-studio` 子模块 | 要被解析的 C 头文件都在这里 |
| `ClangSharpPInvokeGenerator` 本地工具 | 真正执行 C → C# 的那一步 |

```sh
git submodule update --init --recursive   # 拉取 lib/obs-studio
dotnet tool restore                       # 安装 ClangSharpPInvokeGenerator（版本锁在根目录 dotnet-tools.json）
```

## 用法

```sh
dotnet run --project src/OBSharp.Interop.Generator
```

可选参数（必须放在 `--` 之后，`dotnet run` 才会把它们透传给生成器）：

| 参数 | 作用 |
| --- | --- |
| `-SkipRaw` | 复用上一次运行留下的 ClangSharp 暂存产物，跳过重新解析头文件（需要之前至少完整跑过一次） |
| `-SkipBuild` | 只生成，不编译 `OBSharp.Interop` 做验证 |

```sh
dotnet run --project src/OBSharp.Interop.Generator -- -SkipBuild
```

工具会从自身程序集所在目录向上查找 `OBSharp.slnx` 来确定仓库根目录，所以在任何目录下
都能启动。它对仓库唯一写入的位置是 `src/OBSharp.Interop/Generated/`；ClangSharp 的中间
产物、以及它需要用到的 C 头文件，全都写在系统临时目录
（Windows 下为 `%TEMP%/OBSharp-interop-staging`）。

## 产物

`src/OBSharp.Interop/Generated/` 下的所有内容都被 git 忽略，且每次运行都会完整重新生成
——**不要手改**。

| 目录 | 内容 |
| --- | --- |
| `Raw/` | 每个类型一个文件，外加 `Obs.cs`（`-m` 指定的方法类）；最直接的 `LibraryImport`/`DllImport` 绑定，已归一化为地道的 C# |
| `Wrappers/` | 按前缀分组的包装类，带 string/bool 封送、`SafeHandle` 参数/返回值，以及自动生成的回调委托。分组名本身是个 C 类型时用扩展类（`ObsSourceExtensions` + `extension(ObsSource self)`），否则该分组有自己的静态容器类（`ObsFrontend`、`Graphics`、`Os`、`Base`、`Pthread`……），成员直接是类里的 `public static` 方法 |
| `Handles/` | 每种“拥有所有权”的原生类型一个 `SafeHandle` 子类（释放函数自动识别） |

`obs` 是 libobs 的模块名；前端 API 在单独的 DLL 里（`obs-frontend-api.dll`），所以那 98 个
`obs_frontend_*` 入口写的是 `[LibraryImport("obs-frontend-api")]`。模块名由 `Pipeline.cs` 的
`LibraryNameFor()` 按函数前缀决定——ClangSharp 的 `-l` 一次只能给一个模块名。

友好层的调用示例：

```csharp
using OBSharp.Interop;

// 工厂直接给 SafeHandle：Dispose 时调用 obs_data_release()
using var data = ObsDataExtensions.Create();
string? json = data.GetJson();              // -> obs_data_get_json(self)

// 会 addref / 转移所有权的查询同理
using var source = ObsExtensions.GetSourceByName("Desktop Audio");

// 借用的指针保持裸指针——它本来就不归你释放
VideoOutput* video = ObsExtensions.GetVideo();

// 返回新引用的 T** 出参也给成 SafeHandle
if (ConfigDataExtensions.Open(out var config, "cfg.ini", ConfigOpenType.CONFIG_OPEN_EXISTING) == 0)
{
    using (config)
    {
        // ...
    }
}

// 想自己接管裸指针时也可以显式声明所有权：FromBorrowed 的 Dispose 什么都不做
using var borrowed = ObsSourceHandle.FromBorrowed((nint)rawSource);

// 辅助方法挂在各分组自己的类上
ObsExtensions.Log(4 /* LOG_WARNING */, "50% done");   // -> blog()，已帮你转义 '%'

// 没有对应 C 类型的分组各自是一个静态容器类（不然它们全都会挤在 Obs 上）
Os.GetPathExtension(@"C:\path\file.txt");
Graphics.TexrenderCreate();
// 前端 API 也一样（它们来自 obs-frontend-api.dll，不属于 libobs）
ObsFrontend.StreamingStart();
ObsFrontend.SetCurrentProfile("My Profile");
// 带接收者的成员仍落在各自类型的扩展类上，例如 ObsFrontendSourceListExtensions
```

### 哪些指针该包成 SafeHandle

判据是**所有权**，不是类型：

- **函数把新引用交给你**（`*_create*`、`obs_get_*_by_name`、`*_get_ref`、
  `obs_weak_*_get_*`、`*_get_settings`、`obs_data_get_obj`……）——友好层直接返回
  `XxxHandle`，`Dispose` 调用对应的 release 函数。
- **函数只是借用**（`gs_get_context`、`obs_get_video`、`obs_output_get_service`……）——
  友好层保持裸指针。这些指针不归调用方所有，包成 owning 的 `SafeHandle` 会在 GC
  回收时释放掉不属于自己的内存。
- **`T**` 出参**：确认是“返回新引用”的（`config_open`、`video_output_open`……）映射成
  `out XxxHandle?`；其余（`obs_data_item_*` 这类 in/out 槽位）保持 `ref nint` 裸指针。
- **前端 API**：`obs_frontend_get_current_scene` / `_current_transition` /
  `_current_preview_scene` / `_streaming_output` / `_recording_output` /
  `_replay_buffer_output` / `_virtualcam_output` 和 `obs_frontend_add_canvas` 在
  `frontend/OBSStudioAPI.cpp` 里都是 `obs_*_get_ref`，所以包成 `SafeHandle`；
  `obs_frontend_get_profile_config` / `_app_config` / `_user_config` / `_global_config` /
  `_streaming_service` 返回的是借用指针，保持裸指针。这些成员都在 `ObsFrontend` 上。

哪些函数算“给了新引用”，写在 `Pipeline.Wrappers.cs` 的 `OwnedReturns` /
`OwnedOutParams` 两张表里，每条都注明依据（libobs 头文件注释、实现里的
`*_addref`，等等）。**没进表的函数一律保持裸指针**：漏判最多是泄漏（还可以用
`FromOwned` 手动接管），误判就是 use-after-free。要往里加条目，请先确认 OBS 侧
确实转移了所有权。

## 流水线阶段

1. **ClangSharp**（`Pipeline.ClangSharp.cs`）——通过本地工具解析一个伞形头文件。这个头
   文件（以及 CMake 那个 `obsconfig.h` 的替身）不在仓库里，而是每次运行时由生成器写进
   临时暂存目录，所以仓库里只有 C#。先用一次探测（probe）把实际访问到的仓库头文件记录到
   日志，正式那一遍再遍历所有这些头文件。
2. **Raw 归一化**（`Pipeline.RawPublish.cs`）——把暂存产物整理成地道 C#：
   `snake_case` 类型 → PascalCase（声明**和**引用一起改）、`IntPtr` → `nint`、
   `sbyte` → `byte`、C 的 `long`/`unsigned long` → `CLong`/`CULong`（Windows LLP64 与
   Unix LP64 的原生宽度不一样）、在 AOT 源生成器能封送的前提下把 `DllImport` 换成
   `LibraryImport`、补 `#nullable enable`，等等。
3. **Wrappers**（`Pipeline.Wrappers.cs`）——解析 Raw 里的函数签名，生成包装类、回调委托和
   `SafeHandle` 类；再按所有权表决定哪些指针返回值/`T**` 出参包成 `SafeHandle`。没有接收者
   的静态成员落到分组自己的类上：分组名是 C 类型就用它的扩展类，否则用同名容器类
   （`IsContainerGroup`）；`obs_` 本体是模块类，成员留在 `ObsExtensions`。
4. **构建验证**（`Pipeline.BuildVerify.cs`）——对 `OBSharp.Interop` 跑 `dotnet build`；
   编译器报缺的系统头类型（`FILE`、`stat`、`timespec`、`__m128` 等）会被自动 shim 进
   `Generated/Raw/Compat.cs`，然后重试编译（最多三轮）。

## 注意事项

- 前端 API 的 `char *` 返回值（`obs_frontend_get_current_profile` 等）在 OBS 侧是
  `bstrdup`，文档要求调用方 `bfree`；绑定里它们保持裸指针 `byte*`，不走 string 封送，
  释放责任仍然在调用方。
- libobs 的 `const char *` 返回值（`obs_get_module_binary_path`、`obs_source_get_name`
  等）是**借用**指针：内存归 libobs（`bstrdup` 出来挂在结构体上，由它自己释放）。
  包装层给它们标 `[return: MarshalUsing(typeof(OBSStringMarshaller))]`——只
  `Encoding.UTF8` 复制一份，不释放原内存。默认的 string marshaller 会把返回值当成
  `CoTaskMemAlloc` 出来的内存、复制完再 `FreeCoTaskMem`，那就是释放别人的指针：堆损坏
  （`0xC0000374`，ntdll 直接 fast-fail，宿主进程瞬间消失）。marshaller 是手写文件
  `src/OBSharp.Interop/OBSStringMarshaller.cs`，不在 `Generated/` 里，不会被重新生成覆盖。
- 输出是确定性的：跑两遍结果逐字节一致，所以重新生成可以直接当成普通 diff 来 review。
- 实际只能在 **Windows** 上跑：include 路径是用反斜杠传的，那个自动生成的 `obsconfig.h`
  也提供的是 MSVC 风格的默认值。但**生成出来的绑定**本身是跨平台的。
- 少数 CRT/系统类型是近似处理而非精确绑定（`wchar_t`、`stat`、`timespec`、`_iobuf`、
  `__m128`），原因写在 `Pipeline.*.cs` 的注释里。
- 流水线里用到的正则全部是编译期生成的（`[GeneratedRegex]`），所以工具本身没有运行时
  正则初始化开销。
