using System.Text.RegularExpressions;

namespace OBSharp.PInvoke.Generator;

// Stage 1: raw bindings via ClangSharp.
internal sealed partial class Pipeline
{
    private const string LibobsDir = "lib/obs-studio/libobs";
    private const string PthreadDir = "lib/obs-studio/deps/w32-pthreads";

    // The umbrella header and the obsconfig.h stand-in are written into the
    // staging directory on every run instead of being checked in, so the repo
    // stays pure C#. Both are inert text for clang - only the include list and
    // the OBS_* macros matter to the parse.
    private const string ObsAllHeader = """
        /* Umbrella header the generator writes into its staging directory at run
         * time; nothing here is checked into the repo.
         *
         * Why this exists: parsing libobs/obs.h directly makes obs.h the clang
         * "main file", and clang does NOT honor #pragma once for the main file.
         * Because many libobs headers (#include "obs.h") are reachable from
         * obs.h, that leads to every enum/struct being defined twice and the
         * parse failing outright.
         *
         * Instead we make THIS file the main file and pull the public headers in
         * explicitly. We also list extra public headers that obs.h does not reach
         * so a single ClangSharp pass can bind all of libobs at once. */
        #pragma once

        /* Core (obs.h transitively pulls util/, graphics/, media-io/, callback/). */
        #include <obs.h>

        /* obs-scene.h references pthread_mutex_t but does not include threading.h
         * itself (obs-scene.c includes it first). Pull threading in before it. */
        #include <util/threading.h>

        /* Public headers that obs.h does NOT include. */
        #include <obs-module.h>
        #include <obs-scene.h>
        #include <obs-missing-files.h>

        /* Codec / NAL helpers. */
        #include <obs-nal.h>
        #include <obs-avc.h>
        #include <obs-hevc.h>
        #include <obs-av1.h>

        /* Media IO extras not reached from obs.h. */
        #include <media-io/audio-resampler.h>
        #include <media-io/video-scaler.h>
        #include <media-io/media-remux.h>
        #include <media-io/format-conversion.h>

        /* Utility helpers worth binding. */
        #include <util/config-file.h>
        #include <util/platform.h>
        #include <util/threading.h>
        #include <util/dstr.h>
        #include <util/utf8.h>
        #include <util/serializer.h>
        #include <util/pipe.h>
        #include <util/lexer.h>
        #include <util/text-lookup.h>
        #include <util/profiler.h>
        #include <util/source-profiler.h>
        #include <util/util_uint128.h>
        #include <util/crc32.h>
        #include <util/bitstream.h>
        """;

    private const string ObsConfigHeader = """
        /* Shim replacement for the CMake-generated obsconfig.h.
         * libobs/obs-config.h does `#include "obsconfig.h"`, which only exists after a
         * CMake configure. ClangSharp only needs these macros to parse the headers, so
         * we provide plausible Windows defaults. This file is NOT shipped to runtime. */
        #pragma once

        #define OBS_DATA_PATH "data"
        #define OBS_PLUGIN_PATH "obs-plugins"
        #define OBS_PLUGIN_DESTINATION "obs-plugins/64bit"

        #define OBS_RELEASE_CANDIDATE 0
        #define OBS_BETA 0
        """;

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

    // Materialize the two C headers the parse needs. Line endings are forced to
    // LF and the literals get their trailing newline back so the files are
    // byte-for-byte what used to be checked in.
    private void WriteShimHeaders()
    {
        Directory.CreateDirectory(_shimDir);
        File.WriteAllText(Path.Combine(_shimDir, "obs_all.h"), ObsAllHeader.ReplaceLineEndings("\n") + "\n");
        File.WriteAllText(Path.Combine(_shimDir, "obsconfig.h"), ObsConfigHeader.ReplaceLineEndings("\n") + "\n");
    }

    private void RunClangSharpStage()
    {
        if (Directory.Exists(_stagingDir))
        {
            Directory.Delete(_stagingDir, recursive: true);
        }

        Directory.CreateDirectory(_stagingDir);
        WriteShimHeaders();

        Console.WriteLine("==> Probing parsed headers...");
        var probeLog = Path.Combine(_stagingDir, "visited-files.log");
        InvokeClangSharp([LibobsDir + "/obs.h"], _stagingDir, probeLog);
        var traverse = GetVisitedHeaders(probeLog);
        Console.WriteLine($"    {traverse.Count} headers discovered");

        Console.WriteLine("==> Generating raw bindings...");
        InvokeClangSharp(traverse, _stagingDir, logFile: null);
        var fnCount = ExternDeclCountRegex.Matches(
                File.ReadAllText(Path.Combine(_stagingDir, MethodClassName + ".cs")))
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
            "-m", MethodClassName,
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
            var m = VisitingFileRegex.Match(line);
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

    [GeneratedRegex("public static extern")]
    private static partial Regex ExternDeclCountRegex { get; }

    [GeneratedRegex(@"Visiting\s+(lib/obs-studio/[\w\-/\.]+\.h)\s*$")]
    private static partial Regex VisitingFileRegex { get; }
}
