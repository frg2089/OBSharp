# OBSharp.SDK

写 OB# 插件要引用的就是这个项目：`ObsPlugin` 基类、宿主给插件的环境信息，加上传递引用的 `OBSharp.Interop`（libobs 的全套绑定）。

## 引用

```xml
<PropertyGroup>
  <!-- 插件要直接读写指针（Module、interop 绑定等） -->
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="..\OBSharp.SDK\OBSharp.SDK.csproj" />
</ItemGroup>
```

想通过构造函数注入 `ILogger<T>` 的话，再加一个 `Microsoft.Extensions.Logging.Abstractions` 包引用。

## 最小插件

```csharp
using Microsoft.Extensions.Logging;

using OBSharp;

namespace MyPlugin;

public sealed unsafe class MyPlugin(ILogger<MyPlugin> logger) : ObsPlugin
{
    // 插件名，同时也是数据目录名；不写就用程序集名。
    public override string Name => "MyPlugin";
    public override string? Description => "我的第一个插件";

    public override bool Load()
    {
        logger.LogInformation("数据目录：{DataFolder}", DataFolder);
        logger.LogInformation("OB# 模块名：{ModuleName}", Module->GetModuleName());
        return true;
    }

    public override void Unload()
    {
        // 释放插件自己持有的非托管资源
    }
}
```

一个程序集里只能有一个 `ObsPlugin` 子类。插件的构造函数由宿主用依赖注入构造，可以注入下表列出的任何服务。

## 生命周期

`Load()` → 所有插件都加载完再 `PostLoad()` → 卸载时 `Unload()`（实现了 `IDisposable` 的插件还会顺带 `Dispose()`）。

- 插件抛异常只会记一条日志，不会影响别的插件。
- `Load()` 返回 `false` 表示加载失败（宿主记一条 Warning），模块本身继续。

## 插件能拿到什么

| 成员 | 说明 |
| --- | --- |
| `Name` / `Description` | 插件名与描述，加载前就可用 |
| `Context` | `ObsPluginContext`，`Load()` 之后才可用 |
| `DataFolder` | `<OB# 数据目录>/<插件名>`，加载前已经创建好 |
| `Module` | OB# 这个 OBS 模块的 `obs_module_t*` |
| `Services` | 宿主的服务容器 |
| `GetText(key)` | 插件自己的本地化字符串 |

构造函数里可以注入：`ILogger<T>`、`IConfiguration`、`IOptions<T>`、`IServiceProvider`、`IDataFolderProvider`、`IRuntimeInfoProvider`。

## 调用 libobs

绑定都在 `OBSharp.Interop` 里。所有权会转移给调用方的返回值是 `SafeHandle`（出了作用域就释放），借来的指针还是裸指针：

```csharp
using OBSharp.Interop;

using var data = ObsDataExtensions.Create();
data?.SetString("name", "value");
string? json = data?.GetJson();

// 裸指针（char*）由调用方释放
byte* file = Module->FindModuleFile("something.txt");
if (file is not null)
{
    string path = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)file)!;
    Base.Bfree((nint)file);
}

// 直接写 OBS 日志（一般用 ILogger 就够了）
ObsExtensions.Log(300, "hello from plugin");
```

## 数据目录与本地化

```text
<OBS 数据目录>/obs-plugins/OBSharp/MyPlugin/
    locale/en-US.ini     # Greeting=Hello
    locale/zh-CN.ini     # Greeting=你好
```

`GetText("Greeting")` 先读 `en-US.ini`，再用当前语言的同名文件覆盖；两处都没有就返回 key 本身（和 OBS 的 `obs_module_text` 一个习惯）。当前语言来自 OBS 的 `obs_module_set_locale`，宿主会同步到 `CultureInfo.CurrentUICulture`。

## 部署

1. 把插件 dll（连同它旁边的 `.deps.json` 和依赖）放进插件目录，比如 `<数据目录>/obs-plugins/OBSharp/MyPlugin/`。
2. 在 `<数据目录>/obs-plugins/OBSharp/plugins.ini` 里写一行，`=` 后面是 `true`：

   ```ini
   C:\path\to\MyPlugin\MyPlugin.dll = true
   ; 分号和 # 后面是注释，值不是 true 的行会被忽略
   ```

3. 重启 OBS。

`OBSharp.SDK`、`OBSharp.Interop` 和 `Microsoft.Extensions.*` 由宿主提供：插件目录里就算有自己的一份副本也会被忽略。跨边界的类型（`ObsPlugin`、`ILogger<T>`……）只有一份，插件才认得出来。插件加载在自己的 `AssemblyLoadContext` 里，可以卸载，卸载时宿主会把加载上下文也卸掉。

## 目录

| 文件 | 内容 |
| --- | --- |
| `ObsPlugin.cs` | 插件基类 |
| `ObsPluginContext.cs` | 宿主交给插件的环境信息、本地化字符串 |
| `IDataFolderProvider.cs` | 插件数据目录的提供者（宿主实现） |
| `IRuntimeInfoProvider.cs` | 宿主运行时框架信息（宿主实现） |
