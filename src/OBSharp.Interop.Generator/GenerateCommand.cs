using DotMake.CommandLine;

namespace OBSharp.Interop.Generator;

/// <summary>
/// C 头 → C# 薄封装绑定生成器。
///
/// 用法示例：
/// <code>
/// obsharp-interop-gen --input widget.h --lib OBSharp.Interop.Native \
///     --namespace OBSharp.Interop.Native --output src/OBSharp.Interop/Generated/Native
/// </code>
/// </summary>
[CliCommand(Description = "从 C/C++ 头文件生成 SafeHandle 薄封装绑定（*.g.cs）")]
public class GenerateCommand
{
    [CliOption(Alias = "i", Required = true,
        Description = "输入头文件（可多个）。只收集声明位置落在这些头里的符号；被 include 的 Qt/CEF 等声明参与解析但不产出")]
    public string[] Input { get; set; } = [];

    [CliOption(Alias = "l", Required = true,
        Description = "LibraryImport 的库名，如 obs。可配合 --lib-prefix 给不同函数前缀指定不同库")]
    public string Lib { get; set; } = "";

    [CliOption(Description = "函数名前缀 → 库名映射（prefix=libname，可多个）；如 obs_frontend_=obs-frontend-api。匹配不上走 --lib")]
    public string[] LibPrefix { get; set; } = [];

    [CliOption(Description = "C++ 记录类型 → .NET 等价类型（name=Type，可多个）。内置 vec2/vec3/vec4/matrix3/matrix4 → System.Numerics")]
    public string[] TypeAlias { get; set; } = [];

    [CliOption(Description = "生成「程序集已禁用运行时封送」形态的产物：委托全 blittable（byte* 字符串、指针出参）、" +
        "System.Numerics 等别名类型在 raw 层直走 in/ref。默认关闭——默认形态不要求 [assembly: DisableRuntimeMarshalling]")]
    public bool DisableRuntimeMarshalling { get; set; } = false;

    [CliOption(Alias = "n", Required = true,
        Description = "生成代码的命名空间")]
    public string Namespace { get; set; } = "";

    [CliOption(Alias = "o", Required = true,
        Description = "输出目录（每次运行清掉旧的 *.g.cs 再全量重写）")]
    public string Output { get; set; } = "";

    [CliOption(Description = "附加 include 目录（-I），可多个")]
    public string[] Include { get; set; } = [];

    [CliOption(Description = "MSVC 系统 include 目录（-isystem），用于钉死工具集版本绕开 STL 版本门槛")]
    public string[] MsvcInclude { get; set; } = [];

    [CliOption(Alias = "D", Description = "预处理器宏定义（-D），可多个")]
    public string[] Define { get; set; } = [];

    [CliOption(Description = "额外透传给 libclang 的参数，可多个")]
    public string[] ExtraArg { get; set; } = [];

    [CliOption(Description = "符号名过滤前缀（可多个，支持尾部 *）；默认不滤，只按 --input 文件归属")]
    public string[] Traverse { get; set; } = [];

    [CliOption(Description = "C++ 标准")]
    public string Std { get; set; } = "c++20";

    [CliOption(Description = "返回字符串使用的自定义 marshaller 全名（只复制、不释放）")]
    public string StringMarshaller { get; set; } = "global::OBSharp.Interop.OBSStringMarshaller";

    public void Run()
    {
        foreach (var input in Input)
        {
            // 允许通配符（--input "lib/obs-studio/libobs/*.h"）
            if (input.Contains('*') || input.Contains('?'))
                continue;
            if (!File.Exists(input))
                throw new FileNotFoundException($"输入头文件不存在：{input}");
        }

        if (Lib.Length == 0)
            throw new ArgumentException("--lib 不能为空");
        if (Namespace.Length == 0)
            throw new ArgumentException("--namespace 不能为空");
        if (Output.Length == 0)
            throw new ArgumentException("--output 不能为空");

        foreach (var mapping in TypeAlias)
        {
            var eq = mapping.IndexOf('=');
            if (eq <= 0)
                throw new ArgumentException($"--type-alias 格式应为 name=Type：{mapping}");
            Naming.TypeAliases[mapping[..eq]] = mapping[(eq + 1)..];
        }

        var model = new Parser(this).Parse();

        if (model.Functions.Count == 0)
            throw new InvalidOperationException(
                "没有收集到任何函数：检查 --input 是否是导出面头文件、--traverse 是否滤光了");

        new Emitter(this, model).Emit();
    }
}
