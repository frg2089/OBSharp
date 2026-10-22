using OBSharp.Interop.Abi;

namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// obs-browser 的 <c>QCef</c> 面板接口，以及它的版本号。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>obsharp::BrowserPanel</c>（<c>HostApi.h</c>）。这个类型里刻意只有两个
/// 「裸」字段：面板接口是 obs-browser 分配、obs-browser 释放的对象，托管侧永远不该替它
/// 决定生命周期，所以不实现 <see cref="IDisposable"/>、不复制、也不做引用计数——
/// 一旦包上所有权语义，就有人会在错误的线程 / 错误的时候去释放它。
/// </para>
/// <para>
/// <see cref="Cef"/> 非 0 就算可用；<see cref="Version"/> 只用来做版本判断
/// （obs-browser 的 <c>BROWSER_PANEL_VERSION</c> 变化时 vtable 布局可能已经不同）。
/// </para>
/// <para>
/// <b>关于下面那三个静态转发成员（<see cref="TryLoad"/> / <see cref="Load"/> /
/// <see cref="IsAvailable"/>）：实现不在这里，全部在 <see cref="QcefHost"/>。</b>
/// 它们纯粹是为了保持对外的门面（上层调用方一直写的是「问面板接口要不要得」，
/// 而不是「问宿主解析器」），每个方法体都只有一行转发。
/// <b>不要</b>往这里加新的解析逻辑，也不要在这里缓存任何状态——一旦有人把逻辑写进
/// <see cref="QcefPanel"/>，就会出现两份会各自演化的实现，而它们的失败时机
/// （模块还没加载 / Wayland / obs-browser 太老）恰恰是最难对齐的部分。
/// </para>
/// </remarks>
internal sealed class QcefPanel
{
    /// <summary>
    /// obs-browser 的 <c>QCef*</c>。借用指针：归 obs-browser 所有，我们只带着它调用。
    /// </summary>
    public nint Cef;

    /// <summary>
    /// <c>obs_browser_qcef_version_export()</c> 的返回值；
    /// obs-browser 没导出那个符号时是 0（老版本，照样能用）。
    /// </summary>
    public int Version;

    /// <summary>
    /// 解析并返回面板接口；失败返回 <see langword="null"/>。
    /// </summary>
    /// <param name="error">失败原因（成功时为 <see langword="null"/>）。</param>
    /// <remarks>
    /// <b>纯转发，实现见 <see cref="QcefHost.TryLoad"/>。</b>
    /// 留在 <see cref="QcefPanel"/> 上是为了保持对外门面（调用方写的是
    /// <c>QcefPanel.TryLoad(...)</c>），也是为了不逼调用方为了拿一个面板接口
    /// 去认识「宿主解析器」这个概念。这里不缓存、不判断、不记日志。
    /// </remarks>
    public static QcefPanel? TryLoad(out string? error) => QcefHost.TryLoad(out error);

    /// <summary>
    /// 面板接口现在是否可用。
    /// </summary>
    /// <remarks>
    /// <b>纯转发，实现见 <see cref="QcefHost.IsAvailable"/>。</b>
    /// 同上：只是为了门面存在，逻辑不在这里。
    /// </remarks>
    public static bool IsAvailable => QcefHost.IsAvailable;

    /// <summary>
    /// <see cref="TryLoad"/> 的别名。
    /// </summary>
    /// <param name="error">失败原因（成功时为 <see langword="null"/>）。</param>
    /// <remarks>
    /// <b>纯转发，实现见 <see cref="QcefHost.TryLoad"/>。</b>
    /// 另一份契约里写的是 <c>QcefPanel.Load</c>。两个名字语义完全相同、都留着，
    /// 调用方写哪个都能编译；实现仍然只有 <see cref="QcefHost"/> 那一份。
    /// </remarks>
    public static QcefPanel? Load(out string? error) => QcefHost.TryLoad(out error);
}

/// <summary>
/// 在宿主进程里解析 obs-browser 的 <c>QCef</c> 面板接口。
/// </summary>
/// <remarks>
/// <para>
/// 对应旧 C++ 的 <c>LoadBrowserPanel</c> / <c>IsBrowserPanelSupported</c>（<c>HostApi.cpp</c>）。
/// 本库被 OB# 加载进 OBS 进程，宿主是 OBS 自己：我们不链接 libobs，也不链接 obs-browser，
/// 而是在运行时从<b>已经加载</b>的模块里取符号。所以这里所有失败都只可能是「宿主的版本 / 配置
/// 不对」，而不是「我们的路径写错了」——错误文案要能直接告诉用户是哪一种。
/// </para>
/// <para>
/// 为什么用 <c>delegate* unmanaged[Cdecl]</c> 而不是 <c>DllImport</c>：这些函数不在任何
/// 一个固定的 dll 里，路径要运行时才知道；而且函数指针不产生托管桩，AOT 下没有额外的
/// 动态代码生成。
/// </para>
/// </remarks>
internal static unsafe class QcefHost
{
    /// <summary>
    /// 解析过程的互斥锁。
    /// </summary>
    /// <remarks>
    /// 解析会做 <c>GetProcAddress</c> / <c>dlsym</c> 并可能调用 <c>obs_browser_create_qcef()</c>，
    /// 后者对 obs-browser 是有副作用的（建 QCef 单例）。两个线程同时进来会创建两份，
    /// 所以整段串行化；代价只在第一次解析时付一次。
    /// </remarks>
    private static readonly object Gate = new();

    /// <summary>
    /// 解析成功后的缓存。0 / <see langword="null"/> 表示还没成功过。
    /// </summary>
    private static QcefPanel? _cached;

    /// <summary>
    /// 拿到 obs-browser 的面板接口；失败返回 <see langword="null"/> 并给出原因。
    /// </summary>
    /// <param name="error">失败原因（成功时写 <see langword="null"/>）。</param>
    /// <remarks>
    /// <para>
    /// 幂等：成功的结果缓存在 <see cref="_cached"/> 里，后续调用直接返回同一个
    /// <see cref="QcefPanel"/>，跟旧 C++「<c>panel.IsValid()</c> 就直接返回 true」一致。
    /// </para>
    /// <para>
    /// <b>失败不缓存</b>：obs-browser 完全可能在宿主启动之后才被加载（OBS 的模块是异步装的），
    /// 这一次失败不代表下一次还失败。旧 C++ 每次调用都会重新试一遍，这里保持同样的语义。
    /// </para>
    /// </remarks>
    public static QcefPanel? TryLoad(out string? error)
    {
        lock (Gate)
        {
            if (_cached is not null)
            {
                error = null;
                return _cached;
            }

            // 先判平台支持再找符号：Linux/Wayland 上 obs-browser 自己就把面板功能关掉了，
            // 那种情况下后面即使找到符号也没用，先报「平台不支持」比报「符号缺失」准确得多。
            // 顺序跟旧 C++ 的 LoadBrowserPanel 一致（它先调 IsBrowserPanelSupported）。
            if (!IsBrowserPanelSupported(out error))
                return null;

            // libobs 的符号：优先从模块句柄里找，找不到模块就退回「按进程全局找」
            // （AbiApi.FindSymbol(0, ...) 对应 dlsym(RTLD_DEFAULT) / 主程序）。
            // libobs 静态链进主程序时根本没有独立模块句柄，这一步就是为那种情况留的。
            nint libobs = FindLibObsModule();

            var getModule = (delegate* unmanaged[Cdecl]<nint, nint>)AbiApi.FindSymbol(libobs, "obs_get_module");
            var getModuleLib = (delegate* unmanaged[Cdecl]<nint, nint>)AbiApi.FindSymbol(libobs, "obs_get_module_lib");
            if (getModule is null || getModuleLib is null)
            {
                error = "libobs (obs_get_module / obs_get_module_lib) is not loaded in this process";
                return null;
            }

            // getModule("obs-browser")：按模块名找 obs-browser 已经加载的实例。
            // 名字传的是 UTF-8 + NUL 的字节，用 u8 字面量 + fixed 钉住，避免为了一个常量
            // 去走 Marshal.StringToHGlobalAnsi 那种要手动 free 的路径（AOT 下也更干脆）。
            nint obsBrowserModule;
            ReadOnlySpan<byte> moduleName = "obs-browser\0"u8;
            fixed (byte* moduleNamePtr = moduleName)
                obsBrowserModule = getModule((nint)moduleNamePtr);

            if (obsBrowserModule == 0)
            {
                error = "the obs-browser module is not loaded";
                return null;
            }

            // 模块句柄 → 它的动态库句柄。obs-browser 的导出符号要从这里查：直接从
            // 「全局」查不到，因为它是作为插件 dlopen 进来的，不是主程序的一部分。
            nint obsBrowserLib = getModuleLib(obsBrowserModule);
            if (obsBrowserLib == 0)
            {
                error = "cannot resolve the obs-browser module handle";
                return null;
            }

            // 工厂函数：每次调用会建一个新的 QCef。正常只调一次（结果缓存起来）。
            var createQCef = (delegate* unmanaged[Cdecl]<nint>)AbiApi.FindSymbol(obsBrowserLib, "obs_browser_create_qcef");
            if (createQCef is null)
            {
                error = "obs-browser does not export obs_browser_create_qcef (too old?)";
                return null;
            }

            nint cef = createQCef();
            if (cef == 0)
            {
                error = "obs_browser_create_qcef() returned null";
                return null;
            }

            // 版本号是可选符号：老 obs-browser 没有它，那种情况下 Version = 0，
            // 上层只能用「0 表示未知」这个约定，不能拿它去跑版本比较。
            int version = 0;
            var versionFn = (delegate* unmanaged[Cdecl]<int>)AbiApi.FindSymbol(obsBrowserLib, "obs_browser_qcef_version_export");
            if (versionFn is not null)
                version = versionFn();

            _cached = new QcefPanel
            {
                Cef = cef,
                Version = version,
            };

            error = null;
            return _cached;
        }
    }

    /// <summary>
    /// obs-browser 的面板接口现在是否可用（对应的就是 <see cref="TryLoad"/> 成功）。
    /// </summary>
    /// <remarks>
    /// 这是查询，不是承诺：只表示「此刻能拿到」。真正的使用方仍然要处理随后
    /// <c>create_widget</c> 失败的情况（宿主可能在两者之间关掉 CEF）。
    /// </remarks>
    public static bool IsAvailable => TryLoad(out _) is not null;

    /// <summary>
    /// 当前宿主环境是否支持浏览器面板；不支持时给出原因。
    /// </summary>
    /// <param name="error">不支持的原因；支持时为 <see langword="null"/>。</param>
    /// <remarks>
    /// 目前只有一处平台限制：Linux 上 obs-browser 在 Wayland 会话里禁用了面板
    /// （X11 下 CEF 的 OSR 才有办法正确合成）。判断方式跟旧 C++ 一致——问 libobs
    /// 当前是哪个 nix 平台后端，<c>2</c> 是 Wayland（<c>enum obs_nix_platform_type</c>:
    /// INVALID=0 / X11_EGL=1 / WAYLAND=2）。符号不存在时就当不是 Wayland（旧行为）。
    /// </remarks>
    private static bool IsBrowserPanelSupported(out string? error)
    {
        if (OperatingSystem.IsLinux())
        {
            var getNixPlatform = (delegate* unmanaged[Cdecl]<int>)AbiApi.FindSymbol(0, "obs_get_nix_platform");
            if (getNixPlatform is not null && getNixPlatform() == 2)
            {
                error = "browser panels are disabled by obs-browser on Wayland";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 按平台试 libobs 的模块名，找到就返回句柄，找不到返回 0（= 按进程全局查）。
    /// </summary>
    /// <remarks>
    /// 为什么每个平台要试两个名字：
    /// <list type="bullet">
    /// <item>Windows 上 OBS 发布的包是 <c>obs.dll</c>，但从源码构建 / 部分发行版叫 <c>libobs.dll</c>；</item>
    /// <item>macOS 的 framework / 普通 dylib 命名不一致；</item>
    /// <item>Linux 的 soname 通常是 <c>libobs.so.0</c>，但没有版本后缀的开发构建叫 <c>libobs.so</c>。</item>
    /// </list>
    /// 这些差异属于「宿主长什么样」，只能由 C# 决定（原生侧只查一个确切名字）。
    /// </remarks>
    private static nint FindLibObsModule()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["obs.dll", "libobs.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libobs.dylib", "obs.dylib"]
                : ["libobs.so.0", "libobs.so"];

        foreach (string candidate in candidates)
        {
            nint module = AbiApi.FindModule(candidate);
            if (module != 0)
                return module;
        }

        // 一个都没找到不算致命错误：下面会用 FindSymbol(0, ...) 全局找。
        // （libobs 静态链进主程序时就是这样，没有独立模块。）
        return 0;
    }
}
