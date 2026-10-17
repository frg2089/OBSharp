using System.Text.RegularExpressions;

namespace OBSharp.Interop.Generator;

// Stage 4: system-type compat shims + build verification.
internal sealed partial class Pipeline
{
    // Known layouts for system-header types the raw layer references but never
    // defines (they live in CRT headers outside our traversal). All are used as
    // pointers only, never by value (verified against the raw signatures).
    private static readonly Dictionary<string, string[]> ShimLayouts = new(StringComparer.Ordinal)
    {
        ["timespec"] = ["public long tv_sec;", "public int tv_nsec;"], // MSVC x64: 16 bytes
        ["__m128"] = ["public fixed float elements[4];"], // 16 bytes, SSE register
        ["stat"] = ["public fixed byte _buffer[256];"], // layout varies; oversized buffer
        ["_iobuf"] = [], // FILE: opaque, pass-through only
    };

    private void VerifyBuildStage()
    {
        Console.WriteLine("==> Verifying build...");
        var csproj = Path.Combine(_repoRoot, "src", "OBSharp.Interop", "OBSharp.Interop.csproj");
        var compatPath = Path.Combine(_rawOutDir, "Compat.cs");

        var allRawText = string.Join(
            "\n", Directory.GetFiles(_rawOutDir, "*.cs").OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        var declNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DeclaredTypeRegex.Matches(allRawText))
        {
            declNames.Add(m.Groups[1].Value);
        }

        // Text without 'using' lines, for "does raw actually reference this name?" checks.
        var allRawLines = LineSplitRegex.Split(allRawText);
        var rawSigText = string.Join("\n", allRawLines.Where(l => !UsingDirectiveRegex.IsMatch(l)));

        // Only extern signatures matter for the by-value guard: a shim used as a
        // nested struct field (e.g. __m128 in a union) is layout, not a marshalling
        // boundary.
        var externSigText = string.Join(
            "\n", allRawLines.Where(l => ExternOrPartialRegex.IsMatch(l)));

        var shimmed = new HashSet<string>(StringComparer.Ordinal);
        var buildOk = false;
        var lastErrors = new List<string>();

        for (var iter = 1; iter <= 3; iter++)
        {
            var (_, outLines) = RunProcess(
                "dotnet", ["build", csproj, "-v", "q", "--nologo"], _repoRoot, echo: false);

            // Match any diagnostic ID (CS0246, SYSLIB1051, ...): restricting to
            // `error CS\d+:` would let non-CS failures report a false "build OK".
            var errLines = outLines.Where(l => ErrorDiagnosticRegex.IsMatch(l)).ToList();
            var warnCount = outLines.Count(l => WarningDiagnosticRegex.IsMatch(l));
            lastErrors = errLines;
            if (errLines.Count == 0)
            {
                Console.WriteLine($"    build OK ({warnCount} warnings)");
                buildOk = true;
                break;
            }

            var candidates = new List<string>();
            foreach (var e in errLines)
            {
                if (!e.Contains("error CS0246"))
                {
                    continue;
                }

                string? nm = null;
                var mq = QuotedIdentifierRegex.Match(e);
                if (mq.Success)
                {
                    nm = mq.Groups[1].Value;
                }

                if (nm is null || nm.Contains('.'))
                {
                    continue;
                }

                if (declNames.Contains(nm) || shimmed.Contains(nm))
                {
                    continue;
                }

                if (!candidates.Contains(nm))
                {
                    candidates.Add(nm);
                }
            }

            if (candidates.Count == 0)
            {
                break; // errors we cannot auto-fix
            }

            foreach (var nm in candidates)
            {
                if (!Regex.IsMatch(rawSigText, "(?<![\\w.])" + Regex.Escape(nm) + "(?![\\w])"))
                {
                    throw new InvalidOperationException(
                        $"Type '{nm}' is unresolved in generated code but never referenced by raw bindings - wrapper generator bug.");
                }

                if (Regex.IsMatch(externSigText, "(?<![\\w])" + Regex.Escape(nm) + "(?! ?\\*)\\s+\\w"))
                {
                    throw new InvalidOperationException(
                        $"System type '{nm}' is used by value in raw signatures; it cannot be shimmed as a buffer.");
                }

                shimmed.Add(nm);
                declNames.Add(nm);
            }

            List<string> cl =
            [
                .. GeneratedFileBanner,
                "// Shims for system-header types referenced by raw signatures (pointer-only).",
                "#pragma warning disable CS8981 // type name contains only lower-cased ascii characters (native C naming)",
                "#nullable enable",
                "namespace OBSharp.Interop",
                "{",
            ];
            foreach (var nm in shimmed.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) // Sort-Object default
            {
                cl.Add("    public unsafe partial struct " + nm);
                cl.Add("    {");
                if (ShimLayouts.TryGetValue(nm, out var fields))
                {
                    foreach (var f in fields)
                    {
                        cl.Add("        " + f);
                    }
                }

                cl.Add("    }");
            }

            cl.Add("}");
            File.WriteAllText(compatPath, ToFileScopedNamespace(string.Join("\n", cl)));
            Console.WriteLine(
                "    shimming system types: " + string.Join(", ", candidates.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)));
        }

        if (!buildOk)
        {
            throw new InvalidOperationException(
                "Build failed after shimming.\n" + string.Join("\n", lastErrors.Take(10)));
        }
    }

    [GeneratedRegex("public (?:unsafe )?(?:partial )?(?:struct|class|enum|interface) (\\w+)")]
    private static partial Regex DeclaredTypeRegex { get; }

    // Compiler messages quote the missing type name with ASCII or curly quotes.
    [GeneratedRegex("[\"'\u2018\u2019\u201C\u201D](\\w+)[\"'\u2018\u2019\u201C\u201D]")]
    private static partial Regex QuotedIdentifierRegex { get; }

    [GeneratedRegex(@"public static (?:extern|partial)")]
    private static partial Regex ExternOrPartialRegex { get; }

    [GeneratedRegex(@"^\s*using\s")]
    private static partial Regex UsingDirectiveRegex { get; }

    [GeneratedRegex(": error ")]
    private static partial Regex ErrorDiagnosticRegex { get; }

    [GeneratedRegex(": warning ")]
    private static partial Regex WarningDiagnosticRegex { get; }
}
