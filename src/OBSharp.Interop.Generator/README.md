# OBSharp.Interop.Generator

C/C++ 头文件 → C# 薄封装绑定生成器（`*.g.cs`）。单一事实来源是头文件的 `extern "C"` 导出面：
头文件加一个函数 → 重新生成 → C# 侧就有了。ClangSharp **库模式**自写解析/发射，
不走 ClangSharpPInvokeGenerator。

```text
dotnet run --project src/OBSharp.Interop.Generator -- \
    --input src/OBSharp.Interop.Native/src/QT/widget.h \
    --lib OBSharp.Interop.Native \
    --namespace OBSharp.Interop \
    --output src/OBSharp.Interop/Generated/native
```

## 命令行

| 选项 | 必填 | 说明 |
| --- | --- | --- |
| `--input` / `-i` | ✓ | 输入头文件，可多个、**支持通配符**（`--input "lib/obs-studio/libobs/util/*.h"`）。**只收集声明位置落在这些头里的符号**；被 include 的 Qt/CEF/obs 声明参与解析但不产出 |
| `--lib` / `-l` | ✓ | `LibraryImport` 的库名（如 `obs`、`OBSharp.Interop.Native`） |
| `--lib-prefix` | | 函数名前缀 → 库名映射（`obs_frontend_=obs-frontend-api`），匹配不上走 `--lib` |
| `--namespace` / `-n` | ✓ | 生成代码的命名空间 |
| `--output` / `-o` | ✓ | 输出目录；每次运行先清掉旧的 `*.g.cs` 再全量重写 |
| `--include` | | 附加 include 目录（`-I`），可多个 |
| `--msvc-include` | | 钉死 MSVC STL 工具集目录（cc1 的 `-isystem`，排在默认 MSVC STL 之前） |
| `--define` / `-D` | | 预处理器宏 |
| `--extra-arg` | | 原样透传给 libclang 的参数 |
| `--traverse` | | 符号名前缀过滤（支持尾部 `*`）；默认不滤 |
| `--type-alias` | | C++ 记录 → .NET 等价类型（`name=Type`），内置 `vec2/vec3/vec4/matrix3/matrix4` → `System.Numerics` |
| `--disable-runtime-marshalling` | | 可选形态：假定程序集带 `[assembly: DisableRuntimeMarshalling]`。委托全 blittable（`byte*` 字符串、指针出参），System.Numerics 等别名类型在 raw 层直走 `in/ref`。**默认关闭**——默认形态无需该特性 |
| `--std` | | C++ 标准，默认 `c++20` |
| `--string-marshaller` | | 返回值字符串 marshaller 全名，默认 `global::OBSharp.Interop.OBSStringMarshaller` |

## 容错与降级（失败不拖垮整个运行）

- **跳过函数**：封送不了的签名（varargs、`va_list`、匿名函数指针）跳过并告警，stderr 列出原因
- **结构体降级**：含不支持字段（复杂数组、union、匿名嵌套）的布局结构体不产出，引用它的指针
  参数退化为 `nint` 并告警；标量定长数组映射 `fixed` 缓冲
- **二级指针**：`T**` / `const char**` 配 `size_t` → 借用数组；配不上 → `ref nint`；
  函数指针 typedef 再取指针（`log_handler_t*`）→ `ref` 委托；返回裸 `T**` → `nint`

## 产物形状

每个句柄/分组类一个 `.g.cs`，另有 `Enums.g.cs`、`Delegates.g.cs`、`Structs.g.cs`。
类都是 `partial`，方便手写层用 partial 续成员。

```csharp
public unsafe partial class QWidget : QObject
{
    internal QWidget(nint ptr, bool ownsHandle) : base(ptr, ownsHandle) { }

    /// <summary>
    /// 空句柄（原生 NULL）。可选句柄参数用它表示「无」。
    /// </summary>
    public static QWidget Null => field ??= new(0, false);

    /// <summary>
    /// C ABI: <c>obsharp_widget_create</c>
    /// </summary>
    public static QWidget Create(QWidget? parent)
        => new(obsharp_widget_create(parent ?? QWidget.Null), true); // 拥有句柄：Dispose 时释放

    public void SetWindowTitle(string? title)
        => obsharp_widget_set_window_title(this, title);

    public QObject? InstallEventFilter(ObsharpWidgetFilterEventCallback? callback, nint user)
    {
        nint p = obsharp_widget_install_event_filter(this, callback, user);
        return p == 0 ? null : new(p, false); // 借用句柄：不负责释放
    }

    [LibraryImport("OBSharp.Interop.Native", EntryPoint = "obsharp_widget_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial nint obsharp_widget_create(QWidget parent);
    ...
}
```

- 方法参数名是 camelCase（`Create(QWidget? parent)`）；结构体字段是 Pascal（`RequestHeader.Name`）
- 原始 `LibraryImport` 是类内 `private static partial`，公开面只有薄方法
- 返回指针一律 raw 回 `nint`、薄层 `new` 包装（SafeHandle 不能当返回值封送）
- 参数直接用 SafeHandle 类型（source-gen 调用期间自动加引用）。**raw 层句柄参数一律非空**——
  SafeHandle 封送器（`SafeHandleMarshaller.FromManaged`）不接受 null（会 NRE）；公开面照旧
  `X?` 可空，薄层转发时 `?? Xxx.Null` 哨兵兜底（`public static Xxx Null => field ??= new(0, false);`，
  封送成 0 即原生 NULL）
- 句柄类按 C++ 继承关系生成（`QCefWidget : QWidget`、`QFileDialog : QWidget`、
  `QWidget : QObject`），`Create(QWidget? parent)` 才能传派生类型
- **相等性**：继承链根类重写 `Equals`/`GetHashCode`/`==`/`!=`，按 handle 指针判等
- **所有权**：`*_create` 系且存在对应销毁函数 → `ownsHandle: true`（`Dispose` 时调
  `*_destroy`）；`get_*`/`find_*`/`active_*` → 借用（`ownsHandle: false`，产物带注释）。
  例外进入 `Naming.cs` 的表
- `Close()` 这类与 `SafeHandle` 撞名的薄方法用 `new` 显式隐藏

## 类型映射

| C++ | C# |
| --- | --- |
| `const char*` 入参 | `string?` + `LPUTF8Str` |
| `const char*` 返回 | `string?` + `OBSStringMarshaller`（只复制不释放） |
| `const char**` + `size_t*` 返回 | `string[]`（当场拷贝） |
| `const char**` + `size_t` 入参 | `string[]`（调用方拷贝进非托管后释放） |
| `T**` + `size_t*` 返回 | `T?[]`（元素全是借用句柄） |
| `bool` | `[MarshalAs(U1)] bool`（C++ bool 是 1 字节） |
| `int*` 出参 | `out int` |
| `size_t` / `uintptr_t` | `nuint` |
| `long` / `unsigned long` | `CLong` / `CULong`（`System.Runtime.InteropServices`，位宽随平台） |
| `long long` | `long` |
| `vec2` / `vec3` / `vec4` | `Vector2` / `Vector3` / `Vector4`（`System.Numerics`，不产自定义 struct） |
| `matrix3` / `matrix4` | `Matrix3x2` / `Matrix4x4`（`System.Numerics`） |
| `void*` | `nint` |
| Qt/CEF/自有枚举 | 按 C++ 作用域嵌套的 C# enum（`Qt.WindowType`、`QFileDialog.Option`…）；`Type/Option/Filter/Flags` 结尾带 `[Flags]` |
| 函数指针 typedef | `[UnmanagedFunctionPointer(Cdecl)]` 委托 |
| 布局结构体 | `StructLayout(Sequential)` 顺序布局；函数指针字段 → `delegate* unmanaged[Cdecl]<…>` |

## 归属规则（机械）

1. 首参数是句柄且等于家族段 → 实例方法（`obsharp_widget_set_window_title` → `QWidget.SetWindowTitle`）
2. `create` 系返回家族句柄本身 → 静态工厂（`obsharp_widget_create` → `QWidget.Create(parent)`）
3. 家族段映射到句柄类但首参不是它 → 该类的静态方法（`obsharp_filepicker_create` → `QFileDialog.Create(parent)`）
4. 无 this → 按头文件分组的静态类（`application.h` → `Application`、`obsharp.h` → `Native`、`window.h` → `Window`…）

别名表 / 分组表 / 所有权表都在 `Naming.cs`。

## 解析层说明

- libclang 21（ClangSharp 21.1.8.4 附带）驱动：`CXTranslationUnit.Parse` + 自写遍历
- **STL 版本门槛**：libclang 21 会被 MSVC STL 14.52 的 `yvals_core.h`（STL1000）拒绝，
  用 `--msvc-include` 钉旧工具集（如 `…\VC\Tools\MSVC\14.44.xxxxx\include`）绕开
- **`-imsvc` 不可用**：那是 clang driver 选项，libclang 走 cc1；这里用 cc1 的 `-isystem`
- **obsconfig.h 替身**：libobs 的 `obs-config.h` 要构建期生成的 `obsconfig.h`（不在仓库里）。
  生成器会写一个最小替身到 `%TEMP%/obsharp-interop-generator/`，只为解析闭包通过
- 诊断（错误级）打到 stderr，带完整 clang 文本

## 边界

- 只做「薄封装 + 机械映射」：SafeHandle 友好层、回调 thunk 生命周期（`Scheme.Register`
  的托管委托垫片）继续手写，用 `partial` 续在同一张皮上
- varargs 函数（`Log(...)`）过不了 C ABI 封送，自动跳过
- 匿名函数指针参数不支持（必须是 typedef 命名的）

## 验证方式

产物用 `net11.0` + `AllowUnsafeBlocks` 的空工程直接编译（`LibraryImport` 源生成器
参与封送），Native 全量 184 个函数的产物编译 0 警告 0 错误。
