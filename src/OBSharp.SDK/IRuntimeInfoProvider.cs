namespace OBSharp;

/// <summary>
/// 宿主运行时信息。插件可以注入它，判断自己依赖的框架有没有被加载。
/// </summary>
public interface IRuntimeInfoProvider
{
    /// <summary>
    /// 获取宿主运行时框架。
    /// </summary>
    RuntimeFramework RuntimeFramework { get; }

    /// <summary>
    /// 宿主是否加载了 Microsoft.AspNetCore.App。
    /// </summary>
    bool UseAspNet => RuntimeFramework is RuntimeFramework.AspNet;

    /// <summary>
    /// 宿主是否加载了 Microsoft.WindowsDesktop.App。
    /// </summary>
    bool UseWindowsDesktop => RuntimeFramework is RuntimeFramework.WindowsDesktop;
}

/// <summary>
/// 宿主运行时框架。
/// </summary>
public enum RuntimeFramework
{
    /// <summary>
    /// .NET Core 框架。
    /// </summary>
    Core,
    /// <summary>
    /// ASP.NET 框架。
    /// </summary>
    AspNet,
    /// <summary>
    /// Windows 桌面框架。
    /// </summary>
    WindowsDesktop
}
