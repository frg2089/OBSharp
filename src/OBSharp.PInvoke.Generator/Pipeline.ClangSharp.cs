using System.Text.RegularExpressions;

namespace OBSharp.PInvoke.Generator;

// Stage 1: raw bindings via ClangSharp.
internal sealed partial class Pipeline
{
    private const string LibobsDir = "lib/obs-studio/libobs";
    private const string PthreadDir = "lib/obs-studio/deps/w32-pthreads";

    // ClangSharpPInvokeGenerator is a local dotnet tool: dotnet-tools.json (repo
    // root) pins the version and `dotnet tool run` resolves the host-RID payload
    // (exe + libclang/libClangSharp). The trailing `--` keeps `dotnet tool run`
    // from swallowing the generator's own switches.
    private static readonly string[] ToolCommand =
        ["tool", "run", "ClangSharpPInvokeGenerator", "--"];

    // Variadic C functions - C# cannot P/Invoke them.
    private static readonly string[] ExcludeFns =
    [
        "profile_store_name", "dstr_printf", "dstr_catf", "os_process_args_add_argf",
    ];

    private void RunClangSharpStage()
    {
        if (Directory.Exists(_stagingDir))
        {
            Directory.Delete(_stagingDir, recursive: true);
        }

        Directory.CreateDirectory(_stagingDir);

        Console.WriteLine("==> Probing parsed headers...");
        var probeLog = Path.Combine(_stagingDir, "visited-files.log");
        InvokeClangSharp([LibobsDir + "/obs.h"], _stagingDir, probeLog);
        var traverse = GetVisitedHeaders(probeLog);
        Console.WriteLine($"    {traverse.Count} headers discovered");

        Console.WriteLine("==> Generating raw bindings...");
        InvokeClangSharp(traverse, _stagingDir, logFile: null);
        var fnCount = Regex.Matches(
                File.ReadAllText(Path.Combine(_stagingDir, "Libobs.cs")), "public static extern")
            .Count;
        var fileCount = Directory.GetFiles(_stagingDir, "*.cs").Length;
        Console.WriteLine($"    {fnCount} functions, {fileCount} files");
    }

    // ClangSharpPInvokeGenerator runs as the local dotnet tool pinned by
    // dotnet-tools.json; the repo root is the working directory so the manifest
    // is discovered. (The tool must have been restored: `dotnet tool restore`.)
    private void InvokeClangSharp(IReadOnlyList<string> traverse, string outDir, string? logFile)
    {
        var args = new List<string>
        {
            "-f", "obs_all.h",
            "-F", _shimDir,
            "-I", LibobsDir.Replace('/', '\\'),
            "-I", PthreadDir.Replace('/', '\\'),
            "-I", _shimDir,
            "-o", outDir,
            "-n", "OBSharp.PInvoke",
            // PascalCase module name: the LibraryImport source generator re-emits a
            // partial declaration of this class per method in its obj/ output, which
            // cannot carry #pragma directives - a lowercase 'libobs' there triggers
            // CS8981 (lowercase type name) once per raw binding with no way to
            // suppress it (editorconfig severities are not applied to generated trees).
            "-m", "Libobs",
            "-x", "c",
            "-l", "obs",
            "--generate", "funcs-with-body=false",
            "--generate", "helper-types",
            "--config", "file=multi",
        };
        foreach (var e in ExcludeFns)
        {
            args.Add("-e");
            args.Add(e);
        }

        foreach (var t in traverse)
        {
            args.Add("-t");
            args.Add(t);
        }

        if (logFile is not null)
        {
            args.Add("-lg");
            args.Add("visited-files");
        }

        var (exitCode, lines) = RunProcess(
            "dotnet", [.. ToolCommand, .. args], _repoRoot, echo: true);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ClangSharpPInvokeGenerator failed with exit code {exitCode}");
        }

        if (logFile is not null)
        {
            File.WriteAllLines(logFile, lines);
        }
    }

    private static List<string> GetVisitedHeaders(string logFile)
    {
        // Sort-Object -Unique default: culture-aware, case-insensitive. The -t
        // flag order must match the PowerShell pipeline so raw output stays
        // byte-identical.
        var headers = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var line in File.ReadLines(logFile))
        {
            var m = Regex.Match(line, @"Visiting\s+(lib/obs-studio/[\w\-/\.]+\.h)\s*$");
            if (m.Success)
            {
                headers.Add(m.Groups[1].Value);
            }
        }

        if (headers.Count == 0)
        {
            throw new InvalidOperationException($"No repo headers discovered in {logFile}");
        }

        return [.. headers];
    }
}
