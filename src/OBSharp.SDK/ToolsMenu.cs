using System.Collections.Concurrent;
using System.Diagnostics;

using OBSharp.Interop;
using OBSharp.Interop.Browser;

namespace OBSharp;

/// <summary>
/// OBS「工具」菜单项的托管侧登记表。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要这么绕：<c>obs_frontend_add_tools_menu_item()</c> 只接受一个裸回调
/// （<c>obs_frontend_cb</c>），而且 <b>没有</b>对应的「移除菜单项」API —— 加进去的那一项
/// 会一直留在 OBS 的菜单里，直到进程退出。
/// </para>
/// <para>
/// 而插件是装进<b>可卸载</b>的 <c>AssemblyLoadContext</c> 里的：插件停用后，它自己的
/// 类型、委托、对象都会被回收。所以回调不能是插件实例上的方法，也不能把插件对象塞进
/// <c>private_data</c>（插件卸载后那就是野指针）。
/// </para>
/// <para>
/// 做法：回调固定指向本类里的<b>静态方法</b>（本类由默认上下文加载，不随插件卸载），
/// 每个菜单项分配一个<b>稳定</b>的整数 id 传进 <c>private_data</c>；回调进来后按 id
/// 查表，找不到就安全地什么都不做。
/// </para>
/// </remarks>
internal static class ToolsMenu
{
    // 回调只登记一次；OBS 会一直持有这个委托，所以必须活到进程结束。
    private static readonly CreateParamCallback Callback = OnToolsMenuClicked;

    // id -> 处理器。id 递增分配、永不回收（避免卸载后旧 id 被新菜单项重用）。
    private static readonly ConcurrentDictionary<nint, Entry> Entries = new();
    private static int _nextId;

    /// <summary>
    /// 加一个菜单项。
    /// </summary>
    /// <param name="host">菜单项归属的 host（只用于存活判断）。</param>
    /// <param name="text">菜单项文字。</param>
    /// <param name="onClick">点击回调。</param>
    internal static bool Add(ObsHost host, string text, Action onClick)
    {
        // private_data 是 void*：用递增 id 当键，不传托管对象进去。
        nint id = ++_nextId;
        var entry = new Entry(host, onClick);
        Entries[id] = entry;

        try
        {
            OBSharpNative.InvokeUi(() => ObsFrontend.AddToolsMenuItem(text, Callback, id));
            return true;
        }
        catch (Exception exception)
        {
            // 不在 OBS 里（比如离线跑测试）时没有前端 API：菜单项加不上，但插件照常。
            Entries.TryRemove(id, out _);
            Debug.WriteLine($"[OBSharp.SDK] 添加工具菜单项 \"{text}\" 失败：{exception}");
            return false;
        }
    }

    /// <summary>
    /// 断开某个 host 名下的全部菜单项（OBS 菜单里那一项仍在，但点了什么都不做）。
    /// </summary>
    internal static void RemoveAllFor(ObsHost host)
    {
        foreach ((nint id, Entry entry) in Entries)
        {
            if (ReferenceEquals(entry.Host, host))
                Entries.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 清掉所有登记（进程退出 / 插件全部卸载）。之后点菜单什么都不做。
    /// </summary>
    internal static void Clear() => Entries.Clear();

    // 运行在 Qt 主线程上。绝不能把异常抛回原生栈。
    private static void OnToolsMenuClicked(nint privateData)
    {
        try
        {
            if (!Entries.TryGetValue(privateData, out Entry? entry))
                return; // 插件已经卸载：菜单项还在，但什么都不做。

            entry.Invoke();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[OBSharp.SDK] 工具菜单项回调失败，已忽略：{exception}");
        }
    }

    private sealed class Entry(ObsHost host, Action onClick)
    {
        internal ObsHost Host { get; } = host;

        // onClick 指向插件自己的程序集：插件卸载后调它会炸，所以执行前确认 host 还在。
        internal void Invoke()
        {
            if (Host.IsDisposed)
                return;

            onClick();
        }
    }
}
