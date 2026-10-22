using Humanizer;

namespace OBSharp.Interop.Generator;

/// <summary>
/// 命名 / 归属 / 所有权规则。机械可判定的部分全在这里，例外走 override 表。
/// </summary>
internal static class Naming
{
    /// <summary>
    /// 函数名家族段 → 句柄类（这些函数归属该句柄）。
    /// </summary>
    public static readonly Dictionary<string, string> FamilyHandles = new(StringComparer.Ordinal)
    {
        ["widget"] = "QWidget",
        ["cef_widget"] = "QCefWidget",
        ["qcef"] = "QCef",
        ["filepicker"] = "QFileDialog",
        ["response"] = "CefResponse",
        ["resource_handler"] = "ResourceHandler",
    };

    /// <summary>
    /// 函数名家族段 → 静态分组类（无 this 的自由函数按此归组）。
    /// </summary>
    public static readonly Dictionary<string, string> FamilyGroups = new(StringComparer.Ordinal)
    {
        ["window"] = "Window",
        ["application"] = "Application",
        ["scheme"] = "Scheme",
        ["host"] = "Host",
        ["logger"] = "Logger",
        ["thread"] = "Thread",
        ["obsharp"] = "Obsharp",
        ["native"] = "Obsharp",
    };

    /// <summary>
    /// 头文件 stem → 静态分组类（家族段匹配不上时的兜底）。
    /// </summary>
    public static readonly Dictionary<string, string> HeaderGroups =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["obsharp"] = "Obsharp",
        ["obs"] = "Obs",
        ["obs-frontend-api"] = "ObsFrontend",
        ["pthread"] = "Pthread",
        ["w32-pthreads"] = "Pthread",
    };

    /// <summary>
    /// 头文件解析不完整时的基类兜底（否则按 AST 推断）。
    /// </summary>
    public static readonly Dictionary<string, string> FallbackBases = new(StringComparer.Ordinal)
    {
        ["QCefWidget"] = "QWidget",
        ["QFileDialog"] = "QWidget",
        ["QWidget"] = "QObject",
    };

    /// <summary>
    /// C++ 记录类型 → .NET 等价类型。命中后不再生成对应 struct，
    /// 引用处直接用平台类型（--type-alias 可增删）。
    /// </summary>
    public static readonly Dictionary<string, string> TypeAliases = new(StringComparer.Ordinal)
    {
        ["vec2"] = "global::System.Numerics.Vector2",
        ["vec3"] = "global::System.Numerics.Vector3",
        ["vec4"] = "global::System.Numerics.Vector4",
        ["matrix3"] = "global::System.Numerics.Matrix3x2",
        ["matrix4"] = "global::System.Numerics.Matrix4x4",
    };

    /// <summary>
    /// C++ 记录名的 .NET 等价类型名；没有别名返回 null。
    /// </summary>
    public static string? TypeAlias(string cppName)
        => TypeAliases.GetValueOrDefault(cppName);

    /// <summary>给定 C# 类型名是否是别名类型（System.Numerics 等）。</summary>
    public static bool IsAliasType(string csTypeName)
        => TypeAliases.Values.Contains(csTypeName, StringComparer.Ordinal);

    /// <summary>
    /// 销毁函数名后缀（识别 *_destroy 系）。
    /// </summary>
    public static readonly string[] DestroySuffixes = ["_destroy", "_delete", "_free", "_release"];

    /// <summary>
    /// 函数名含这些片段且存在对应销毁函数时，返回的句柄归调用方所有。
    /// </summary>
    public static readonly string[] OwnedReturnMarkers = ["_create", "_new", "_open"];

    /// <summary>
    /// 返回句柄归调用方所有的例外（create 标记之外）。libobs 的 addref / 反序列化 /
    /// 设置拷贝系全在这里，数据来自旧生成器的 OwnedReturns（依据：libobs 头注释与实现）。
    /// </summary>
    public static readonly HashSet<string> OwnedReturnOverrides =
    [
        with(StringComparer.Ordinal),
        // 工厂：被调方分配、调用方销毁（名字不带 create 的）
        "audio_resampler_create", "config_create", "gs_duplicator_create", "gs_effect_create",
        "gs_effect_create_from_file", "gs_indexbuffer_create", "gs_samplerstate_create",
        "gs_pixelshader_create", "gs_pixelshader_create_from_file", "gs_vertexshader_create",
        "gs_vertexshader_create_from_file", "gs_stagesurface_create", "gs_stagesurface_create_nv12",
        "gs_stagesurface_create_p010", "gs_swapchain_create", "gs_texrender_create", "gs_timer_create",
        "gs_timer_range_create", "gs_vertexbuffer_create", "gs_zstencil_create", "gs_render_save",
        "obs_canvas_create", "obs_canvas_create_private", "obs_data_create", "obs_data_create_from_json",
        "obs_data_create_from_json_file", "obs_data_create_from_json_file_safe", "obs_data_array_create",
        "obs_display_create", "obs_audio_encoder_create", "obs_video_encoder_create",
        "obs_encoder_group_create", "obs_fader_create", "obs_missing_files_create", "obs_output_create",
        "obs_properties_create", "obs_properties_create_param", "obs_service_create",
        "obs_service_create_private", "obs_source_create", "obs_source_create_private", "obs_view_create",
        "obs_volmeter_create", "os_cpu_usage_info_start", "os_inhibit_sleep_create", "os_process_args_create",
        "proc_handler_create", "profiler_name_store_create", "profile_snapshot_create",
        "signal_handler_create", "text_lookup_create", "video_output_create_with_frame_rate_divisor",
        "obs_module_load_locale",

        // 反序列化：每次都是新对象
        "obs_load_source", "obs_load_canvas", "obs_save_source", "obs_save_canvas", "obs_save_sources",
        "obs_save_sources_filtered", "obs_hotkey_save", "obs_hotkeys_save_encoder", "obs_hotkeys_save_output",
        "obs_hotkeys_save_service", "obs_hotkeys_save_source", "obs_sceneitem_transition_save",
        "obs_scene_save_transform_states", "obs_source_backup_filters",

        // 按名/uuid 取、get_ref、weak↔strong 桥：都 addref
        "obs_get_source_by_name", "obs_get_source_by_uuid", "obs_get_output_source", "obs_get_output_by_name",
        "obs_get_encoder_by_name", "obs_get_service_by_name", "obs_get_canvas_by_name", "obs_get_canvas_by_uuid",
        "obs_get_main_canvas", "obs_get_transition_by_name", "obs_get_transition_by_uuid", "obs_get_private_data",
        "obs_source_get_ref", "obs_source_get_canvas", "obs_source_duplicate", "obs_source_get_filter_by_name",
        "obs_source_get_missing_files", "obs_canvas_get_ref", "obs_canvas_get_source_by_name",
        "obs_encoder_get_ref", "obs_output_get_ref", "obs_service_get_ref", "obs_object_get_ref",
        "obs_transition_get_source", "obs_transition_get_active_source", "obs_canvas_get_weak_canvas",
        "obs_encoder_get_weak_encoder", "obs_object_get_weak_object", "obs_output_get_weak_output",
        "obs_service_get_weak_service", "obs_source_get_weak_source", "obs_weak_canvas_get_canvas",
        "obs_weak_encoder_get_encoder", "obs_weak_object_get_object", "obs_weak_output_get_output",
        "obs_weak_service_get_service", "obs_weak_source_get_source",

        // 新设置/默认/属性拷贝（data_item_get_* 也 addref）
        "obs_source_get_settings", "obs_source_get_private_settings", "obs_sceneitem_get_private_settings",
        "obs_encoder_get_settings", "obs_output_get_settings", "obs_service_get_settings",
        "obs_encoder_defaults", "obs_encoder_get_defaults", "obs_output_defaults", "obs_service_defaults",
        "obs_get_source_defaults", "obs_data_get_defaults", "obs_data_get_obj", "obs_data_get_array",
        "obs_data_get_default_obj", "obs_data_get_default_array", "obs_data_get_autoselect_obj",
        "obs_data_get_autoselect_array", "obs_data_item_get_obj", "obs_data_item_get_array",
        "obs_data_item_get_default_obj", "obs_data_item_get_default_array", "obs_data_item_get_autoselect_obj",
        "obs_data_item_get_autoselect_array", "obs_data_array_item", "obs_source_properties",
        "obs_encoder_properties", "obs_output_properties", "obs_service_properties",
        "obs_get_source_properties", "obs_get_encoder_properties", "obs_get_output_properties",
        "obs_get_service_properties",

        // 前端 API：对象 getter 回新引用（config_t 访问器是借用，不在此列）
        "obs_frontend_get_current_scene", "obs_frontend_get_current_transition",
        "obs_frontend_get_current_preview_scene", "obs_frontend_get_streaming_output",
        "obs_frontend_get_recording_output", "obs_frontend_get_replay_buffer_output",
        "obs_frontend_get_virtualcam_output", "obs_frontend_add_canvas",
    ];

    private static readonly HashSet<string> ReservedWords =
    [
        with(StringComparer.Ordinal),
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    ];

    // ---- 动态家族段（从句柄的 C++ 名和分组类名来） ----

    private static readonly List<string> Families = [];
    private static readonly Dictionary<string, string> FamilyClasses = [with(StringComparer.Ordinal)];

    /// <summary>
    /// 解析开始前调用：把句柄的 C++ 名（obs_source 等）登记为家族段。
    /// </summary>
    public static void Initialize(Model model)
    {
        Families.Clear();
        FamilyClasses.Clear();

        foreach (var (family, cls) in FamilyHandles)
            FamilyClasses[family] = cls;

        foreach (var handle in model.Handles.Values)
        {
            FamilyClasses[handle.NativeName] = handle.Name;
            if (handle.NativeName.Contains('_') || char.IsLower(handle.NativeName[0]))
                Families.Add(handle.NativeName);
        }

        // 布局结构体也是家族段（dstr_copy → DstrExtension.Copy）
        foreach (var structDecl in model.Structs.Values)
        {
            if (structDecl.Name.Contains('_') || char.IsLower(structDecl.Name[0]))
                Families.Add(structDecl.Name);
        }

        foreach (var family in FamilyHandles.Keys.Concat(FamilyGroups.Keys))
            Families.Add(family);

        foreach (var group in HeaderGroups.Values)
            Families.Add(Snake(group));

        Families.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    /// <summary>
    /// 家族段 → 句柄类（含动态表）。
    /// </summary>
    public static string? FamilyClass(string family)
        => FamilyClasses.GetValueOrDefault(family)
            ?? FamilyHandles.GetValueOrDefault(family);

    /// <summary>
    /// 函数名（已去 obsharp_ 前缀）里最长的家族段；没有则 null。
    /// </summary>
    public static string? FamilyOf(string bareName)
    {
        foreach (var family in Families) // 已按长度降序
        {
            if (bareName.StartsWith(family + "_", StringComparison.Ordinal))
                return family;
        }

        return null;
    }

    /// <summary>
    /// snake_case → PascalCase。
    /// </summary>
    public static string Pascal(string name)
        => string.IsNullOrEmpty(name) ? name : name.Humanize().Pascalize();

    /// <summary>
    /// C++ 类型名 → C# 类型名：snake/小写/带空格的 Pascal 化，其余原样（QWidget 等）。
    /// </summary>
    public static string TypeName(string cppName)
    {
        if (cppName.Length == 0)
            return cppName;
        if (!cppName.Contains('_') && !cppName.Contains(' ') && !char.IsLower(cppName[0]))
            return cppName;
        return Pascal(cppName).Replace(" ", "");
    }

    /// <summary>
    /// Pascal 名 → snake（ObsFrontend → obs_frontend）。
    /// </summary>
    public static string Snake(string pascal)
    {
        if (pascal.Length == 0)
            return pascal;
        var chars = new List<char> { char.ToLowerInvariant(pascal[0]) };
        foreach (var c in pascal[1..])
        {
            if (char.IsUpper(c))
            {
                chars.Add('_');
                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(c);
            }
        }

        return new string([.. chars]);
    }

    /// <summary>
    /// C 参数名/字段名 → 合法 C# 标识符（Pascal 化，避开保留字）。
    /// </summary>
    public static string Identifier(string name)
    {
        var id = Pascal(name).Replace(" ", "");
        if (id.Length == 0)
            id = "arg";
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return ReservedWords.Contains(id) ? "@" + id : id;
    }

    /// <summary>
    /// 方法参数名 → camelCase（避开保留字；生成的临时变量用 <see cref="Identifier"/>）。
    /// </summary>
    public static string CamelIdentifier(string name)
    {
        var id = Pascal(name).Replace(" ", "");
        if (id.Length == 0)
            id = "arg";
        if (char.IsDigit(id[0]))
            return "_" + id;
        id = char.ToLowerInvariant(id[0]) + id[1..];
        return ReservedWords.Contains(id) ? "@" + id : id;
    }

    /// <summary>
    /// 头文件 stem → 静态分组类名。
    /// </summary>
    public static string GroupClass(string headerStem)
        => HeaderGroups.TryGetValue(headerStem, out var mapped) ? mapped : TypeName(headerStem);

    /// <summary>
    /// 方法名：去掉家族段后 Pascal 化。
    /// </summary>
    public static string MethodName(string functionName)
    {
        var name = functionName.StartsWith("obsharp_", StringComparison.Ordinal)
            ? functionName["obsharp_".Length..]
            : functionName;

        foreach (var family in Families)
        {
            if (name.StartsWith(family + "_", StringComparison.Ordinal))
            {
                name = name[(family.Length + 1)..];
                break;
            }
        }

        return Identifier(name.Length == 0 ? "invoke" : name);
    }

    /// <summary>
    /// 函数是否是某句柄的释放函数（名字以 destroy 系后缀结尾）。
    /// </summary>
    public static bool IsDestroyFunction(string functionName, string handleName)
    {
        if (handleName.Length == 0)
            return false;

        var lower = functionName.ToLowerInvariant();
        foreach (var suffix in DestroySuffixes)
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 返回句柄是否归调用方所有（create 标记 / override 表，且存在释放函数）。
    /// </summary>
    public static bool ReturnsOwnedHandle(FunctionDecl function, Model model)
    {
        if (function.Sig.Return.Kind != TypeKind.Handle)
            return false;

        var name = function.Name;
        var marked = OwnedReturnOverrides.Contains(name);
        if (!marked)
        {
            foreach (var marker in OwnedReturnMarkers)
            {
                if (name.Contains(marker, StringComparison.Ordinal))
                {
                    marked = true;
                    break;
                }
            }
        }

        return marked && FindDestroy(function.Sig.Return.DeclName!, model) is not null;
    }

    /// <summary>
    /// 找句柄（含沿继承链向上）的释放函数名。
    /// </summary>
    public static string? FindDestroy(string handleName, Model model)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var name = handleName; name is not null && seen.Add(name);)
        {
            if (!model.Handles.TryGetValue(name, out var handle))
                break;

            if (handle.DestroyFunction is not null)
                return handle.DestroyFunction;

            name = handle.BaseName;
        }

        return null;
    }
}
