using System.Reflection;
using System.Runtime.InteropServices;

namespace OBSharp.Interop.Browser;

/// <summary>
/// 告诉运行时去哪儿找 <c>OBSharp.Interop.Browser.Native</c>。
/// </summary>
/// <remarks>
/// 原生库由本工程（<c>OBSharp.Interop.Browser.csproj</c>）从
/// <c>out/native/&lt;arch&gt;/&lt;Configuration&gt;</c> 复制到输出目录，默认就在程序集
/// 旁边；另外可以用环境变量 <c>OBSHARP_NATIVE_DIR</c> 指到别的地方（开发时省得
/// 每次拷文件）。
/// </remarks>
internal static class NativeLibraryLoader
{
    internal const string LibraryName = "OBSharp.Interop.Browser.Native";
    internal const string DirectoryVariable = "OBSHARP_NATIVE_DIR";

    private static int s_registered;

    /// <summary>
    /// 注册解析器。放在 <see cref="NativeMethods"/> 的静态构造函数里，保证在第一次
    /// P/Invoke 之前执行。
    /// </summary>
    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) != 0)
            return;

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return 0; // 不是我们的库，交回给默认逻辑

        foreach (string directory in SearchDirectories(assembly))
        {
            foreach (string fileName in FileNames())
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
                    return handle;
            }
        }

        return 0;
    }

    private static IEnumerable<string> SearchDirectories(Assembly assembly)
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrEmpty(overrideDirectory))
            yield return overrideDirectory;

        // 这里用 Assembly.Location 而不是 AppContext.BaseDirectory：插件程序集不在
        // 主程序的目录里（OBSharp.Loader 从别处加载它），原生库就跟在它旁边。
        // 单文件发布（IL3000 警告的场景）不是 OBS 插件的形态，所以忽略该警告。
#pragma warning disable IL3000
        string? assemblyDirectory = Path.GetDirectoryName(assembly.Location);
#pragma warning restore IL3000
        if (!string.IsNullOrEmpty(assemblyDirectory))
            yield return assemblyDirectory;
    }

    private static IEnumerable<string> FileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return LibraryName + ".dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "lib" + LibraryName + ".dylib";
        }
        else
        {
            yield return "lib" + LibraryName + ".so";
        }
    }
}
