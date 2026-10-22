namespace OBSharp.Interop.Generator;

/// <summary>
/// 解析结果：一份头文件导出面的完整模型，供发射器消费。
/// </summary>
internal sealed class Model
{
    public List<FunctionDecl> Functions { get; } = [];
    public Dictionary<string, HandleDecl> Handles { get; } = [with(StringComparer.Ordinal)];
    public Dictionary<string, StructDecl> Structs { get; } = [with(StringComparer.Ordinal)];
    public Dictionary<string, DelegateDecl> Delegates { get; } = [with(StringComparer.Ordinal)];
    public Dictionary<string, EnumDecl> Enums { get; } = [with(StringComparer.Ordinal)];
}

/// <summary>
/// 句柄类型（C++ 类/结构体的指针出现在签名里、无字段布局要求）。
/// </summary>
internal sealed class HandleDecl
{
    public required string Name;            // C# 类名（Pascal），如 QWidget / ObsSource
    public required string NativeName;      // C++ 名，如 QWidget / obs_source（家族段来源）
    public string? BaseName;                // C# 基类名（同为句柄时才生成继承）
    public string? DestroyFunction;         // 释放函数（obsharp_widget_destroy），无则为借用句柄
}

/// <summary>
/// 有字段布局的 struct（obsharp_request_header 等）。
/// </summary>
internal sealed class StructDecl
{
    public required string Name;            // C++ 名（最后一段）
    public List<StructField> Fields { get; } = [];
}

internal sealed class StructField
{
    public required string Name;            // C++ 字段名（原样保留做布局对照）
    public required FieldKind Kind;
    public string? TypeName;                // Scalar 的 C# 类型 / Struct、Enum 的 C++ 名
    public int Count;                       // Kind == FixedArray 的元素数
    public FunctionSig? Signature;          // Kind == FnPtr 时的签名
}

internal enum FieldKind
{
    Scalar,     // 标量（含 size_t 等 typedef）
    Pointer,    // const char* → byte*
    FnPtr,      // 函数指针 → delegate* unmanaged[Cdecl]<...>
    Struct,     // 按值嵌套 struct
    Enum,       // 枚举字段
    Opaque,     // void* / 其他指针 → nint
    FixedArray, // 标量定长数组 → fixed 缓冲
}

/// <summary>
/// 函数指针 typedef → C# 委托。
/// </summary>
internal sealed class DelegateDecl
{
    public required string Name;            // typedef 名，如 obsharp_cef_widget_value_callback
    public required FunctionSig Signature;
}

/// <summary>
/// 函数签名（导出函数和函数指针共用）。
/// </summary>
internal sealed class FunctionSig
{
    public TypeRef Return { get; set; } = new() { Kind = TypeKind.Void };
    public List<ParamDecl> Parameters { get; } = [];
}

/// <summary>
/// 导出函数。
/// </summary>
internal sealed class FunctionDecl
{
    public required string Name;            // C 符号名，如 obsharp_widget_create
    public required string Header;          // 所在头文件（stem），如 widget
    public FunctionSig Sig { get; } = new();
}

internal sealed class ParamDecl
{
    public required string Name;
    public required TypeRef Type;
}

/// <summary>
/// C++ 枚举 → C# enum（按 C++ 作用域嵌套）。
/// </summary>
internal sealed class EnumDecl
{
    public required string Name;            // 枚举名，如 WindowType / obsharp_log_level
    public List<string> Scope { get; } = []; // 作用域链，如 ["Qt"]、["QFileDialog"]
    public string Underlying { get; set; } = "int";
    public List<(string Name, long Value)> Values { get; } = [];

    /// <summary>
    /// C# 引用名（含作用域），如 Qt.WindowType。
    /// </summary>
    public string CsRef => Scope.Count == 0 ? CsName : string.Join('.', Scope) + "." + CsName;

    public string CsName => Naming.TypeName(Name);
}

/// <summary>
/// 类型引用：分类 + C# 原始/公开表示。
/// </summary>
internal sealed class TypeRef
{
    public TypeKind Kind;
    public string Spelling = "";            // C 类型拼写（诊断用）
    public string? DeclName;                // 关联的 Handle/Struct/Enum/Delegate 名
    public bool IsConst;                    // const char* 的 const
    public TypeRef? Pointee;                // 指针的被指类型
}

internal enum TypeKind
{
    Void,
    Scalar,         // int/long long/size_t/double/... → 机械映射
    Bool,
    String,         // const char*
    VoidPtr,        // void*
    Handle,         // QWidget*
    StructPtr,      // obsharp_request_header*
    Enum,
    Delegate,       // 函数指针 typedef
    DelegatePtr,    // 函数指针 typedef*（log_handler_t*）→ ref 委托
    HandleArray,    // QWidget**（配 size_t* 出参）
    StringArray,    // const char**（配 size_t* 出参）
    RefPtr,         // 未配数组长度的二级指针 → ref nint
    OutScalar,      // int* 单独出现 → out int
    RecordPtr,      // record* 待定（收齐后按 布局结构体/句柄/不透明 定性）
    RecordArrayPtr, // record** 待定
}
