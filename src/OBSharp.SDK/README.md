# OBSharp.SDK

写 OBSharp 插件的类库。一个插件程序集实现一个 `ObsPlugin` 子类；宿主
（`OBSharp.Runtime`）加载程序集后用无参构造之外的方式实例化它（构造函数支持依赖注入），
再按 `Load` → `PostLoad` → `Unload` 驱动。

## 插件长什么样

```csharp
namespace MyPlugin;

public sealed class MyPlugin(ObsPluginInfo info) : ObsPlugin(info)
{
    public override string? Description => "我的第一个插件";

    public override bool Load()
    {
        // 没成功就 return false：宿主会回滚（调 Unload、释放加载上下文），
        // 所以这里失败时不要留下半截状态。
        return true;
    }
}
```

## 生命周期

| 钩子 | 时机 | 约定 |
| --- | --- | --- |
| `bool Load()` | 插件加载时 | 返回 `false` 表示失败：宿主记警告并回滚（调 `Unload`、释放加载上下文） |
| `void PostLoad()` | **所有**插件 `Load` 完之后 | 注册依赖其他插件的资源；运行时才启用的插件紧跟自己的 `Load` |
| `void Unload()` | 插件卸载时 | 只清理插件自己持有的非托管资源；窗口、网页等由宿主在 `Unload` 之后统一收尾 |

## 元信息

| 成员 | 含义 |
| --- | --- |
| `Name` | 插件名，默认取程序集名（不含扩展名），同时用作数据目录名 |
| `Description` | 描述，默认 `null` |
| `PluginFolder` | 插件目录（来自 `ObsPluginInfo`，由宿主注入） |

## 日志

`CreateBuilder` 会把 `ObsLoggerProvider` 注册进插件的 Web 应用：插件里拿到的
`ILogger` / `ILogger<T>` / `ILoggerFactory` 写出来的日志全部落进 OBS 日志，每行形如
`[插件标识符] [级别] 分类: 消息`。过滤按 .NET 的规则走，即插件自己 `appsettings.json`
里的 `Logging:LogLevel`。

## 自带 Web 应用

`CreateBuilder` 返回一个预配置的 `WebApplicationBuilder`（`CreateSlimBuilder`）：

* Kestrel 已配好端点：Windows 是命名管道 `\\.\pipe\obsharp\<name>`，Unix 是
  `$TMP/obsharp/<name>.socket`；面板页面的 HTTP 就从这条链路走。
* 日志已接 OBS 日志（见上）。
* Web 根默认 `<PluginFolder>/wwwroot`，可用参数覆盖 `name` / `contentRootPath` / `webRootPath`。

构建完 `Build()` / `Start()` 由插件自己调（参考
[PluginManagerPlugin](../OBSharp.PluginManager/PluginManagerPlugin.cs)）。

## libobs 与 UI

引用 SDK 即传递可用：

* **libobs 绑定**：`OBSharp.Interop` 里的生成绑定（`Obs`、`ObsSource`、`ObsData`、
  `ObsFrontend` …），日志入口 `Obs.Log(ObsLogLevel, string)`。
* **UI 原语**：`OBSharp.Interop.UI` 的 `Window` / `WebView` / `FilePicker`。窗口在需要时
  创建（比如菜单点击回调里）、用完销毁，不要替宿主管理生命周期。

## 工程配置

```xml
<ProjectReference Include="..\OBSharp.SDK\OBSharp.SDK.csproj" />
<!-- ProjectReference 引用 SDK 工程时要手动 Import（NuGet 包引用会自动生效）： -->
<Import Project="..\OBSharp.SDK\build\OBSharp.SDK.targets" />
```

[build/OBSharp.SDK.targets](build/OBSharp.SDK.targets) 把宿主提供的程序集
（`OBSharp.SDK`、`OBSharp.Interop`、`OBSharp.Runtime`、`Microsoft.Extensions.*` 抽象包）
从插件的输出、`deps.json` 和 publish 列表里去掉——宿主自己带着它们，插件目录里再放
一份只会白占空间。不想去掉就把 `OBSharpExcludeHostAssembliesFromOutput` 设成 `false`。

插件产物（`dotnet publish`）= 插件 dll + 插件自己的依赖 + `wwwroot`，放进插件目录，
由插件管理器或 `plugins.ini` 登记。
