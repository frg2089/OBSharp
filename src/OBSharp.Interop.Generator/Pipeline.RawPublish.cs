using System.Text.RegularExpressions;

namespace OBSharp.Interop.Generator;

// Stage 2: raw files -> Generated/Raw, normalized for idiomatic C#:
//   - PascalCase every snake_case struct/enum/union type (declaration + every
//     reference, including pointer signatures like `obs_data*` -> `ObsData*`)
//   - IntPtr -> nint, sbyte -> byte, LibraryImport conversion
//   - C `long`/`unsigned long` -> CLong/CULong (native width differs between the
//     Windows LLP64 and Unix LP64 data models)
//   - #nullable enable on every file
internal sealed partial class Pipeline
{
    // Reserved words / collisions we must not rename into.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "String", "Object", "Int32", "Boolean", "Void", "Array", "Enum", "Delegate",
        "List", "Task", "Value", "Type", "Attribute", "SafeHandle", "IntPtr", "Exception",
    };

    // 128-bit integers map to the BCL types (defensive: the OBS headers contain
    // no __int128 today). The longer key wins for `unsigned __int128` before
    // `__int128` can match.
    private static readonly (string C, string Cs)[] Int128Types =
    [
        ("unsigned __int128", "System.UInt128"),
        ("__int128", "System.Int128"),
    ];

    // System.Numerics counterparts of the OBS math structs (C# name after the
    // PascalCase rename -> BCL type). The AOT LibraryImport generator only
    // accepts these BCL structs behind a pointer - by value, in/ref and struct
    // fields all trigger SYSLIB1051 - so the rewrite targets pointer positions
    // only:
    //   - exported-fn params/returns: `Vec4* dst` -> `System.Numerics.Vector4* dst`
    //   - struct fields keep the generated type (Vec4 stays a blittable field,
    //     which is what keeps e.g. ObsSceneItem marshalable)
    // Layout parity verified against the headers: vec2(8B)=Vector2,
    // vec4(16B)=Vector4, quat(16B)=Quaternion, matrix4(64B)=Matrix4x4.
    // vec3 stays generated: OBS's vec3 is 16 bytes (SSE padding - union with
    // __m128 exposes x,y,z,w) vs System.Numerics.Vector3's 12, so a swap would
    // let native code write w past the end of the managed struct. plane stays
    // generated: {vec3 dir; float dist} is 32 bytes native vs
    // System.Numerics.Plane's 16 with a different field order. matrix3/axisang
    // have no .NET counterpart.
    private static readonly (string Cs, string Bcl)[] ExternalPointerTypes =
    [
        ("Vec2", "System.Numerics.Vector2"),
        ("Vec4", "System.Numerics.Vector4"),
        ("Quat", "System.Numerics.Quaternion"),
        ("Matrix4", "System.Numerics.Matrix4x4"),
    ];

    // Pointer-position rewrite: a renamed math type immediately followed by a
    // star (optionally spaced). The lookbehind keeps longer identifiers intact;
    // runs after the PascalCase rename so `vec4*` -> `Vec4*` -> BCL pointer.
    private static readonly Regex ExternalPointerRegex = new(
        "(?<![A-Za-z0-9_])(?:"
        + string.Join("|", ExternalPointerTypes.Select(t => Regex.Escape(t.Cs)))
        + @")(?=\s*\*)");

    // C `long`/`unsigned long` have the native word size: 32 bits under the
    // Windows LLP64 model but 64 bits under the Unix LP64 model. ClangSharp bakes
    // the host ABI into the emitted type (`int`/`uint` on Windows, `long`/`ulong`
    // on Unix), which would make the bindings wrong on the other platform. The
    // BCL exchange types CLong/CULong carry exactly the native width on each
    // platform, so the raw layer is rewritten to them.
    //
    // The mapping keys off the [NativeTypeName("long")] annotation ClangSharp
    // emits for these declarations: a fixed-width `int64_t`/`long long` member
    // gets NativeTypeName("int64_t"/"long long") and keeps the platform-
    // independent `long`/`ulong`, so only genuine C `long` is rewritten.
    [GeneratedRegex(
        @"\[return: NativeTypeName\(""(unsigned )?long""\)\]|\[NativeTypeName\(""(unsigned )?long""\)\]")]
    private static partial Regex NativeLongAttrRegex { get; }

    // Cross-stage state: PascalCase renames consumed by the wrapper generator.
    private readonly Dictionary<string, string> _renameMap = new(StringComparer.Ordinal);
    private HashSet<string> _pascalNames = new(StringComparer.Ordinal);
    private HashSet<string> _externalCsTypes = new(StringComparer.Ordinal);
    private int _renamedPairCount;

    private void PublishRawStage()
    {
        Console.WriteLine("==> Publishing raw bindings...");
        if (Directory.Exists(_rawOutDir))
        {
            Directory.Delete(_rawOutDir, recursive: true);
        }

        Directory.CreateDirectory(_rawOutDir);

        // Pass A: collect every declared struct/enum/union name (ordinal equality,
        // like the PowerShell HashSet, then sorted like Sort-Object below).
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(_stagingDir, "*.cs"))
        {
            var txt = File.ReadAllText(path);
            foreach (Match m in TypeNameRegex.Matches(txt))
            {
                typeNames.Add(m.Groups[1].Value);
            }
        }

        // Pass B: build the rename map (snake_case -> PascalCase), resolving collisions.
        // Types that are already PascalCase (single uppercase-leading token) are kept
        // as-is; single-token lowercase names like vec2/quat still need PascalCasing.
        // Sort order mirrors Sort-Object (culture, case-insensitive): collision
        // suffixes depend on iteration order.
        var pascalSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in typeNames.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
        {
            if (PascalIdentifierRegex.IsMatch(t))
            {
                _renameMap[t] = t;
                continue;
            }

            var p = GetPascalName(t);
            if (ReservedNames.Contains(p))
            {
                p += "Type";
            }

            if (pascalSeen.ContainsKey(p))
            {
                p = p + pascalSeen[p];
                pascalSeen[p] = 1;
            }
            else
            {
                pascalSeen[p] = 1;
            }

            _renameMap[t] = p;
        }

        // External (BCL) types: the int128 pairs join the rename map so bare
        // references get rewritten; the math types keep their PascalCase rename
        // (generated structs/fields stay as before) and are remapped only in
        // pointer positions by ExternalPointerRegex. The wrapper gate keys off
        // _pascalNames (declared types) and _externalCsTypes (BCL names)
        // separately - fully-qualified names never enter _pascalNames.
        foreach (var (c, cs) in Int128Types)
        {
            _renameMap[c] = cs;
        }

        _externalCsTypes = new HashSet<string>(
            Int128Types.Select(t => t.Cs)
                .Concat(ExternalPointerTypes.Select(t => t.Bcl)),
            StringComparer.Ordinal);

        // After the rewrite, wrapper signatures reference types by their PascalCase
        // names, so lookups (blittable-struct gate, handle-class map) key off these.
        _pascalNames = new HashSet<string>(
            _renameMap.Values.Where(v => v.IndexOf('.') < 0), StringComparer.Ordinal);

        // One big regex rewrites any reference to a renamed type. Word-boundary
        // guards mean only WHOLE identifiers are renamed - substrings inside longer
        // identifiers stay untouched. Without the guards, function names like
        // obs_data_create were mangled into ObsData_create, which silently broke
        // both the native entry point (P/Invoke defaults it to the method name)
        // and the wrapper prefix grouping (ObsData_create no longer matched the
        // obs_data_ prefix and fell into the Base bucket). Longest names first so
        // `obs_data_item` wins over `obs_data`; the guards also force backtracking
        // to the longest alternative. Case-sensitive by design (vec2 != Vec2).
        var renamedPairs = _renameMap
            .Where(kv => !string.Equals(kv.Value, kv.Key, StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();
        _renamedPairCount = renamedPairs.Count;
        var typeRegex = renamedPairs.Count > 0
            ? new Regex(
                "(?<![A-Za-z0-9_])(?:" + string.Join("|", renamedPairs.Select(kv => Regex.Escape(kv.Key))) + ")(?![A-Za-z0-9_])")
            : NeverMatchRegex;

        var header =
            string.Join("\n", GeneratedFileBanner)
            + "\n"
            + "#nullable enable\n"
            + "#pragma warning disable CS8981 // type name contains only lower-cased ascii characters (native C naming)\n";

        var rawConverted = 0;
        foreach (var path in Directory.GetFiles(_stagingDir, "*.cs").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            var baseName = Path.GetFileNameWithoutExtension(path);
            var content = File.ReadAllText(path);

            // C emits constant expressions like (PTW32_FALSE == 0) as an int; C# types
            // the comparison as bool -> rewrite "int const = (bool-expr)" as a ternary.
            content = ConstBoolExprRegex.Replace(
                content,
                "public const int $1 = ($2) ? 1 : 0;");

            // Rename snake_case types everywhere they appear (declarations + references).
            // Skip the two helper-attribute files (they carry no native type names).
            if (baseName is not ("NativeTypeNameAttribute" or "NativeAnnotationAttribute"))
            {
                content = RewriteTypes(content, typeRegex);
            }

            // IntPtr -> nint (and IntPtr.Zero -> 0, covered by the following).
            content = IntPtrRegex.Replace(content, "nint");
            content = NintZeroRegex.Replace(content, "0");

            // sbyte -> byte: C char* is a UTF-8 byte pointer; C# idiom (Encoding.UTF8,
            // Span<byte>, Memory<byte>) uses byte, not sbyte.
            content = SByteRegex.Replace(content, "byte");

            // C long/unsigned long -> platform-width CLong/CULong.
            content = MapNativeLongs(content);

            content = ConvertLibraryImports(content, out var converted);
            rawConverted += converted;

            if (CLongRegex.IsMatch(content))
            {
                content = EnsureUsing(content, "using System.Runtime.InteropServices;");
            }

            content = ToFileScopedNamespace(content);

            File.WriteAllText(Path.Combine(_rawOutDir, fileName), header + content);
        }

        var rawFileCount = Directory.GetFiles(_rawOutDir).Length;
        Console.WriteLine(
            $"    {rawFileCount} raw files, {_renamedPairCount} types renamed, {rawConverted} raw externs converted to LibraryImport");
    }

    // PascalCase replacements apply only to bare identifiers outside quoted strings,
    // so NativeTypeName("...") / EntryPoint = "..." keep the original C names.
    // Split on double-quoted literals: even segments are code, odd are strings.
    private string RewriteTypes(string text, Regex typeRegex)
    {
        var parts = QuotedStringSplitRegex.Split(text);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 != 0)
            {
                continue;
            }

            parts[i] = typeRegex.Replace(
                parts[i], m => _renameMap.TryGetValue(m.Value, out var v) ? v : m.Value);

            // Pointer positions of the math structs point at the BCL types
            // (fields keep the generated names - see ExternalPointerTypes).
            parts[i] = ExternalPointerRegex.Replace(
                parts[i], m => Array.Find(ExternalPointerTypes, t => t.Cs == m.Value).Bcl);
        }

        return string.Concat(parts);
    }

    // Convert ClangSharp externs to LibraryImport partials. Only methods the AOT
    // source generator can marshal (no fn pointers, no variadics) are converted;
    // the rest keep DllImport - delegate* and __arglist are not marshalable.
    private static string ConvertLibraryImports(string content, out int converted)
    {
        converted = 0;
        var lines = LineSplitRegex.Split(content);
        var declIdxs = new HashSet<int>();
        var output = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var ln = lines[i];
            var m = DllImportAttrRegex.Match(ln);
            if (m.Success)
            {
                var ind = m.Groups[1].Value;

                // The extern declaration follows the attribute (possibly after other
                // [return:]/[Obsolete] attribute lines).
                var declIdx = -1;
                for (var j = i + 1; j < lines.Length && j <= i + 6; j++)
                {
                    if (lines[j].Contains("public static extern "))
                    {
                        declIdx = j;
                        break;
                    }

                    if (lines[j].TrimStart().StartsWith("["))
                    {
                        continue;
                    }

                    break;
                }

                if (declIdx >= 0
                    && !lines[declIdx].Contains("delegate*")
                    && !lines[declIdx].Contains("__arglist"))
                {
                    // ExactSpelling externs use the method name as entry point; the
                    // LibraryImport default matches, so no EntryPoint= is needed.
                    output.Add(ind + "[LibraryImport(\"obs\")]");
                    output.Add(ind + "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]");
                    declIdxs.Add(declIdx);
                    converted++;
                    continue;
                }
            }

            if (declIdxs.Contains(i))
            {
                ln = ln.Replace("public static extern ", "public static partial ");
            }

            output.Add(ln);
        }

        var text = string.Join("\n", output);

        // UnmanagedCallConv/CallConvCdecl live in System.Runtime.CompilerServices.
        if (converted > 0 && !CompilerServicesUsingRegex.IsMatch(text))
        {
            var ls = new List<string>(LineSplitRegex.Split(text));
            var lastUsing = -1;
            for (var k = 0; k < ls.Count; k++)
            {
                if (UsingLineRegex.IsMatch(ls[k]))
                {
                    lastUsing = k;
                }
            }

            if (lastUsing >= 0)
            {
                ls.Insert(lastUsing + 1, "using System.Runtime.CompilerServices;");
            }

            text = string.Join("\n", ls);
        }

        return text;
    }

    // Rewrites C `long`/`unsigned long` declarations to the BCL CLong/CULong
    // exchange types so the raw layer has the native width on both Windows
    // (LLP64, 32-bit long) and Unix (LP64, 64-bit long). Handles the three shapes
    // ClangSharp emits:
    //   [NativeTypeName("long")]          (own line: annotation for a field)
    //   ... [NativeTypeName("long")] int x (inline: annotation for a parameter)
    //   [return: NativeTypeName("long")]  (own line: annotation for a return)
    private static string MapNativeLongs(string content)
    {
        var lines = new List<string>(LineSplitRegex.Split(content));
        for (var i = 0; i < lines.Count; i++)
        {
            var m = NativeLongAttrRegex.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var csType = m.Groups[1].Success || m.Groups[2].Success ? "CULong" : "CLong";
            var after = lines[i][(m.Index + m.Length)..];
            if (after.Trim().Length > 0)
            {
                // Inline annotation: the (parameter or same-line field/return)
                // type follows the attribute on the same line.
                lines[i] = lines[i][..(m.Index + m.Length)]
                    + NativeLongInlineRegex.Replace(after, "$1" + csType);
            }
            else
            {
                // Own-line annotation: rewrite the following declaration's type
                // (a field, or the return type of the method for [return:]).
                ReplaceFollowingType(lines, i + 1, csType);
            }
        }

        return string.Join("\n", lines);
    }

    // Replaces the type of the next non-attribute declaration line: the return
    // type of `public static (partial|extern) T Name(...)`, or the type of a
    // `public T Name;` field. Anything else is left untouched.
    private static void ReplaceFollowingType(List<string> lines, int start, string csType)
    {
        for (var j = start; j < lines.Count; j++)
        {
            var t = lines[j].Trim();
            if (t.Length == 0 || t.StartsWith('['))
            {
                continue;
            }

            var mm = MethodTypeRegex.Match(lines[j]);
            if (mm.Success)
            {
                lines[j] = mm.Groups[1].Value + csType + lines[j][(mm.Index + mm.Length)..];
                return;
            }

            var mf = FieldTypeRegex.Match(lines[j]);
            if (mf.Success)
            {
                lines[j] = mf.Groups[1].Value + csType + lines[j][(mf.Index + mf.Length)..];
            }

            return;
        }
    }

    // Adds a using directive after the last existing one (or before the first
    // namespace when the file has none) unless it is already present.
    private static string EnsureUsing(string content, string usingDirective)
    {
        if (content.Contains(usingDirective, StringComparison.Ordinal))
        {
            return content;
        }

        var ls = new List<string>(LineSplitRegex.Split(content));
        var lastUsing = -1;
        var firstNamespace = -1;
        for (var k = 0; k < ls.Count; k++)
        {
            if (UsingLineRegex.IsMatch(ls[k]))
            {
                lastUsing = k;
            }
            else if (NamespaceLineRegex.IsMatch(ls[k]))
            {
                firstNamespace = k;
                break;
            }
        }

        var at = lastUsing >= 0 ? lastUsing + 1 : firstNamespace >= 0 ? firstNamespace : 0;
        ls.Insert(at, usingDirective);
        return string.Join("\n", ls);
    }

    // ClangSharp - and this class' own emitters - write a block-scoped namespace
    // with the whole body indented one level. Rewrite it as file-scoped (C# 10)
    // and drop that level, so every generated file matches the repo's style.
    // Files with no namespace (the ClangSharp GeneratedCode assembly attribute)
    // and anything unexpected pass through untouched.
    private static string ToFileScopedNamespace(string content)
    {
        var lines = new List<string>(LineSplitRegex.Split(content));

        var nsIdx = -1;
        var nsName = string.Empty;
        for (var i = 0; i < lines.Count; i++)
        {
            var m = NamespaceDeclRegex.Match(lines[i]);
            if (m.Success)
            {
                nsIdx = i;
                nsName = m.Groups[1].Value;
                break;
            }
        }

        if (nsIdx < 0 || nsIdx + 1 >= lines.Count || lines[nsIdx + 1].Trim() != "{")
        {
            return content;
        }

        // The namespace's closing brace is the last line with content; everything
        // after it is trailing whitespace and is left alone.
        var closeIdx = lines.Count - 1;
        while (closeIdx > nsIdx && lines[closeIdx].Trim().Length == 0)
        {
            closeIdx--;
        }

        if (lines[closeIdx].Trim() != "}")
        {
            return content;
        }

        lines[nsIdx] = "namespace " + nsName + ";";

        // The `{` line becomes the blank line that separates the namespace from
        // the first member.
        lines[nsIdx + 1] = string.Empty;

        for (var i = nsIdx + 2; i < closeIdx; i++)
        {
            if (lines[i].StartsWith("    ", StringComparison.Ordinal))
            {
                lines[i] = lines[i][4..];
            }
        }

        lines.RemoveAt(closeIdx);
        return string.Join("\n", lines);
    }

    // --- Compile-time regular expressions (SYSLIB1045) ---
    [GeneratedRegex("public (?:unsafe )?(?:readonly )?(?:partial )?(?:struct|enum|union) (\\w+)")]
    private static partial Regex TypeNameRegex { get; }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex PascalIdentifierRegex { get; }

    [GeneratedRegex("(?!)")]
    private static partial Regex NeverMatchRegex { get; }

    [GeneratedRegex(@"public const int (\w+) = \(([^;]*?(?:==|&&|\|\|)[^;]*?)\);")]
    private static partial Regex ConstBoolExprRegex { get; }

    [GeneratedRegex(@"\bIntPtr\b")]
    private static partial Regex IntPtrRegex { get; }

    [GeneratedRegex(@"nint\.Zero")]
    private static partial Regex NintZeroRegex { get; }

    [GeneratedRegex(@"\bsbyte\b")]
    private static partial Regex SByteRegex { get; }

    [GeneratedRegex(@"\bCULong\b|\bCLong\b")]
    private static partial Regex CLongRegex { get; }

    [GeneratedRegex("(\"(?:[^\"\\\\]|\\\\.)*\")")]
    private static partial Regex QuotedStringSplitRegex { get; }

    [GeneratedRegex(
        @"^(\s*)\[DllImport\(""obs"", CallingConvention = CallingConvention\.Cdecl, ExactSpelling = true\)\]\s*$")]
    private static partial Regex DllImportAttrRegex { get; }

    [GeneratedRegex(@"using System\.Runtime\.CompilerServices;")]
    private static partial Regex CompilerServicesUsingRegex { get; }

    [GeneratedRegex(@"^(\s*(?:public )?(?:static (?:partial|extern) )?)(?:int|uint|long|ulong)\b")]
    private static partial Regex NativeLongInlineRegex { get; }

    [GeneratedRegex(@"^(\s*public static (?:partial|extern) )\w+")]
    private static partial Regex MethodTypeRegex { get; }

    [GeneratedRegex(@"^(\s*public )\w+")]
    private static partial Regex FieldTypeRegex { get; }

    [GeneratedRegex(@"^\s*namespace ")]
    private static partial Regex NamespaceLineRegex { get; }

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*$")]
    private static partial Regex NamespaceDeclRegex { get; }
}
