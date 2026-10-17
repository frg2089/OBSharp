using System.Text;
using System.Text.RegularExpressions;

namespace OBSharp.PInvoke.Generator;

// Stage 3: friendly wrapper layer (Generated/Wrappers + Generated/Handles).
internal sealed partial class Pipeline
{
    // Longest-prefix grouping table; first match wins after length sort.
    private static readonly (string Prefix, string Group)[] PrefixGroupsSource =
    [
        ("obs_sceneitems_", "ObsSceneItem"),
        ("obs_sceneitem_", "ObsSceneItem"),
        ("obs_hotkeys_", "ObsHotkey"),
        ("obs_hotkey_", "ObsHotkey"),
        ("obs_source_", "ObsSource"),
        ("obs_data_", "ObsData"),
        ("obs_properties_", "ObsProperties"),
        ("obs_property_", "ObsProperty"),
        ("obs_output_", "ObsOutput"),
        ("obs_encoder_", "ObsEncoder"),
        ("obs_service_", "ObsService"),
        ("obs_transition_", "ObsTransition"),
        ("obs_canvas_", "ObsCanvas"),
        ("obs_scene_", "ObsScene"),
        ("obs_module_", "ObsModule"),
        ("obs_missing_", "ObsMissingFiles"),
        ("obs_fader_", "ObsFader"),
        ("obs_display_", "ObsDisplay"),
        ("obs_view_", "ObsView"),
        ("obs_volmeter_", "ObsVolmeter"),
        ("obs_weak_", "ObsWeak"),
        ("obs_key_", "ObsKey"),
        ("obs_", "Obs"),
        ("gs_", "Graphics"),
        ("os_", "Os"),
        ("config_", "Config"),
        ("video_", "Video"),
        ("audio_", "Audio"),
        ("dstr_", "DStr"),
        ("profiler_", "Profiler"),
        ("profile_", "Profiler"),
        ("signal_", "Signal"),
        ("proc_handler_", "ProcHandler"),
        ("calldata_", "CallData"),
        ("text_lookup_", "TextLookup"),
        ("media_remux_", "Media"),
        ("bitstream_", "Bitstream"),
        ("pthread_", "Pthread"),
        ("ptw32_", "Pthread"),
        ("sched_", "Pthread"),
        ("matrix4_", "Graphics"),
        ("matrix3_", "Graphics"),
        ("vec2_", "Graphics"),
        ("vec3_", "Graphics"),
        ("vec4_", "Graphics"),
        ("quat_", "Graphics"),
        ("axisang_", "Graphics"),
        ("compress_", "Video"),
        ("decompress_", "Video"),
        ("convert_", "Video"),
        ("source_profiler_", "SourceProfiler"),
        ("error_data_", "Error"),
        ("base_", "Base"),
        ("lexer_", "Base"),
        ("strlist_", "Base"),
        ("strref_", "Base"),
        ("utf8_", "Base"),
        ("wchar_", "Base"),
        ("valid_", "Base"),
        ("calc_", "Base"),
        ("input_", "Base"),
        ("metadata_", "Base"),
    ];

    // Function names that carry no usable prefix go to an explicit group.
    private static readonly Dictionary<string, string> ExactGroups = new(StringComparer.Ordinal)
    {
        ["blog"] = "Base",
        ["blogva"] = "Base",
        ["bcrash"] = "Base",
        ["bmalloc"] = "Base",
        ["brealloc"] = "Base",
        ["bfree"] = "Base",
        ["bmemdup"] = "Base",
        ["bnum_allocs"] = "Base",
        ["astrcmp"] = "Base",
        ["astrcmpi"] = "Base",
        ["astrstri"] = "Base",
        ["wstrcmp"] = "Base",
        ["wstrcmpi"] = "Base",
        ["wstrstri"] = "Base",
        ["wcsdepad"] = "Base",
        ["strdepad"] = "Base",
        ["pthreadCancelableWait"] = "Pthread",
        ["pthreadCancelableTimedWait"] = "Pthread",
    };

    private const string DefaultGroup = "Base";

    // --- ownership tables -----------------------------------------------------
    // The wrapper layer turns an opaque pointer into a SafeHandle only when the
    // native call hands the caller a NEW reference (ownership transfer, or an
    // incremented refcount). Everything else keeps its raw pointer on purpose:
    // wrapping a borrowed pointer (gs_get_context, obs_get_video,
    // obs_output_get_service, obs_data_get_string, ...) in an owning handle
    // would free memory the caller does not own - a use-after-free.
    //
    // Under-reporting here only leaks (the caller still holds a raw pointer they
    // can wrap by hand); over-reporting crashes. Every entry therefore has to be
    // backed by libobs evidence ("use obs_*_release", "increments the reference
    // counter", obs_data_array_item's os_atomic_inc_long, *_create, ...).
    private static readonly HashSet<string> OwnedReturns = new(StringComparer.Ordinal)
    {
        // Factories: the callee allocates, the caller destroys.
        "audio_resampler_create",
        "config_create",
        "gs_duplicator_create",
        "gs_effect_create",
        "gs_effect_create_from_file",
        "gs_indexbuffer_create",
        "gs_samplerstate_create",
        "gs_pixelshader_create",
        "gs_pixelshader_create_from_file",
        "gs_vertexshader_create",
        "gs_vertexshader_create_from_file",
        "gs_stagesurface_create",
        "gs_stagesurface_create_nv12",
        "gs_stagesurface_create_p010",
        "gs_swapchain_create",
        "gs_texrender_create",
        "gs_timer_create",
        "gs_timer_range_create",
        "gs_vertexbuffer_create",
        "gs_zstencil_create",
        "gs_render_save",
        "obs_canvas_create",
        "obs_canvas_create_private",
        "obs_data_create",
        "obs_data_create_from_json",
        "obs_data_create_from_json_file",
        "obs_data_create_from_json_file_safe",
        "obs_data_array_create",
        "obs_display_create",
        "obs_audio_encoder_create",
        "obs_video_encoder_create",
        "obs_encoder_group_create",
        "obs_fader_create",
        "obs_missing_files_create",
        "obs_output_create",
        "obs_properties_create",
        "obs_properties_create_param",
        "obs_service_create",
        "obs_service_create_private",
        "obs_source_create",
        "obs_source_create_private",
        "obs_view_create",
        "obs_volmeter_create",
        "os_cpu_usage_info_start",
        "os_inhibit_sleep_create",
        "os_process_args_create",
        "proc_handler_create",
        "profiler_name_store_create",
        "profile_snapshot_create",
        "signal_handler_create",
        "text_lookup_create",
        "video_output_create_with_frame_rate_divisor",
        "obs_module_load_locale", // text_lookup_create()'d; the module macro destroys it

        // Deserialization helpers that build a fresh object.
        "obs_load_source",
        "obs_load_private_source",
        "obs_load_canvas",
        "obs_save_source",
        "obs_save_canvas",
        "obs_save_sources",
        "obs_save_sources_filtered",
        "obs_hotkey_save",
        "obs_hotkeys_save_encoder",
        "obs_hotkeys_save_output",
        "obs_hotkeys_save_service",
        "obs_hotkeys_save_source",
        "obs_sceneitem_transition_save",
        "obs_scene_save_transform_states",
        "obs_source_backup_filters",

        // "get by name/uuid", get_ref and the weak<->strong bridges all addref.
        "obs_get_source_by_name",
        "obs_get_source_by_uuid",
        "obs_get_output_source",
        "obs_get_output_by_name",
        "obs_get_encoder_by_name",
        "obs_get_service_by_name",
        "obs_get_canvas_by_name",
        "obs_get_canvas_by_uuid",
        "obs_get_main_canvas",
        "obs_get_transition_by_name",
        "obs_get_transition_by_uuid",
        "obs_get_private_data",
        "obs_source_get_ref",
        "obs_source_get_canvas",
        "obs_source_duplicate",
        "obs_source_get_filter_by_name",
        "obs_source_get_missing_files",
        "obs_canvas_get_ref",
        "obs_canvas_get_source_by_name",
        "obs_encoder_get_ref",
        "obs_output_get_ref",
        "obs_service_get_ref",
        "obs_object_get_ref",
        "obs_transition_get_source",
        "obs_transition_get_active_source",
        "obs_canvas_get_weak_canvas",
        "obs_encoder_get_weak_encoder",
        "obs_object_get_weak_object",
        "obs_output_get_weak_output",
        "obs_service_get_weak_service",
        "obs_source_get_weak_source",
        "obs_weak_canvas_get_canvas",
        "obs_weak_encoder_get_encoder",
        "obs_weak_object_get_object",
        "obs_weak_output_get_output",
        "obs_weak_service_get_service",
        "obs_weak_source_get_source",

        // Fresh settings/defaults/property copies (data_item_get_* addrefs too).
        "obs_source_get_settings",
        "obs_source_get_private_settings",
        "obs_sceneitem_get_private_settings",
        "obs_encoder_get_settings",
        "obs_output_get_settings",
        "obs_service_get_settings",
        "obs_encoder_defaults",
        "obs_encoder_get_defaults",
        "obs_output_defaults",
        "obs_service_defaults",
        "obs_get_source_defaults",
        "obs_data_get_defaults",
        "obs_data_get_obj",
        "obs_data_get_array",
        "obs_data_get_default_obj",
        "obs_data_get_default_array",
        "obs_data_get_autoselect_obj",
        "obs_data_get_autoselect_array",
        "obs_data_item_get_obj",
        "obs_data_item_get_array",
        "obs_data_item_get_default_obj",
        "obs_data_item_get_default_array",
        "obs_data_item_get_autoselect_obj",
        "obs_data_item_get_autoselect_array",
        "obs_data_array_item",
        "obs_source_properties",
        "obs_encoder_properties",
        "obs_output_properties",
        "obs_service_properties",
        "obs_get_source_properties",
        "obs_get_encoder_properties",
        "obs_get_output_properties",
        "obs_get_service_properties",
    };

    // `T**` parameters that are pure out-slots handing back a new reference
    // (same evidence as OwnedReturns). The member exposes them as `out THandle?`
    // while the helper keeps a raw `ref nint` slot.
    //
    // Deliberately absent: the obs_data_item_* set/next/remove family, whose
    // `obs_data_item_t**` is an in/out slot for a borrowed item rather than an
    // owned out-param - those keep the raw `ref nint` shape.
    private static readonly HashSet<string> OwnedOutParams = new(StringComparer.Ordinal)
    {
        "audio_output_open",
        "config_open",
        "config_open_string",
        "gs_create",
        "media_remux_job_create",
        "obs_hotkey_pair_save",
        "os_sem_init",
        "video_output_open",
        "video_scaler_create",
    };

    private static readonly string[] WrapperFileHeader =
    [
        .. GeneratedFileBanner,
        "#nullable enable",
        "using System;",
        "using System.Runtime.CompilerServices;",
        "using System.Runtime.InteropServices;",
        "",
        "namespace OBSharp.PInvoke",
        "{",
    ];

    private (string Prefix, string Group)[] _prefixGroups = [];
    private List<ParsedFunction> _functions = [];
    private readonly Dictionary<string, HandleInfo> _handleMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _handleClassByStruct = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandleInfo> _consumeByFn = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _declHasContent = new(StringComparer.Ordinal);

    // Fn-pointer delegate registry: LibraryImport cannot marshal
    // delegate* unmanaged[Cdecl]<...>; single-level fn-pointer params become
    // [UnmanagedFunctionPointer(Cdecl)] delegate types passed via
    // [MarshalAs(UnmanagedType.FunctionPtr)].
    private readonly Dictionary<string, string> _delegateBySig = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _delegateArgs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedDelegateNames = new(StringComparer.Ordinal);
    // Method-name dedupe + entry placement keyed by extension class.
    private readonly Dictionary<string, HashSet<string>> _usedByClass = new(StringComparer.Ordinal);

    private List<string> _classOrder = [];
    private readonly Dictionary<string, List<ExtensionFn>> _classFns = new(StringComparer.Ordinal);
    private int _wrapperCount;

    private void GenerateWrappersStage()
    {
        Console.WriteLine("==> Generating wrapper layer...");

        // Stable sort: equal-length prefixes keep their source order (first wins).
        _prefixGroups = [.. PrefixGroupsSource.OrderByDescending(g => g.Prefix.Length)];

        ParseRawFunctions();
        BuildHandleMap();
        SeedReservedDelegateNames();
        GroupFunctions();
        EmitWrappers();
        EmitHandles();

        Console.WriteLine(
            $"    {_wrapperCount} wrapper members in {_classOrder.Count} extension classes, {_handleMap.Count} handle classes, {_delegateArgs.Count} callback delegates");
    }

    // --- 3a. parse every extern signature in Raw/Obs.cs ---------------------
    private void ParseRawFunctions()
    {
        var rawLines = File.ReadAllLines(Path.Combine(_rawOutDir, MethodClassName + ".cs"));
        _functions = new List<ParsedFunction>();
        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i];

            // Raw externs are converted to LibraryImport partials in stage 2; accept both.
            var mDecl = DeclSignatureRegex.Match(line);
            if (!mDecl.Success)
            {
                continue;
            }

            var attrs = new List<string>();
            for (var j = i - 1; j >= 0 && j >= i - 6; j--)
            {
                var t = rawLines[j].Trim();
                if (t.StartsWith("["))
                {
                    attrs.Insert(0, t);
                }
                else
                {
                    break;
                }
            }

            var sig = line.Substring(mDecl.Index + mDecl.Length).Trim();
            var parenOpen = sig.IndexOf('(');
            if (parenOpen < 0)
            {
                throw new InvalidOperationException($"No parameter list in: {sig}");
            }

            var head = sig[..parenOpen].Trim();

            var depth = 0;
            var parenClose = -1;
            for (var k = parenOpen; k < sig.Length; k++)
            {
                var c = sig[k];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        parenClose = k;
                        break;
                    }
                }
            }

            if (parenClose < 0)
            {
                throw new InvalidOperationException($"Unbalanced parentheses in: {sig}");
            }

            var paramsText = sig.Substring(parenOpen + 1, parenClose - parenOpen - 1);

            var hs = head.LastIndexOf(' ');
            if (hs < 0)
            {
                throw new InvalidOperationException($"Cannot split return type/name: {head}");
            }

            var retType = head[..hs].Trim();
            var fnName = head[(hs + 1)..].Trim();

            string? retNative = null;
            var obsolete = false;
            foreach (var a in attrs)
            {
                var m = ReturnNativeRegex.Match(a);
                if (m.Success)
                {
                    retNative = m.Groups[1].Value;
                }

                if (a.StartsWith("[Obsolete"))
                {
                    obsolete = true;
                }
            }

            // Depth-aware comma split (delegate* <...>, [NativeTypeName(...)] etc.).
            var parameters = new List<ParsedParam>();
            foreach (var pt in SplitTypeArgs(paramsText))
            {
                var p = pt.Trim();
                if (p.Length == 0)
                {
                    continue;
                }

                string? nat = null;
                var mp = ParamNativeRegex.Match(p);
                if (mp.Success)
                {
                    nat = mp.Groups[1].Value;
                    p = mp.Groups[2].Value.Trim();
                }

                if (p == "__arglist")
                {
                    parameters.Add(new ParsedParam(null, "__arglist", "__arglist", "__arglist"));
                    continue;
                }

                var ls = p.LastIndexOf(' ');
                if (ls < 0)
                {
                    throw new InvalidOperationException($"Cannot split parameter: {p}");
                }

                parameters.Add(new ParsedParam(nat, p[..ls].Trim(), p[(ls + 1)..].Trim(), p));
            }

            _functions.Add(new ParsedFunction(fnName, retType, retNative, obsolete, parameters));
        }

        Console.WriteLine($"    {_functions.Count} functions parsed");
    }

    // --- 3b. opaque types -> SafeHandle classes (release fn auto-detected) ----
    // An opaque type is a struct that has no members in any raw declaration.
    // pthread/ptw32 types are excluded: they are library-internal wrappers.
    private void BuildHandleMap()
    {
        foreach (var path in Directory.GetFiles(_rawOutDir, "*.cs"))
        {
            var text = File.ReadAllText(path);
            foreach (Match m in StructBodyRegex.Matches(text))
            {
                var n = m.Groups[1].Value;
                if (m.Groups[2].Value.Trim().Length > 0)
                {
                    _declHasContent[n] = true;
                }
                else if (!_declHasContent.ContainsKey(n))
                {
                    _declHasContent[n] = false;
                }
            }
        }

        var opaqueTypes = _declHasContent
            .Where(kv => !kv.Value && !PthreadTypeRegex.IsMatch(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase); // Sort-Object default

        var handleClassUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in opaqueTypes)
        {
            var cands = _functions.Where(f =>
                ReleaseSuffixRegex.IsMatch(f.Name)
                && f.RetType == "void"
                && f.Params.Count == 1
                && (f.Params[0].CsType == t + "*" || f.Params[0].CsType == t + "**")).ToList();
            if (cands.Count == 0)
            {
                continue;
            }

            // Priority: <type>_release > _destroy > _close > _free > _delete >
            // unique remaining candidate; ambiguous types get no handle at all.
            ParsedFunction? chosen = null;
            foreach (var sfx in new[] { "release", "destroy", "close", "free", "delete" })
            {
                var hit = cands.FirstOrDefault(c => c.Name == $"{t}_{sfx}");
                if (hit is not null)
                {
                    chosen = hit;
                    break;
                }
            }

            if (chosen is null)
            {
                if (cands.Count != 1)
                {
                    continue;
                }

                chosen = cands[0];
            }

            var cls = GetPascalName(t) + "Handle";
            if (!handleClassUsed.Add(cls))
            {
                var n = 2;
                while (!handleClassUsed.Add(cls + n))
                {
                    n++;
                }

                cls += n;
            }

            var info = new HandleInfo
            {
                StructName = t,
                Class = cls,
                ReleaseFn = chosen.Name,
                PtrPtr = chosen.Params[0].CsType.EndsWith("**"),
                Param = chosen.Params[0],
            };
            _handleMap[t] = info;
            _handleClassByStruct[t] = cls;
            _consumeByFn[chosen.Name] = info;

            // Non-chosen functions that release this handle type stay raw-only:
            // exposing them with a SafeHandle param would double-release.
            foreach (var c in cands)
            {
                if (c.Name != chosen.Name)
                {
                    _excluded.Add(c.Name);
                }
            }
        }

        Console.WriteLine($"    {_handleMap.Count} SafeHandle types, {_excluded.Count} ambiguous release fns excluded");
    }

    private void SeedReservedDelegateNames()
    {
        foreach (var n in _declHasContent.Keys)
        {
            _usedDelegateNames.Add(n); // raw struct/class names
        }

        foreach (var n in _handleClassByStruct.Values)
        {
            _usedDelegateNames.Add(n); // SafeHandle class names
        }

        foreach (var n in _pascalNames)
        {
            _usedDelegateNames.Add(n); // renamed struct/enum type names
        }

        foreach (var n in _externalCsTypes)
        {
            // BCL replacements extend under their short name (Vector4Extensions).
            var shortName = n[(n.LastIndexOf('.') + 1)..];
            _usedDelegateNames.Add(shortName);
            _usedDelegateNames.Add(shortName + "Extensions");
        }

        foreach (var pg in _prefixGroups)
        {
            _usedDelegateNames.Add(pg.Group + "Extensions"); // extension container classes
        }

        foreach (var n in _declHasContent.Keys)
        {
            _usedDelegateNames.Add(n + "Extensions");
        }

        _usedDelegateNames.Add(MethodClassName + "Extensions");
        _usedDelegateNames.Add("Callbacks");
    }

    private GroupInfo GetGroupInfo(string fnName)
    {
        foreach (var (prefix, group) in _prefixGroups)
        {
            if (fnName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new GroupInfo(group, fnName.Substring(prefix.Length), prefix);
            }
        }

        if (ExactGroups.TryGetValue(fnName, out var eg))
        {
            return new GroupInfo(eg, fnName, string.Empty);
        }

        return new GroupInfo(DefaultGroup, fnName, string.Empty);
    }

    private static string GetMethodName(GroupInfo gi, string fnName)
    {
        var rest = gi.Rest;
        if (rest.Length == 0)
        {
            rest = fnName;
        }

        // Remainders like "420" (decompress_420) would not be valid identifiers;
        // keep the prefix's last segment as a stem in that case.
        if (char.IsDigit(rest[0]))
        {
            var seg = gi.Prefix.TrimEnd('_');
            if (seg.Length > 0)
            {
                rest = seg.Split('_')[^1] + "_" + rest;
            }
        }

        return GetPascalName(rest);
    }

    // --- 3c. shape every function as an extension member ----------------------
    // Instance fns (first param renders as `in T`/`ref T` or an owning handle)
    // become instance extension members (ptr->Method() / handle.Method()); the
    // rest become static members either on their prefix's type (Type.Method())
    // or - when the group is not a type - on the Obs module class.
    private void GroupFunctions()
    {
        // Hand-written blog()/Log() pair lives on the Obs module class.
        EnsureClass(MethodClassName);
        AddMember(MethodClassName, new ExtensionFn(
            [
                "[LibraryImport(\"obs\", EntryPoint = \"blog\", StringMarshalling = StringMarshalling.Utf8)]",
                "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]",
            ],
            "private static partial void blog(int logLevel, string format);",
            MethodClassName,
            [],
            "public static void Log(int logLevel, string message)",
            [
                "                // blog() is variadic; escape '%' so callers can pass plain text safely.",
                "                blog(logLevel, message.Replace(\"%\", \"%%\"));",
            ]));

        foreach (var fn in _functions)
        {
            if (fn.Params.Any(p => p.CsType == "__arglist"))
            {
                continue; // variadic
            }

            if (fn.Params.Any(p => p.Native == "va_list"))
            {
                continue; // not callable from C#
            }

            if (_excluded.Contains(fn.Name))
            {
                continue;
            }

            var gi = GetGroupInfo(fn.Name);
            var method = GetMethodName(gi, fn.Name);

            FirstParamReceiver? recv;
            HandleInfo? consume = null;
            string classKey;
            if (_consumeByFn.TryGetValue(fn.Name, out var h))
            {
                consume = h;
                if (h.PtrPtr)
                {
                    // ref receivers are value-type-only (CS9300), so a ptr-ptr
                    // release disposes through a static member on the type.
                    classKey = GroupIsType(gi.Group) ? gi.Group : MethodClassName;
                }
                else
                {
                    classKey = h.StructName;
                }

                recv = null;
            }
            else
            {
                var (fnPtrParams, _) = AnalyzeFnPtrs(fn);
                recv = ClassifyFirstParam(fn, fnPtrParams);
                classKey = recv is not null ? recv.StructKey
                    : DoublePointerReceiver(fn) ?? (GroupIsType(gi.Group) ? gi.Group
                    : MethodClassName);
            }

            // One dedupe set per extension class; the raw C name is reserved so a
            // helper identifier can never clash with a member name.
            var used = EnsureClass(classKey);
            used.Add(fn.Name);
            if (!used.Add(method))
            {
                var n = 2;
                while (!used.Add(method + n))
                {
                    n++;
                }

                method += n;
            }

            ExtensionFn entry;
            if (consume is not null && consume.PtrPtr)
            {
                var pname = consume.Param.Name;
                var memberAttrs = new List<string>();
                if (fn.Obsolete)
                {
                    memberAttrs.Add("[Obsolete]");
                }

                entry = new ExtensionFn(
                    null,
                    null,
                    classKey,
                    memberAttrs,
                    $"public static void {method}(ref {consume.Class}? {pname})",
                    [$"                {pname}?.Dispose();", $"                {pname} = null;"]);
            }
            else if (consume is not null)
            {
                var memberAttrs = new List<string>();
                if (fn.Obsolete)
                {
                    memberAttrs.Add("[Obsolete]");
                }

                entry = new ExtensionFn(
                    null,
                    null,
                    consume.Class + "? self",
                    memberAttrs,
                    $"public void {method}() => self?.Dispose();",
                    null);
            }
            else
            {
                entry = BuildNativeEntry(fn, method, recv, recv is not null ? recv.BlockReceiver : classKey);
            }

            AddMember(classKey, entry);
        }
    }

    // Register an extension class (creation order = first use) and return its
    // method-name dedupe set. The module class seeds the hand-written member names.
    private HashSet<string> EnsureClass(string classKey)
    {
        if (!_usedByClass.TryGetValue(classKey, out var used))
        {
            _usedByClass[classKey] = used = new HashSet<string>(StringComparer.Ordinal);
            if (classKey == MethodClassName)
            {
                used.Add("Log");
                used.Add("NativeLog");
            }
        }

        if (!_classFns.ContainsKey(classKey))
        {
            _classOrder.Add(classKey);
            _classFns[classKey] = [];
        }

        return used;
    }

    private void AddMember(string classKey, ExtensionFn fn)
    {
        _classFns[classKey].Add(fn);
        _wrapperCount++;
    }

    // Static members land on the prefix's type only when that name really is a
    // declared struct/enum (Graphics, Os, Base, ... are not).
    private bool GroupIsType(string group) => _pascalNames.Contains(group);

    // Shared fn-pointer analysis: which delegate* params are cdecl-compatible and
    // whether the AOT source generator can marshal this signature at all.
    private static (List<ParsedParam> FnPtrParams, bool UseLibraryImport) AnalyzeFnPtrs(ParsedFunction fn)
    {
        var fnPtrParams = new List<ParsedParam>();
        var useLibraryImport = true;
        foreach (var p in fn.Params)
        {
            if (!p.CsType.StartsWith("delegate*", StringComparison.Ordinal))
            {
                continue;
            }

            if (CdeclFnPtrRegex.IsMatch(p.CsType) && !p.CsType.EndsWith(">*"))
            {
                fnPtrParams.Add(p);
            }
            else
            {
                useLibraryImport = false; // pointer-to-fnptr / non-cdecl
            }
        }

        if (FnPtrRegex.IsMatch(fn.RetType))
        {
            useLibraryImport = false;
        }

        if (!useLibraryImport)
        {
            fnPtrParams.Clear();
        }

        return (fnPtrParams, useLibraryImport);
    }

    // Mirrors RenderParam's branching for parameter 0 only: a first param that
    // renders as `in T`/`ref T` or `{Handle}?` becomes the extension receiver.
    private FirstParamReceiver? ClassifyFirstParam(ParsedFunction fn, List<ParsedParam> fnPtrParams)
    {
        if (fn.Params.Count == 0)
        {
            return null;
        }

        var p = fn.Params[0];
        if (p.Native == "const char *" || p.Native == "_Bool")
        {
            return null;
        }

        if (p.CsType is "void*" or "void**")
        {
            return null;
        }

        if (fnPtrParams.Contains(p))
        {
            return null;
        }

        // Dotted bases cover BCL replacements like System.Numerics.Vector4*.
        var mp = PointerTypeRegex.Match(p.CsType);
        if (!mp.Success)
        {
            return null;
        }

        var bt = mp.Groups[1].Value;
        if (!p.CsType.EndsWith("**", StringComparison.Ordinal)
            && _handleClassByStruct.TryGetValue(bt, out var hc))
        {
            return new FirstParamReceiver(bt, $"{hc}? self", false);
        }

        var baseRenamed = _renameMap.TryGetValue(bt, out var br) ? br : bt;
        if (_pascalNames.Contains(baseRenamed) && !p.CsType.EndsWith("**"))
        {
            var isConst = p.Native is not null && ConstPrefixRegex.IsMatch(p.Native);
            var typeName = TrailingStarRegex.Replace(p.CsType, "");
            return new FirstParamReceiver(bt, $"{(isConst ? "in" : "ref")} {typeName} self", !isConst);
        }

        // External BCL type (System.Numerics.*): same in/ref receiver, but the
        // class key is the short name (System.Numerics.Vector4 -> Vector4Extensions).
        if (_externalCsTypes.Contains(baseRenamed) && !p.CsType.EndsWith("**"))
        {
            var isConst = p.Native is not null && ConstPrefixRegex.IsMatch(p.Native);
            var typeName = TrailingStarRegex.Replace(p.CsType, "");
            var shortName = baseRenamed[(baseRenamed.LastIndexOf('.') + 1)..];
            return new FirstParamReceiver(shortName, $"{(isConst ? "in" : "ref")} {typeName} self", !isConst);
        }

        return null;
    }

    // A `T**` first parameter (T an opaque handle type) cannot become an
    // extension receiver - the callee writes through the slot - but the member
    // still belongs on T's extension class rather than the prefix group's.
    private string? DoublePointerReceiver(ParsedFunction fn)
    {
        if (fn.Params.Count == 0)
        {
            return null;
        }

        var p = fn.Params[0];
        if (!p.CsType.EndsWith("**", StringComparison.Ordinal))
        {
            return null;
        }

        var mp = PointerTypeRegex.Match(p.CsType);
        return mp.Success && _handleClassByStruct.ContainsKey(mp.Groups[1].Value) ? mp.Groups[1].Value : null;
    }

    // `T**` where T is an opaque type with a generated SafeHandle, else null.
    private string? DoublePointerHandleClass(ParsedParam p)
    {
        if (!p.CsType.EndsWith("**", StringComparison.Ordinal))
        {
            return null;
        }

        var mp = PointerTypeRegex.Match(p.CsType);
        return mp.Success && _handleClassByStruct.TryGetValue(mp.Groups[1].Value, out var hc) ? hc : null;
    }

    // A single-level pointer to a BCL-replaced struct (System.Numerics.*).
    // LibraryImport rejects those structs by value/in/ref (SYSLIB1051) but
    // accepts pointers to them, so the helper keeps the raw pointer shape while
    // the public member takes in/ref and pins via fixed (see BuildNativeEntry).
    private bool IsExternalStructParam(ParsedParam p)
    {
        if (p.CsType.EndsWith("**", StringComparison.Ordinal))
        {
            return false;
        }

        var mp = PointerTypeRegex.Match(p.CsType);
        if (!mp.Success)
        {
            return false;
        }

        var bt = mp.Groups[1].Value;
        var baseRenamed = _renameMap.TryGetValue(bt, out var br) ? br : bt;
        return _externalCsTypes.Contains(baseRenamed);
    }

    // Native entry: a private LibraryImport/DllImport helper sibling (AOT-safe,
    // source-generated; only fn-pointer shapes the generator cannot marshal keep
    // DllImport) plus the public extension member that forwards to it - extension
    // members cannot be partial (CS0751), so the P/Invoke must live beside them.
    private ExtensionFn BuildNativeEntry(
        ParsedFunction fn, string method, FirstParamReceiver? recv, string blockReceiver)
    {
        var (fnPtrParams, useLibraryImport) = AnalyzeFnPtrs(fn);
        var hasStringParam = fn.Params.Any(p => p.Native == "const char *");

        var helperAttrs = new List<string>();
        if (useLibraryImport)
        {
            helperAttrs.Add(hasStringParam
                ? $"[LibraryImport(\"obs\", EntryPoint = \"{fn.Name}\", StringMarshalling = StringMarshalling.Utf8)]"
                : $"[LibraryImport(\"obs\", EntryPoint = \"{fn.Name}\")]");
        }
        else
        {
            helperAttrs.Add(
                $"[DllImport(\"obs\", EntryPoint = \"{fn.Name}\", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]");
        }

        string retSig;
        string? retAttr;
        if (fn.RetNative == "const char *")
        {
            retSig = "string?";
            retAttr = "[return: MarshalAs(UnmanagedType.LPUTF8Str)]";
        }
        else if (fn.RetNative == "_Bool")
        {
            retSig = "bool";
            retAttr = "[return: MarshalAs(UnmanagedType.I1)]";
        }
        else if (VoidPointerRegex.IsMatch(fn.RetType))
        {
            retSig = "nint";
            retAttr = null;
        }
        else
        {
            retSig = fn.RetType;
            retAttr = fn.RetNative is not null ? $"[return: NativeTypeName(\"{fn.RetNative}\")]" : null;
        }

        // An owned pointer return is exposed as a SafeHandle on the member; the
        // private helper keeps the raw pointer shape (see OwnedReturns).
        var retBase = PointerTypeRegex.Match(retSig);
        string? ownedHandle = null;
        if (retSig == fn.RetType
            && retBase.Success
            && _handleClassByStruct.TryGetValue(retBase.Groups[1].Value, out var ownedHc)
            && OwnedReturns.Contains(fn.Name))
        {
            ownedHandle = ownedHc;
        }

        var memberRetSig = ownedHandle is null ? retSig : ownedHandle + "?";

        if (retAttr is not null)
        {
            helperAttrs.Add(retAttr);
        }

        if (fn.Obsolete)
        {
            helperAttrs.Add("[Obsolete]");
        }

        if (useLibraryImport)
        {
            helperAttrs.Add("[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]");
        }

        var helperParams = new List<string>();
        var memberParams = new List<string>();
        var callArgs = new List<string>();
        var fixedLines = new List<string>();
        var outLocals = new List<string>();
        var outAssigns = new List<string>();
        if (recv is not null)
        {
            if (recv.BlockReceiver.Contains('.'))
            {
                // BCL receiver (in/ref System.Numerics.*): pin it to the raw
                // pointer shape the helper takes; writes land in place.
                var recvType = InRefModifierRegex.Replace(recv.BlockReceiver, string.Empty);
                recvType = recvType[..^" self".Length];
                fixedLines.Add($"                fixed ({recvType}* __p_self = &self)");
                callArgs.Add("__p_self");
            }
            else
            {
                callArgs.Add(recv.ForwardRef ? "ref self" : "self");
            }
        }

        for (var i = 0; i < fn.Params.Count; i++)
        {
            var p = fn.Params[i];

            // Verified owned out-param: hand the caller a SafeHandle while the
            // helper keeps the raw `T**` slot (see OwnedOutParams).
            var outHcls = OwnedOutParams.Contains(fn.Name) ? DoublePointerHandleClass(p) : null;
            if (outHcls is not null)
            {
                outLocals.Add($"                nint __out_{p.Name} = 0;");
                outAssigns.Add(
                    $"                {p.Name} = __out_{p.Name} == 0 ? null : {outHcls}.FromOwned(__out_{p.Name});");
                helperParams.Add($"ref nint {p.Name}");
                memberParams.Add($"out {outHcls}? {p.Name}");
                callArgs.Add($"ref __out_{p.Name}");
                continue;
            }

            var external = IsExternalStructParam(p);
            var (helperText, _) = RenderParam(fn, p, useLibraryImport, fnPtrParams, method, includeMarshalAs: true, forMember: false);
            helperParams.Add(helperText);
            if (i == 0 && recv is not null)
            {
                continue; // promoted to the extension receiver
            }

            // The member is pure managed code: [MarshalAs] carries no meaning on
            // it; NativeTypeName annotations stay for parity with the old surface.
            var (memberText, memberIsRef) = RenderParam(fn, p, useLibraryImport, fnPtrParams, method, includeMarshalAs: false, forMember: true);
            memberParams.Add(memberText);
            if (external)
            {
                // in/ref BCL argument pinned to a pointer for the helper.
                var pType = TrailingStarRegex.Replace(p.CsType, "");
                fixedLines.Add($"                fixed ({pType}* __p_{p.Name} = &{p.Name})");
                callArgs.Add($"__p_{p.Name}");
            }
            else
            {
                callArgs.Add(memberIsRef ? $"ref {p.Name}" : p.Name);
            }
        }

        var memberAttrs = new List<string>();
        if (ownedHandle is null
            && retAttr is not null
            && retAttr.StartsWith("[return: NativeTypeName", StringComparison.Ordinal))
        {
            memberAttrs.Add(retAttr);
        }

        if (fn.Obsolete)
        {
            memberAttrs.Add("[Obsolete]");
        }

        if (fixedLines.Count > 0 && outLocals.Count > 0)
        {
            throw new InvalidOperationException(
                $"{fn.Name}: BCL in/ref pinning cannot be combined with owned out-params; extend BuildNativeEntry.");
        }

        var helperDeclKw = useLibraryImport ? "private static partial " : "private static extern ";
        var memberDeclKw = recv is null ? "public static " : "public "; // unnamed receiver = static members
        var call = fn.Name + "(" + string.Join(", ", callArgs) + ")";
        if (ownedHandle is not null)
        {
            // Adopt the new reference the native call handed us, so the caller
            // never has to touch the raw pointer.
            call = $"{ownedHandle}.FromOwned((nint){call})";
        }

        call += ";";
        var memberSig = memberDeclKw + memberRetSig + " " + method + "(" + string.Join(", ", memberParams) + ")";
        List<string>? memberBody = null;
        if (fixedLines.Count == 0 && outLocals.Count == 0)
        {
            memberSig += " => " + call; // no pinning needed: expression body as before
        }
        else if (fixedLines.Count > 0)
        {
            // fixed chains pin every BCL in/ref argument for the helper call.
            memberBody = [.. fixedLines, "                {"];
            memberBody.Add(memberRetSig == "void"
                ? "                    " + call
                : "                    return " + call);
            memberBody.Add("                }");
        }
        else
        {
            // Owned out-params: call into a raw local slot, then wrap it.
            memberBody = [.. outLocals];
            memberBody.Add(memberRetSig == "void"
                ? "                " + call
                : "                var __ret = " + call);
            memberBody.AddRange(outAssigns);
            if (memberRetSig != "void")
            {
                memberBody.Add("                return __ret;");
            }
        }

        return new ExtensionFn(
            helperAttrs,
            helperDeclKw + retSig + " " + fn.Name + "(" + string.Join(", ", helperParams) + ");",
            blockReceiver,
            memberAttrs,
            memberSig,
            memberBody);
    }

    // One parameter rendered either for the helper (marshalling attrs included,
    // forMember: false) or for the forwarding member (stripped, forMember:
    // true). IsRef tells the caller to pass `ref` onward - `in` params forward
    // without a keyword (probe-verified).
    private (string Text, bool IsRef) RenderParam(
        ParsedFunction fn,
        ParsedParam p,
        bool useLibraryImport,
        List<ParsedParam> fnPtrParams,
        string methodName,
        bool includeMarshalAs,
        bool forMember)
    {
        if (p.Native == "const char *")
        {
            return (includeMarshalAs && !useLibraryImport
                ? $"[MarshalAs(UnmanagedType.LPUTF8Str)] string? {p.Name}"
                : $"string? {p.Name}", false);
        }

        if (p.Native == "_Bool")
        {
            return (includeMarshalAs ? $"[MarshalAs(UnmanagedType.I1)] bool {p.Name}" : $"bool {p.Name}", false);
        }

        if (p.CsType == "void*")
        {
            return ($"nint {p.Name}", false);
        }

        if (p.CsType == "void**")
        {
            return ($"ref nint {p.Name}", true);
        }

        if (fnPtrParams.Contains(p))
        {
            // delegate* unmanaged[Cdecl]<...> -> generated callback delegate
            // (nullable: many C callbacks accept NULL to disconnect/reset).
            var dlName = GetDelegateName(p, p.CsType, methodName);
            var natAttr = p.Native is not null ? $"[NativeTypeName(\"{p.Native}\")] " : string.Empty;
            var cast = includeMarshalAs ? "[MarshalAs(UnmanagedType.FunctionPtr)] " : "";
            return ($"{natAttr}{cast}{dlName}? {p.Name}", false);
        }

        string? hcls = null;
        var isPtr = false;
        string? baseRenamed = null;
        var mp = PointerTypeRegex.Match(p.CsType);
        if (mp.Success)
        {
            var bt = mp.Groups[1].Value;
            isPtr = true;
            if (_handleClassByStruct.TryGetValue(bt, out var hc))
            {
                hcls = hc;
            }

            // Signatures reference the post-rename (PascalCase) names; the
            // map resolves them for the blittable-struct gate below.
            baseRenamed = _renameMap.TryGetValue(bt, out var br) ? br : bt;
        }

        if (hcls is not null)
        {
            if (p.CsType.EndsWith("**", StringComparison.Ordinal))
            {
                // A pointer to a pointer is a slot the callee may write through;
                // a SafeHandle cannot stand in for it, so keep it raw.
                return ($"ref nint {p.Name}", true);
            }

            // Owning handle: pass the SafeHandle so lifetime is managed.
            return ($"{hcls}? {p.Name}", false);
        }

        if (isPtr
            && (_pascalNames.Contains(baseRenamed!) || _externalCsTypes.Contains(baseRenamed!)))
        {
            // Pointer to a blittable struct: map to in/out/ref by const.
            //   const T*  -> in   (read-only, callee does not write)
            //   T*        -> ref  (in/out, callee may read and write)
            // Single-level pointers only; T** stays a raw pointer.
            if (p.CsType.EndsWith("**"))
            {
                return (p.Text, false);
            }

            var isConst = p.Native is not null && ConstPrefixRegex.IsMatch(p.Native);

            // Helper side of a BCL-replaced struct: keep the raw pointer shape
            // (in/ref System.Numerics.* is SYSLIB1051 in LibraryImport). The
            // member side falls through to in/ref and pins via fixed.
            if (!forMember && _externalCsTypes.Contains(baseRenamed!))
            {
                return (p.Native is not null
                    ? $"[NativeTypeName(\"{p.Native}\")] {p.Text}"
                    : p.Text, false);
            }

            return ($"{(isConst ? "in" : "ref")} {TrailingStarRegex.Replace(p.CsType, "")} {p.Name}", !isConst);
        }

        if (p.Native is not null)
        {
            return ($"[NativeTypeName(\"{p.Native}\")] {p.Text}", false);
        }

        return (p.Text, false);
    }

    private string GetDelegateName(ParsedParam param, string sig, string methodName)
    {
        if (_delegateBySig.TryGetValue(sig, out var existing))
        {
            return existing;
        }

        string @base;
        if (param.Native is not null && IdentifierRegex.IsMatch(param.Native))
        {
            // Typedef name (audio_output_callback_t, obs_hotkey_func, ...) wins.
            var tn = param.Native;
            if (tn.EndsWith("_t", StringComparison.Ordinal))
            {
                tn = tn[..^2];
            }

            @base = GetPascalName(tn);
        }
        else
        {
            // Inline signature void (*)(...): name after the owning method.
            @base = methodName + "Callback";
        }

        var name = @base;
        if (!_usedDelegateNames.Add(name))
        {
            var alt = @base + GetPascalName(param.Name);
            if (!_usedDelegateNames.Add(alt))
            {
                var n = 2;
                while (!_usedDelegateNames.Add(@base + n))
                {
                    n++;
                }

                alt = @base + n;
            }

            name = alt;
        }

        var lt = sig.IndexOf('<');
        var gt = sig.LastIndexOf('>');
        _delegateBySig[sig] = name;
        _delegateArgs[name] = sig.Substring(lt + 1, gt - lt - 1);
        return name;
    }

    // --- 3d. emit Wrappers/*.cs ----------------------------------------------
    // One file per extension class: P/Invoke helper siblings first, then the
    // extension blocks (one per receiver) that forward to them.
    private void EmitWrappers()
    {
        if (Directory.Exists(_wrapperOutDir))
        {
            Directory.Delete(_wrapperOutDir, recursive: true);
        }

        Directory.CreateDirectory(_wrapperOutDir);

        foreach (var key in _classOrder)
        {
            var lines = new List<string>(WrapperFileHeader);
            lines.Add($"    public static unsafe partial class {key}Extensions");
            lines.Add("    {");
            var needSep = false;

            // 1) helpers (deterministic: creation order).
            foreach (var fn in _classFns[key])
            {
                if (fn.HelperSig is null)
                {
                    continue;
                }

                if (needSep)
                {
                    lines.Add(string.Empty);
                }

                needSep = true;
                foreach (var a in fn.HelperAttrs!)
                {
                    lines.Add("        " + a);
                }

                lines.Add("        " + fn.HelperSig);
            }

            // 2) extension blocks, one per receiver, in first-use order.
            foreach (var block in _classFns[key].GroupBy(f => f.BlockReceiver))
            {
                if (needSep)
                {
                    lines.Add(string.Empty);
                }

                needSep = true;
                lines.Add($"        extension({block.Key})");
                lines.Add("        {");
                var firstMember = true;
                foreach (var fn in block)
                {
                    if (!firstMember)
                    {
                        lines.Add(string.Empty);
                    }

                    firstMember = false;
                    foreach (var a in fn.MemberAttrs)
                    {
                        lines.Add("            " + a);
                    }

                    lines.Add("            " + fn.MemberSig);
                    if (fn.MemberBody is not null)
                    {
                        lines.Add("            {");
                        lines.AddRange(fn.MemberBody);
                        lines.Add("            }");
                    }
                }

                lines.Add("        }");
            }

            TrimTrailingEmpty(lines);
            lines.Add("    }");
            lines.Add("}");
            WriteWrapperFile(Path.Combine(_wrapperOutDir, key + "Extensions.cs"), lines);
        }

        EmitCallbacks();
    }

    // Every emitter below builds a block-scoped namespace with the body indented
    // one level; this flattens it to a file-scoped namespace on the way out.
    private static void WriteWrapperFile(string path, List<string> lines) =>
        File.WriteAllText(path, ToFileScopedNamespace(string.Join("\n", lines)));

    // Callback delegates for fn-pointer params (see the registry in
    // GenerateWrappersStage); void* maps to nint like everywhere else,
    // everything else stays raw (byte* callbacks are native data).
    private void EmitCallbacks()
    {
        if (_delegateArgs.Count == 0)
        {
            return;
        }

        var lines = new List<string>(WrapperFileHeader);
        lines.Add("    // Callback types for fn-pointer parameters ([MarshalAs(UnmanagedType.FunctionPtr)]).");
        var firstDelegate = true;
        foreach (var name in _delegateArgs.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase)) // Sort-Object default
        {
            var argList = SplitTypeArgs(_delegateArgs[name]);
            var retT = argList[^1];
            var parTypes = new List<string>();
            if (argList.Count > 1)
            {
                parTypes.AddRange(argList.GetRange(0, argList.Count - 1));
            }

            parTypes = parTypes.Select(t => t == "void*" ? "nint" : t).ToList();
            if (retT == "void*")
            {
                retT = "nint";
            }

            var sigText = string.Concat(parTypes.Select((t, i) => ", " + t + " arg" + i));
            var isUnsafe = StarRegex.IsMatch(string.Join(" ", parTypes) + " " + retT);
            if (!firstDelegate)
            {
                lines.Add(string.Empty);
            }

            firstDelegate = false;
            lines.Add("    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]");
            lines.Add(
                $"    public {(isUnsafe ? "unsafe " : "")}delegate {retT} {name}({(sigText.Length > 0 ? sigText[2..] : string.Empty)});");
        }

        TrimTrailingEmpty(lines);
        lines.Add("}");
        WriteWrapperFile(Path.Combine(_wrapperOutDir, "Callbacks.cs"), lines);
    }

    // --- 3e. emit Handles/*.cs ------------------------------------------------
    private void EmitHandles()
    {
        if (Directory.Exists(_handleOutDir))
        {
            Directory.Delete(_handleOutDir, recursive: true);
        }

        Directory.CreateDirectory(_handleOutDir);

        foreach (var (t, h) in _handleMap)
        {
            var lines = new List<string>(WrapperFileHeader);
            lines.Add($"    public sealed unsafe partial class {h.Class} : SafeHandle");
            lines.Add("    {");
            lines.Add("        private readonly bool _owned;");
            lines.Add(string.Empty);
            lines.Add($"        private {h.Class}(nint preexistingHandle, bool owned) : base(0, ownsHandle: true)");
            lines.Add("        {");
            lines.Add("            SetHandle(preexistingHandle);");
            lines.Add("            _owned = owned;");
            lines.Add("        }");
            lines.Add(string.Empty);
            lines.Add($"        public static {h.Class} FromOwned(nint ptr) => new(ptr, true);");
            lines.Add($"        public static {h.Class} FromBorrowed(nint ptr) => new(ptr, false);");
            lines.Add(string.Empty);
            lines.Add("        public override bool IsInvalid => handle == 0;");
            lines.Add(string.Empty);
            lines.Add("        protected override bool ReleaseHandle()");
            lines.Add("        {");
            lines.Add("            if (!_owned)");
            lines.Add("                return true;");
            if (h.PtrPtr)
            {
                // e.g. obs_data_item_release(&local): hand the native function a local
                // pointer slot so it can release and null it out.
                lines.Add($"            var local = ({t}*)handle;");
                lines.Add($"            {h.ReleaseFn}(&local);");
            }
            else
            {
                lines.Add($"            {h.ReleaseFn}(handle);");
            }

            lines.Add("            return true;");
            lines.Add("        }");
            lines.Add(string.Empty);
            lines.Add($"        [LibraryImport(\"obs\", EntryPoint = \"{h.ReleaseFn}\")]");
            lines.Add("        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]");
            if (h.PtrPtr)
            {
                lines.Add($"        private static partial void {h.ReleaseFn}({t}** {h.Param.Name});");
            }
            else
            {
                lines.Add($"        private static partial void {h.ReleaseFn}(nint {h.Param.Name});");
            }

            lines.Add("    }");
            lines.Add("}");
            WriteWrapperFile(Path.Combine(_handleOutDir, h.Class + ".cs"), lines);
        }
    }

    // --- Compile-time regular expressions (SYSLIB1045) ---
    [GeneratedRegex("public static (?:extern|partial) ")]
    private static partial Regex DeclSignatureRegex { get; }

    [GeneratedRegex(@"^\[return: NativeTypeName\(""([^""]+)""\)\]")]
    private static partial Regex ReturnNativeRegex { get; }

    [GeneratedRegex(@"^\[NativeTypeName\(""([^""]+)""\)\]\s*(.*)$")]
    private static partial Regex ParamNativeRegex { get; }

    [GeneratedRegex("public (?:unsafe )?partial struct (\\w+)\\s*\\{(.*?)\\}", RegexOptions.Singleline)]
    private static partial Regex StructBodyRegex { get; }

    [GeneratedRegex("^(pthread|ptw32)_")]
    private static partial Regex PthreadTypeRegex { get; }

    [GeneratedRegex("_(release|destroy|close|free|delete)$")]
    private static partial Regex ReleaseSuffixRegex { get; }

    [GeneratedRegex(@"^delegate\* unmanaged\[Cdecl\]<.+>$")]
    private static partial Regex CdeclFnPtrRegex { get; }

    [GeneratedRegex(@"^delegate\*")]
    private static partial Regex FnPtrRegex { get; }

    [GeneratedRegex(@"^((?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*)\*+$")]
    private static partial Regex PointerTypeRegex { get; }

    [GeneratedRegex("^const ")]
    private static partial Regex ConstPrefixRegex { get; }

    [GeneratedRegex(@"\*$")]
    private static partial Regex TrailingStarRegex { get; }

    [GeneratedRegex(@"^void\*+$")]
    private static partial Regex VoidPointerRegex { get; }

    [GeneratedRegex(@"^(?:in|ref) ")]
    private static partial Regex InRefModifierRegex { get; }

    [GeneratedRegex("^[A-Za-z_]\\w*$")]
    private static partial Regex IdentifierRegex { get; }

    [GeneratedRegex(@"\*")]
    private static partial Regex StarRegex { get; }
}
