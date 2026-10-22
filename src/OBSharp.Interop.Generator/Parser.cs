using System.Runtime.InteropServices;

using ClangSharp.Interop;

namespace OBSharp.Interop.Generator;

/// <summary>
/// 解析层：ClangSharp.Interop（libclang）直接驱动，不走 PInvokeGenerator。
/// 只收集声明位置落在 --input 头文件里的符号；Qt/CEF/obs 的声明参与解析但不产出。
/// </summary>
internal sealed unsafe class Parser
{
    private readonly GenerateCommand _options;
    private readonly Model _model = new();
    private readonly HashSet<string> _inputFiles;   // 规范化后的输入头路径
    private readonly List<string> _diagnostics = [];
    private readonly HashSet<string> _opaqueStructs = [with(StringComparer.Ordinal)]; // 退化为 nint 的结构体
    private readonly HashSet<string> _skipped = [with(StringComparer.Ordinal)];        // 封送不了被跳过的函数
    private readonly Dictionary<string, string> _recordBases = [with(StringComparer.Ordinal)]; // record 名 → 基类 record 名（收集期快照）

    public Parser(GenerateCommand options)
    {
        _options = options;
        _inputFiles = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var pattern in options.Input)
        {
            foreach (var input in ExpandInput(pattern))
                _inputFiles.Add(Path.GetFullPath(input));
        }
    }

    /// <summary>
    /// --input 支持通配符（libobs 的 *.h 一把抓）。
    /// </summary>
    public static IEnumerable<string> ExpandInput(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            yield return pattern;
            yield break;
        }

        var dir = Path.GetDirectoryName(pattern);
        var glob = Path.GetFileName(pattern);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        foreach (var file in Directory.EnumerateFiles(dir, glob))
            yield return file;
    }

    /// <summary>
    /// obsconfig.h 最小替身（按 obs-studio 的 Windows 默认值），只为解析闭包通过，值不会被用到。
    /// </summary>
    private const string ObsConfigStub =
        """
        #pragma once
        // 由 OBSharp.Interop.Generator 写的解析用替身（对应 obs-studio 构建期生成的 obsconfig.h）。
        #define OBS_VERSION "31.0.0"
        #define OBS_DATA_PATH "../../data/obs"
        #define OBS_INSTALL_PREFIX ""
        #define OBS_PLUGIN_DESTINATION "obs-plugins/64bit"
        #define OBS_RELATIVE_PREFIX ""
        """;

    // libclang 的 clang_visitChildren 只吃非托管函数指针：用静态委托换指针。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate CXChildVisitResult ChildVisitor(CXCursor cursor, CXCursor parent, void* clientData);

    private static readonly ChildVisitor CollectDelegate = CollectChild;
    private static readonly delegate* unmanaged[Cdecl]<CXCursor, CXCursor, void*, CXChildVisitResult> CollectPtr =
        (delegate* unmanaged[Cdecl]<CXCursor, CXCursor, void*, CXChildVisitResult>)
            Marshal.GetFunctionPointerForDelegate(CollectDelegate);

    [ThreadStatic]
    private static List<CXCursor>? s_children;

    private static CXChildVisitResult CollectChild(CXCursor cursor, CXCursor parent, void* clientData)
    {
        s_children!.Add(cursor);
        return CXChildVisitResult.CXChildVisit_Continue;
    }

    private static List<CXCursor> ChildCursors(CXCursor cursor)
    {
        var list = new List<CXCursor>();
        s_children = list;
        try
        {
            clang.visitChildren(cursor, CollectPtr, null);
        }
        finally
        {
            s_children = null;
            GC.KeepAlive(CollectDelegate);
        }

        return list;
    }

    public Model Parse()
    {
        var args = new List<string> { "-x", "c++", "-std=" + _options.Std, "-fms-extensions", "-w" };
        foreach (var define in _options.Define)
            args.Add("-D" + define);
        foreach (var include in _options.Include)
            args.Add("-I" + Path.GetFullPath(include));
        foreach (var include in _options.MsvcInclude)
        {
            // -imsvc 是 clang driver 选项，libclang 走 cc1 只认 -isystem（会排在默认 MSVC STL 之前）
            args.Add("-isystem");
            args.Add(Path.GetFullPath(include));
        }

        // libobs 的 obs-config.h 要求构建期生成的 obsconfig.h（CMake 生成物，不在仓库里）。
        // 和旧生成器一样：写一个最小替身到 staging，只为解析通过，值不会被用到。
        var staging = Path.Combine(Path.GetTempPath(), "obsharp-interop-generator");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "obsconfig.h"), ObsConfigStub);
        args.Insert(0, "-I" + staging);
        foreach (var extra in _options.ExtraArg)
            args.Add(extra);

        var index = (CXIndex)clang.createIndex(0, 0);
        try
        {
            foreach (var input in _options.Input)
                ParseOne(index, Path.GetFullPath(input), [.. args]);
        }
        finally
        {
            clang.disposeIndex(index);
        }

        PostProcess();

        foreach (var line in _diagnostics)
            Console.Error.WriteLine(line);

        return _model;
    }

    private void ParseOne(CXIndex index, string path, string[] args)
    {
        CXTranslationUnit tu;
        try
        {
            tu = CXTranslationUnit.Parse(index, path, args, default,
                CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"libclang 解析失败：{path}", ex);
        }

        if (tu.Handle == IntPtr.Zero)
            throw new InvalidOperationException($"libclang 返回了空 TU：{path}\n参数：{string.Join(' ', args)}");

        try
        {
            for (uint i = 0; i < tu.NumDiagnostics; i++)
            {
                var diag = clang.getDiagnostic(tu, i);
                if (clang.getDiagnosticSeverity(diag) >= CXDiagnosticSeverity.CXDiagnostic_Error)
                    _diagnostics.Add($"warning: {path}: {clang.formatDiagnostic(diag, clang.defaultDiagnosticDisplayOptions())}");
                clang.disposeDiagnostic(diag);
            }

            Walk(clang.getTranslationUnitCursor(tu));
        }
        finally
        {
            clang.disposeTranslationUnit(tu);
        }
    }

    private void Walk(CXCursor cursor)
    {
        foreach (var child in ChildCursors(cursor))
        {
            switch (clang.getCursorKind(child))
            {
                case CXCursorKind.CXCursor_Namespace:
                case CXCursorKind.CXCursor_LinkageSpec:
                case CXCursorKind.CXCursor_UnexposedDecl:
                    Walk(child);
                    break;

                case CXCursorKind.CXCursor_FunctionDecl:
                    TryCollectFunction(child);
                    break;

                case CXCursorKind.CXCursor_StructDecl:
                case CXCursorKind.CXCursor_ClassDecl:
                    TryCollectRecord(child);
                    break;

                case CXCursorKind.CXCursor_TypedefDecl:
                    TryCollectDelegate(child);
                    break;
            }
        }
    }

    // ---- 收集 ----

    private void TryCollectFunction(CXCursor cursor)
    {
        if (!IsInInputFile(cursor))
            return;

        var name = Spelling(cursor);
        if (name.Length == 0 || !MatchesTraverse(name))
            return;

        // 只收外链接面（static / 内部 helper 不是导出面）
        if (clang.getCursorLinkage(cursor) == CXLinkageKind.CXLinkage_Internal)
            return;

        // 多个 --input 头共用 include 时，同一符号会在多个 TU 里出现：按名字去重
        if (_model.Functions.Any(f => f.Name == name) || _skipped.Contains(name))
            return;

        var type = clang.getCursorType(cursor);
        if (clang.isFunctionTypeVariadic(type) != 0)
            return; // varargs 过不了 C ABI 封送（logger 的 Log 就是这种）

        try
        {
            var function = new FunctionDecl { Name = name, Header = HeaderStem(cursor) };
            function.Sig.Return = ResolveType(clang.getCursorResultType(cursor));

            int argc = clang.Cursor_getNumArguments(cursor);
            for (int i = 0; i < argc; i++)
            {
                var arg = clang.Cursor_getArgument(cursor, (uint)i);
                var argName = Spelling(arg);
                function.Sig.Parameters.Add(new ParamDecl
                {
                    Name = argName.Length == 0 ? $"arg{i}" : argName,
                    Type = ResolveType(clang.getCursorType(arg)),
                });
            }

            _model.Functions.Add(function);
        }
        catch (NotSupportedException ex)
        {
            // 封送不了的签名（匿名函数指针 / va_list 等）：跳过这个函数，别拖垮整个运行
            _skipped.Add(name);
            _diagnostics.Add($"warning: 跳过 {name}：{ex.Message}");
        }
    }

    private void TryCollectRecord(CXCursor cursor)
    {
        if (!IsInInputFile(cursor))
            return;

        var name = Spelling(cursor);
        if (name.Length == 0)
            return;

        // .NET 等价类型（vec2 → System.Numerics.Vector2 等）不产布局
        if (Naming.TypeAlias(name) is not null)
            return;

        var fields = new List<CXCursor>();
        foreach (var child in ChildCursors(cursor))
        {
            if (clang.getCursorKind(child) == CXCursorKind.CXCursor_FieldDecl)
                fields.Add(child);
        }

        if (fields.Count == 0)
            return; // 前向声明类（ResourceHandler）→ 等签名引用到按句柄登记

        var decl = new StructDecl { Name = name };
        try
        {
            foreach (var field in fields)
                decl.Fields.Add(ResolveField(field));
        }
        catch (NotSupportedException ex)
        {
            // 含不支持字段（复杂数组/匿名嵌套）的结构体：不产布局，引用它的指针参数退化为 nint
            _opaqueStructs.Add(name);
            _diagnostics.Add($"warning: {name}: {ex.Message}；引用它的参数退化为 nint");
            return;
        }

        _model.Structs[name] = decl;
    }

    private StructField ResolveField(CXCursor field)
    {
        var name = Spelling(field);
        var type = clang.getCursorType(field);
        var canonical = clang.getCanonicalType(type);
        return ResolveFieldCore(field, name, type, canonical);
    }

    private StructField ResolveFieldCore(CXCursor field, string name, CXType type, CXType canonical)
    {
        if (canonical.kind == CXTypeKind.CXType_Pointer)
        {
            var pointee = clang.getCanonicalType(clang.getPointeeType(canonical));
            if (pointee.kind == CXTypeKind.CXType_FunctionProto)
            {
                var typedefName = TypedefName(type);
                return new StructField
                {
                    Name = name,
                    Kind = FieldKind.FnPtr,
                    TypeName = typedefName,
                    Signature = SigFromFunctionProto(pointee, ParamNamesOfTypedef(field), field),
                };
            }

            if (IsCharType(pointee.kind))
                return new StructField { Name = name, Kind = FieldKind.Pointer };

            return new StructField { Name = name, Kind = FieldKind.Opaque };
        }

        if (canonical.kind == CXTypeKind.CXType_Enum)
            return new StructField { Name = name, Kind = FieldKind.Enum, TypeName = RegisterEnum(canonical) };

        if (canonical.kind == CXTypeKind.CXType_Record)
        {
            // .NET 等价类型（vec2 → Vector2 等）：字段直接用平台类型
            if (Naming.TypeAlias(RecordName(canonical)) is { } alias)
                return new StructField { Name = name, Kind = FieldKind.Scalar, TypeName = alias };

            return new StructField { Name = name, Kind = FieldKind.Struct, TypeName = RegisterStruct(canonical) };
        }

        if (canonical.kind is CXTypeKind.CXType_ConstantArray or CXTypeKind.CXType_IncompleteArray)
        {
            // 标量定长数组 → fixed 缓冲；指针数组/匿名嵌套等不支持 → 抛给上层降级
            var element = clang.getCanonicalType(clang.getArrayElementType(canonical));
            if (element.kind is not (CXTypeKind.CXType_Pointer or CXTypeKind.CXType_Record
                or CXTypeKind.CXType_ConstantArray or CXTypeKind.CXType_FunctionProto)
                && clang.getArraySize(canonical) is > 0 and <= 256)
            {
                return new StructField
                {
                    Name = name,
                    Kind = FieldKind.FixedArray,
                    TypeName = MapScalar(element.kind, clang.getTypeSpelling(element).ToString()),
                    Count = checked((int)clang.getArraySize(canonical)),
                };
            }

            throw new NotSupportedException($"结构体字段是复杂数组，暂不支持：{name}（{clang.getTypeSpelling(type)}）");
        }

        var typedefName2 = TypedefName(type);
        if (typedefName2 is "size_t" or "uintptr_t")
            return new StructField { Name = name, Kind = FieldKind.Scalar, TypeName = "nuint" };
        if (typedefName2 is "intptr_t" or "ptrdiff_t")
            return new StructField { Name = name, Kind = FieldKind.Scalar, TypeName = "nint" };

        return new StructField
        {
            Name = name,
            Kind = FieldKind.Scalar,
            TypeName = MapScalar(canonical.kind, clang.getTypeSpelling(canonical).ToString()),
        };
    }

    private void TryCollectDelegate(CXCursor cursor)
    {
        if (!IsInInputFile(cursor))
            return;

        var name = Spelling(cursor);
        if (name.Length == 0 || _model.Delegates.ContainsKey(name))
            return;

        var underlying = clang.getCanonicalType(clang.getTypedefDeclUnderlyingType(cursor));
        if (underlying.kind != CXTypeKind.CXType_Pointer)
            return;

        var proto = clang.getCanonicalType(clang.getPointeeType(underlying));
        if (proto.kind != CXTypeKind.CXType_FunctionProto)
            return;

        try
        {
            _model.Delegates[name] = new DelegateDecl
            {
                Name = name,
                Signature = SigFromFunctionProto(proto, ParamNamesOfTypedef(cursor), cursor),
            };
        }
        catch (NotSupportedException ex)
        {
            _diagnostics.Add($"warning: 跳过委托 {name}：{ex.Message}");
        }
    }

    /// <summary>
    /// typedef 游标的子节点里有 ParmDecl（函数指针 typedef 的参数名）。
    /// </summary>
    private static List<string> ParamNamesOfTypedef(CXCursor typedefCursor)
    {
        var names = new List<string>();
        foreach (var child in ChildCursors(typedefCursor))
        {
            if (clang.getCursorKind(child) == CXCursorKind.CXCursor_ParmDecl)
                names.Add(Spelling(child));
        }

        return names;
    }

    private FunctionSig SigFromFunctionProto(CXType proto, List<string> paramNames)
        => SigFromFunctionProto(proto, paramNames, null);

    /// <summary>
    /// 从函数原型组签名。owner 是 typedef/字段游标时优先用它的 ParmDecl 游标取参数类型
    /// ——clang.getArgType 会剥掉 typedef 糖，嵌套的函数指针 typedef（obs_task_t）就丢了。
    /// </summary>
    private FunctionSig SigFromFunctionProto(CXType proto, List<string> paramNames, CXCursor? owner)
    {
        var sig = new FunctionSig { Return = ResolveType(clang.getResultType(proto)) };
        int argc = clang.getNumArgTypes(proto);

        var parmCursors = new List<CXCursor>();
        if (owner is not null)
        {
            foreach (var child in ChildCursors(owner.Value))
            {
                if (clang.getCursorKind(child) == CXCursorKind.CXCursor_ParmDecl)
                    parmCursors.Add(child);
            }
        }

        for (int i = 0; i < argc; i++)
        {
            var argType = parmCursors.Count == argc
                ? clang.getCursorType(parmCursors[i])
                : clang.getArgType(proto, (uint)i);
            sig.Parameters.Add(new ParamDecl
            {
                Name = i < paramNames.Count && paramNames[i].Length > 0 ? paramNames[i] : $"arg{i}",
                Type = ResolveType(argType),
            });
        }

        return sig;
    }

    // ---- 类型 ----

    private TypeRef ResolveType(CXType type)
    {
        var result = new TypeRef { Spelling = clang.getTypeSpelling(type).ToString() };

        // typedef 名字优先（size_t / uintptr_t / 函数指针 typedef）
        var typedefName = TypedefName(type);
        if (typedefName is not null)
        {
            if (typedefName is "va_list" or "__builtin_va_list" or "__gnuc_va_list")
                throw new NotSupportedException($"va_list 参数封送不了：{result.Spelling}");

            if (typedefName is "size_t" or "uintptr_t")
            {
                result.Kind = TypeKind.Scalar;
                result.Spelling = "nuint";
                return result;
            }

            if (typedefName is "intptr_t" or "ptrdiff_t")
            {
                result.Kind = TypeKind.Scalar;
                result.Spelling = "nint";
                return result;
            }

            if (IsFunctionPointer(clang.getCanonicalType(type)))
            {
                result.Kind = TypeKind.Delegate;
                result.DeclName = Naming.TypeName(typedefName);
                return result;
            }
        }

        switch (type.kind)
        {
            case CXTypeKind.CXType_Void:
                result.Kind = TypeKind.Void;
                return result;

            case CXTypeKind.CXType_Bool:
                result.Kind = TypeKind.Bool;
                return result;

            case CXTypeKind.CXType_Pointer:
                return ResolvePointer(type, result);
        }

        var canonical = clang.getCanonicalType(type);
        if (canonical.kind == CXTypeKind.CXType_Enum)
        {
            result.Kind = TypeKind.Enum;
            result.DeclName = RegisterEnum(canonical);
            return result;
        }

        result.Kind = TypeKind.Scalar;
        result.Spelling = MapScalar(canonical.kind, result.Spelling);
        return result;
    }

    private TypeRef ResolvePointer(CXType pointer, TypeRef result)
    {
        var pointee = clang.getPointeeType(pointer);
        var pointeeCanonical = clang.getCanonicalType(pointee);
        result.Pointee = new TypeRef
        {
            Spelling = clang.getTypeSpelling(pointee).ToString(),
            IsConst = clang.isConstQualifiedType(pointee) != 0,
        };

        switch (pointeeCanonical.kind)
        {
            case CXTypeKind.CXType_Void:
                result.Kind = TypeKind.VoidPtr;
                return result;

            case CXTypeKind.CXType_FunctionProto:
                var typedefName = TypedefName(pointer)
                    ?? throw new NotSupportedException($"匿名函数指针参数暂不支持：{result.Spelling}");
                result.Kind = TypeKind.Delegate;
                result.DeclName = Naming.TypeName(typedefName);
                return result;

            case CXTypeKind.CXType_Pointer:
                // T** / const char**：配 size_t 的借用数组；配不上时按 ref nint / ref 委托处理
                var inner = clang.getCanonicalType(clang.getPointeeType(pointeeCanonical));
                if (IsCharType(inner.kind))
                {
                    result.Kind = TypeKind.StringArray;
                    return result;
                }

                if (inner.kind == CXTypeKind.CXType_Record)
                {
                    // 结构体指针数组没有轻量表示 → ref nint；只有真句柄才组数组（PostProcess 定性）
                    var innerName = RecordName(inner);
                    PeekBase(innerName, inner);
                    result.Kind = TypeKind.RecordArrayPtr;
                    result.DeclName = innerName;
                    return result;
                }

                if (inner.kind == CXTypeKind.CXType_FunctionProto)
                {
                    // 函数指针 typedef 再取指针（log_handler_t*）
                    var fnptrName = TypedefName(pointee)
                        ?? throw new NotSupportedException($"匿名函数指针二级指针暂不支持：{result.Spelling}");
                    result.Kind = TypeKind.DelegatePtr;
                    result.DeclName = Naming.TypeName(fnptrName);
                    return result;
                }

                result.Kind = TypeKind.RefPtr;
                return result;

            case CXTypeKind.CXType_Record:
                // record* 先记待定：收齐所有 TU 后在 PostProcess 统一定性
                // （首见处可能只看得到前向声明，定义在别的头里）
                var recordName = RecordName(pointeeCanonical);
                PeekBase(recordName, pointeeCanonical);
                result.Kind = TypeKind.RecordPtr;
                result.DeclName = recordName;
                return result;

            case CXTypeKind.CXType_Enum:
                result.Kind = TypeKind.OutScalar;
                result.DeclName = RegisterEnum(pointeeCanonical);
                return result;
        }

        if (IsCharType(pointeeCanonical.kind))
        {
            if (clang.isConstQualifiedType(pointee) != 0)
            {
                result.Kind = TypeKind.String;
            }
            else
            {
                result.Kind = TypeKind.OutScalar;
                result.Spelling = "byte";
            }

            return result;
        }

        // 标量指针：先看被指类型的 typedef 名（size_t* 是数组长度出参，必须认出来）
        var pointeeTypedef = TypedefName(pointee);
        if (pointeeTypedef is "size_t" or "uintptr_t")
        {
            result.Kind = TypeKind.OutScalar;
            result.Spelling = "nuint";
            return result;
        }

        if (pointeeTypedef is "intptr_t" or "ptrdiff_t")
        {
            result.Kind = TypeKind.OutScalar;
            result.Spelling = "nint";
            return result;
        }

        // 其他标量指针：出参
        result.Kind = TypeKind.OutScalar;
        result.Spelling = MapScalar(pointeeCanonical.kind, result.Pointee.Spelling);
        return result;
    }

    /// <summary>
    /// 收集期快照基类（TU 还活着时读 AST）。定性阶段只用字符串：
    /// CXType/CXCursor 指向 TU 内部，TU 销毁后是悬垂指针，不能再碰。
    /// </summary>
    private void PeekBase(string nativeName, CXType recordType)
    {
        if (_recordBases.ContainsKey(nativeName))
            return;

        string? baseName = null;
        var definition = clang.getCursorDefinition(clang.getTypeDeclaration(recordType));
        if (clang.getCursorKind(definition) is CXCursorKind.CXCursor_StructDecl
            or CXCursorKind.CXCursor_ClassDecl)
        {
            foreach (var child in ChildCursors(definition))
            {
                if (clang.getCursorKind(child) != CXCursorKind.CXCursor_CXXBaseSpecifier)
                    continue;

                var baseType = clang.getCanonicalType(clang.getCursorType(child));
                if (baseType.kind == CXTypeKind.CXType_Record)
                {
                    baseName = RecordName(baseType);
                    PeekBase(baseName, baseType); // 继承链（QCefWidget : QWidget : QObject）整条快照
                    break;
                }
            }
        }

        _recordBases[nativeName] = baseName ?? "";
    }

    /// <summary>
    /// 按名字登记句柄（PostProcess 定性阶段用；只依赖字符串快照）。
    /// </summary>
    private string RegisterHandleByName(string nativeName)
    {
        var name = Naming.TypeName(nativeName);
        if (_model.Handles.ContainsKey(name))
            return name;

        var baseNative = _recordBases.GetValueOrDefault(nativeName);
        string? baseName;
        if (!string.IsNullOrEmpty(baseNative))
            baseName = RegisterHandleByName(baseNative); // 基类也登记成句柄
        else
            baseName = Naming.FallbackBases.GetValueOrDefault(name);

        _model.Handles[name] = new HandleDecl { Name = name, NativeName = nativeName, BaseName = baseName };
        return name;
    }

    private static IEnumerable<TypeRef> AllTypeRefs(FunctionSig sig)
    {
        yield return sig.Return;
        foreach (var parameter in sig.Parameters)
            yield return parameter.Type;
    }

    /// <summary>
    /// 按值嵌套的 struct：递归登记字段布局。
    /// </summary>
    private string RegisterStruct(CXType recordType)
    {
        var name = RecordName(recordType);
        if (name.Length == 0)
            throw new NotSupportedException($"匿名结构体字段暂不支持：{clang.getTypeSpelling(recordType)}");

        if (_opaqueStructs.Contains(name))
            throw new NotSupportedException($"结构体 {name} 已退化为不透明指针，不能再按值嵌套");
        if (_model.Structs.ContainsKey(name))
            return name;

        var decl = new StructDecl { Name = name };
        _model.Structs[name] = decl; // 先放表，防递归类型死循环

        var definition = clang.getCursorDefinition(clang.getTypeDeclaration(recordType));
        try
        {
            if (clang.getCursorKind(definition) == CXCursorKind.CXCursor_UnionDecl)
                throw new NotSupportedException($"union 字段暂不支持：{name}");

            if (clang.getCursorKind(definition) is CXCursorKind.CXCursor_StructDecl
                or CXCursorKind.CXCursor_ClassDecl)
            {
                foreach (var child in ChildCursors(definition))
                {
                    if (clang.getCursorKind(child) == CXCursorKind.CXCursor_FieldDecl)
                        decl.Fields.Add(ResolveField(child));
                }
            }
        }
        catch (NotSupportedException)
        {
            _model.Structs.Remove(name);
            _opaqueStructs.Add(name);
            throw;
        }

        return name;
    }

    private string RegisterEnum(CXType enumType)
    {
        var decl = clang.getTypeDeclaration(enumType);
        var name = Spelling(decl);
        if (name.Length == 0)
        {
            var typedef = TypedefName(enumType);
            name = typedef ?? throw new NotSupportedException($"匿名枚举无 typedef 名：{clang.getTypeSpelling(enumType)}");
        }

        if (_model.Enums.ContainsKey(name))
            return name;

        var info = new EnumDecl { Name = name };

        for (var parent = clang.getCursorSemanticParent(decl);
             clang.getCursorKind(parent) is CXCursorKind.CXCursor_Namespace
                 or CXCursorKind.CXCursor_ClassDecl or CXCursorKind.CXCursor_StructDecl
                 or CXCursorKind.CXCursor_EnumDecl;
             parent = clang.getCursorSemanticParent(parent))
        {
            info.Scope.Insert(0, Spelling(parent));
        }

        var underlying = clang.getEnumDeclIntegerType(decl);
        info.Underlying = underlying.kind switch
        {
            CXTypeKind.CXType_UInt or CXTypeKind.CXType_ULong or CXTypeKind.CXType_ULongLong => "uint",
            CXTypeKind.CXType_LongLong => "long",
            _ => "int",
        };

        foreach (var child in ChildCursors(decl))
        {
            if (clang.getCursorKind(child) == CXCursorKind.CXCursor_EnumConstantDecl)
                info.Values.Add((Spelling(child), clang.getEnumConstantDeclValue(child)));
        }

        _model.Enums[name] = info;
        return name;
    }

    // ---- 收尾：数组配对、销毁函数 ----

    private void PostProcess()
    {
        // record* / record** 定性：--input 里有字段布局的 → 布局结构体；退化的 → 不透明指针；
        // 其余（前向声明的不透明类）→ 句柄。必须收齐所有 TU 后做，顺序无关。
        var allSigs = _model.Functions.Select(f => f.Sig)
            .Concat(_model.Delegates.Values.Select(d => d.Signature))
            .Concat(_model.Structs.Values.SelectMany(s => s.Fields)
                .Where(f => f.Signature is not null).Select(f => f.Signature!))
            .ToList();
        foreach (var sig in allSigs)
        {
            foreach (var typeRef in AllTypeRefs(sig))
            {
                switch (typeRef.Kind)
                {
                    case TypeKind.RecordPtr:
                        if (Naming.TypeAlias(typeRef.DeclName!) is not null)
                            typeRef.Kind = TypeKind.StructPtr; // .NET 等价类型按结构体指针封送
                        else if (_model.Structs.ContainsKey(typeRef.DeclName!))
                            typeRef.Kind = TypeKind.StructPtr;
                        else if (_opaqueStructs.Contains(typeRef.DeclName!))
                            typeRef.Kind = TypeKind.VoidPtr;
                        else
                        {
                            typeRef.Kind = TypeKind.Handle;
                            typeRef.DeclName = RegisterHandleByName(typeRef.DeclName!);
                        }

                        break;

                    case TypeKind.RecordArrayPtr:
                        if (Naming.TypeAlias(typeRef.DeclName!) is not null
                            || _model.Structs.ContainsKey(typeRef.DeclName!)
                            || _opaqueStructs.Contains(typeRef.DeclName!))
                            typeRef.Kind = TypeKind.RefPtr;
                        else
                        {
                            typeRef.Kind = TypeKind.HandleArray;
                            typeRef.DeclName = RegisterHandleByName(typeRef.DeclName!);
                        }

                        break;
                }
            }
        }

        // 引用了被跳过委托的函数 → 整个跳过（否则产物引用不存在的委托类型）
        var delegateNames = _model.Delegates.Values
            .Select(d => Naming.TypeName(d.Name))
            .ToHashSet(StringComparer.Ordinal);
        _model.Functions.RemoveAll(f =>
        {
            var missing = (f.Sig.Return.Kind is TypeKind.Delegate or TypeKind.DelegatePtr
                           && !delegateNames.Contains(f.Sig.Return.DeclName!))
                || f.Sig.Parameters.Any(p => p.Type.Kind is TypeKind.Delegate or TypeKind.DelegatePtr
                    && !delegateNames.Contains(p.Type.DeclName!));
            if (missing)
                _diagnostics.Add($"warning: 跳过 {f.Name}：引用了未生成的委托");
            return missing;
        });

        foreach (var function in _model.Functions)
        {
            var parameters = function.Sig.Parameters;

            // 返回 T** / const char** 配末尾 size_t* 出参 → 借用数组；配不上就当裸指针返回
            if (function.Sig.Return.Kind is TypeKind.StringArray or TypeKind.HandleArray)
            {
                var paired = false;
                for (int i = parameters.Count - 1; i >= 0; i--)
                {
                    if (parameters[i].Type.Kind == TypeKind.OutScalar && parameters[i].Type.Spelling == "nuint")
                    {
                        parameters[i].Name = "__size"; // 发射时识别为数组长度出参
                        paired = true;
                        break;
                    }
                }

                if (!paired)
                    function.Sig.Return.Kind = TypeKind.VoidPtr;
            }

            // 参数 const char** 配紧随其后的 size_t（按值）→ 入参数组。
            // 注意：T** + size_t 不配对 —— 那是 in/out 单槽（pthread_attr_setstacksize 之类），
            // 不是数组。T** 数组只在返回值 + size_t* 出参的形状里出现。
            for (int i = 0; i < parameters.Count - 1; i++)
            {
                if (parameters[i].Type.Kind != TypeKind.StringArray)
                    continue;
                if (parameters[i + 1].Type.Kind == TypeKind.Scalar && parameters[i + 1].Type.Spelling == "nuint")
                    parameters[i + 1].Name = "__len";
            }

            // 没配上长度参数的二级指针：入参 ref nint / ref 委托
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].Type.Kind is not (TypeKind.StringArray or TypeKind.HandleArray))
                    continue;
                var paired = i + 1 < parameters.Count && parameters[i + 1].Name == "__len";
                if (!paired)
                    parameters[i].Type.Kind = TypeKind.RefPtr;
            }

            // 参数结构体指针 配紧随其后的 int xxx_count → 结构体数组
            for (int i = 0; i < parameters.Count - 1; i++)
            {
                if (parameters[i].Type.Kind != TypeKind.StructPtr)
                    continue;
                var next = parameters[i + 1];
                if (next.Type.Kind == TypeKind.Scalar && next.Type.Spelling == "int"
                    && next.Name.EndsWith("count", StringComparison.Ordinal))
                    next.Name = "__count";
            }
        }

        // 销毁函数：*_destroy 系且首参数是句柄
        foreach (var function in _model.Functions)
        {
            if (function.Sig.Parameters.Count == 0
                || function.Sig.Parameters[0].Type.Kind != TypeKind.Handle)
                continue;
            if (!Naming.IsDestroyFunction(function.Name, function.Sig.Parameters[0].Type.DeclName!))
                continue;

            var handleName = function.Sig.Parameters[0].Type.DeclName!;
            if (_model.Handles.TryGetValue(handleName, out var handle))
                handle.DestroyFunction = function.Name;
        }
    }

    // ---- 工具 ----

    private bool MatchesTraverse(string name)
    {
        if (_options.Traverse.Length == 0)
            return true;

        foreach (var pattern in _options.Traverse)
        {
            var prefix = pattern.EndsWith('*') ? pattern[..^1] : pattern;
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool IsInInputFile(CXCursor cursor)
    {
        var path = FileNameOf(clang.getCursorLocation(cursor));
        return path.Length > 0 && _inputFiles.Contains(Path.GetFullPath(path));
    }

    private static string HeaderStem(CXCursor cursor)
    {
        var path = FileNameOf(clang.getCursorLocation(cursor));
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string FileNameOf(CXSourceLocation location)
    {
        void* file;
        uint line, column, offset;
        clang.getFileLocation(location, &file, &line, &column, &offset);
        return file == null ? "" : clang.getFileName(file).ToString();
    }

    private static bool IsCharType(CXTypeKind kind)
        => kind is CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U
            or CXTypeKind.CXType_SChar or CXTypeKind.CXType_UChar;

    private static bool IsFunctionPointer(CXType canonical)
        => canonical.kind == CXTypeKind.CXType_Pointer
            && clang.getCanonicalType(clang.getPointeeType(canonical)).kind == CXTypeKind.CXType_FunctionProto;

    private static string TypedefName(CXType type)
    {
        if (type.kind == CXTypeKind.CXType_Typedef)
            return Spelling(clang.getTypeDeclaration(type));

        if (type.kind == CXTypeKind.CXType_Elaborated)
        {
            var named = clang.getTypeDeclaration(type);
            if (clang.getCursorKind(named) == CXCursorKind.CXCursor_TypedefDecl)
                return Spelling(named);
        }

        return null!;
    }

    private static string RecordName(CXType recordType)
    {
        // 用声明游标的名字：类型拼写可能带 const 等限定词（const struct obs_scene），
        // 直接用会生出 ConstObsScene 这种假类型名
        var name = Spelling(clang.getTypeDeclaration(recordType));
        if (name.Length > 0)
            return name;

        var spelling = clang.getTypeSpelling(recordType).ToString();
        var idx = spelling.LastIndexOf("::", StringComparison.Ordinal);
        return idx >= 0 ? spelling[(idx + 2)..] : spelling;
    }

    private static string Spelling(CXCursor cursor)
        => clang.getCursorSpelling(cursor).ToString();

    private static string MapScalar(CXTypeKind kind, string spelling) => kind switch
    {
        CXTypeKind.CXType_Bool => "byte",   // C++ bool 是 1 字节（结构体字段布局用）
        CXTypeKind.CXType_Char_S or CXTypeKind.CXType_SChar => "sbyte",
        CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UChar => "byte",
        CXTypeKind.CXType_Short => "short",
        CXTypeKind.CXType_UShort => "ushort",
        CXTypeKind.CXType_Int => "int",
        CXTypeKind.CXType_UInt => "uint",
        CXTypeKind.CXType_Long => "CLong",      // C long 用 .NET 的 CLong（Win32/Unix64 位宽都对）
        CXTypeKind.CXType_ULong => "CULong",
        CXTypeKind.CXType_LongLong => "long",
        CXTypeKind.CXType_ULongLong => "ulong",
        CXTypeKind.CXType_Float => "float",
        CXTypeKind.CXType_Double => "double",
        _ => throw new NotSupportedException($"不支持的标量类型：{spelling}"),
    };
}
