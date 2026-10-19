# OBSharp.SDK

写 OB# 插件要引用的就是这个项目：`ObsPlugin` 基类、宿主给插件的环境信息（`ObsPluginContext`），
加上传递引用的 `OBSharp.Interop`（libobs 的全套绑定）、`OBSharp.Interop.Browser`
（浏览器控件的托管绑定，原生库另说）和 `Microsoft.Extensions.Logging.Abstractions`
（`ILogger`，不用另外加包）。

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
`OBSharp.Interop.Browser`、`Microsoft.Extensions.Logging.Abstractions` 和它带的
`Microsoft.Extensions.DependencyInjection.Abstractions`
（引用包时自动生效，不用写 `Import`）。它们只在编译时有用，运行时宿主自己带着（见「部署」）；
不拷进 `bin`、不写进插件自己的 `deps.json`，`dotnet publish` 也一样。插件自己的第三方依赖不受影响，
确实要带上它们的话设 `OBSharpExcludeHostAssembliesFromOutput=false`。

## 最小插件

```csharp
using OBSharp;

namespace MyPlugin;

public sealed class MyPlugin : ObsPlugin
{
    // 插件名，同时也是数据目录名；不写就用程序集名。
    public override string Name => "MyPlugin";
    public override string? Description => "我的第一个插件";

    public override bool Load()
    {
        Logger.LogInformation("数据目录：{DataFolder}", DataFolder);
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
| `Logger` | 插件自己的日志，直接写进 OBS 的日志 |
| `GetText(key)` | 插件自己的本地化字符串 |

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

// 直接写 OBS 日志（一般用 Logger 就够了）
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
   ; 插件列表：<程序集路径> = <true|false>，分号和 # 后面是注释
   C:\path\to\MyPlugin\MyPlugin.dll = true
   MyOther\MyOtherPlugin.dll = false
   ```

   路径可以写相对的，按这个文件所在目录（也就是 OB# 数据目录）解析。
3. 重启 OBS。

`= false` 的行不会加载，但会留在插件列表里，可以在 OB# 的插件界面（`OBSharpPluginManager`）里启用。
启用或停用之后 OB# 会把列表写回这个文件，所以手写的注释会被覆盖、相对路径会变成绝对路径。

`OBSharp.SDK`、`OBSharp.Interop`、`OBSharp.Interop.Browser` 和 `Microsoft.Extensions.Logging.Abstractions` 由宿主提供：插件目录里就算有自己的一份副本也会被忽略（引用 SDK 时 `build/OBSharp.SDK.targets` 已经把它们从输出里去掉，编译时照样能引用）。跨边界的类型（`ObsPlugin`、`ILogger<T>`……）只有一份，插件才认得出来。插件加载在自己的 `AssemblyLoadContext` 里，可以卸载，卸载时宿主会把加载上下文也卸掉。

## 目录

| 文件 | 内容 |
| --- | --- |
| `ObsPlugin.cs` | 插件基类 |
| `ObsPluginContext.cs` | 宿主交给插件的环境信息、本地化字符串 |
| `build/OBSharp.SDK.targets` | 插件工程 Import 它（引用包时自动导入）：把宿主提供的程序集从插件输出里去掉 |
