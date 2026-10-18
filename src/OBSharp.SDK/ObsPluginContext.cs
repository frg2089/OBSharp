using System.Collections.Concurrent;
using System.Globalization;

using OBSharp.Interop;

namespace OBSharp;

/// <summary>
/// 宿主（OBSharp.Runtime）为每个插件准备好的环境信息，插件通过
/// <see cref="ObsPlugin.Context"/> 拿到它。插件不需要自己构造。
/// </summary>
public sealed unsafe class ObsPluginContext
{
    private const string FallbackLocale = "en-US";

    // 每种语言一份解析结果。OBS 换语言时会重新查（见 OBSharpHost.SetLocale），
    // 所以这里按语言名缓存，插件可以在任意线程上调用 GetText。
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _locales = new(StringComparer.OrdinalIgnoreCase);

    internal ObsPluginContext(ObsModule* module, string dataFolder, IServiceProvider services, RuntimeFramework runtimeFramework)
    {
        Module = module;
        DataFolder = dataFolder;
        Services = services;
        RuntimeFramework = runtimeFramework;
    }

    /// <summary>
    /// OB# 这个 OBS 模块的 <c>obs_module_t*</c>。可以拿它调用生成的绑定，
    /// 比如 <c>Module-&gt;GetModuleDataPath()</c>、<c>Module-&gt;FindModuleFile()</c>。
    /// </summary>
    public ObsModule* Module { get; }

    /// <summary>
    /// 插件的数据目录 <c>&lt;OB# 数据目录&gt;/&lt;插件名&gt;</c>，附加到插件之前已经创建好。
    /// </summary>
    public string DataFolder { get; }

    /// <summary>
    /// 宿主注册的服务，可以解析 <c>ILogger&lt;T&gt;</c>、<c>IConfiguration</c>、<c>IOptions&lt;T&gt;</c> 等。
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// 宿主是否加载了 Microsoft.AspNetCore.App。
    /// </summary>
    public bool UseAspNet => RuntimeFramework is RuntimeFramework.AspNet;

    /// <summary>
    /// 宿主是否加载了 Microsoft.WindowsDesktop.App。
    /// </summary>
    public bool UseWindowsDesktop => RuntimeFramework is RuntimeFramework.WindowsDesktop;
    /// <summary>
    /// 宿主运行时框架。
    /// </summary>
    public RuntimeFramework RuntimeFramework { get; }

    /// <summary>
    /// 读取插件自己的本地化字符串：<c>&lt;DataFolder&gt;/locale/&lt;locale&gt;.ini</c>，
    /// 先装 <c>en-US.ini</c> 再用当前语言覆盖（和 libobs 的 <c>obs_module_load_locale</c>
    /// 一致）。找不到时按 OBS 的习惯返回 <paramref name="key"/> 本身。
    /// </summary>
    public string GetText(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        string locale = CultureInfo.CurrentUICulture.Name;
        return _locales.GetOrAdd(locale.Length is 0 ? FallbackLocale : locale, LoadLocale)
            .TryGetValue(key, out string? text) ? text : key;
    }

    private Dictionary<string, string> LoadLocale(string locale)
    {
        Dictionary<string, string> text = new(StringComparer.Ordinal);
        ReadLocaleFile(FallbackLocale, text);
        if (!string.Equals(locale, FallbackLocale, StringComparison.OrdinalIgnoreCase))
            ReadLocaleFile(locale, text);

        return text;
    }

    private void ReadLocaleFile(string locale, Dictionary<string, string> text)
    {
        string path = Path.Combine(DataFolder, "locale", locale + ".ini");
        if (!File.Exists(path))
            return;

        foreach (string line in File.ReadLines(path))
        {
            ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] is '#' or ';')
                continue;

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            text[trimmed[..separator].TrimEnd().ToString()] = trimmed[(separator + 1)..].TrimStart().ToString();
        }
    }
}
