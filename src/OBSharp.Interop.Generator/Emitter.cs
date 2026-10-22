using System.Text;

namespace OBSharp.Interop.Generator;

/// <summary>
/// 发射层：文本模板发射，不用 SyntaxFactory，产物可读可 diff。
/// 输出 *.g.cs：每个句柄/分组类一个文件 + Enums/Delegates/Structs 各一个。
/// </summary>
internal sealed class Emitter
{
    private readonly GenerateCommand _options;
    private readonly Model _model;
    private readonly Dictionary<string, ClassModel> _classes = [with(StringComparer.Ordinal)];

    public Emitter(GenerateCommand options, Model model)
    {
        _options = options;
        _model = model;
        Naming.Initialize(model);
        _structNames = model.Structs.Values.Select(s => Naming.TypeName(s.Name))
            .ToHashSet(StringComparer.Ordinal);
        BuildClassModel();
    }

    private readonly HashSet<string> _structNames;

    // ---- 类模型 ----

    private sealed class ClassModel
    {
        public required string Name;
        public bool IsHandle;
        public bool IsExtension;            // 结构体扩展类（C# 14 extension 块）
        public string? ExtensionReceiver;   // 如 "ref Vec2 self" / "Vec2 self"
        public string? BaseName;            // 句柄基类（仅当基类也是生成的句柄）
        public bool IsRoot = true;          // 继承链根（相等性 / IsInvalid 发在根上）
        public readonly List<string> Members = [];   // 公开方法（类级 / extension 块外）
        public readonly List<string> ExtensionMembers = [];      // extension(值 self) 块内
        public readonly List<string> RefExtensionMembers = [];   // extension(ref self) 块内
        public readonly List<string> Imports = [];   // 私有 LibraryImport
        public readonly HashSet<string> UsedNames = [with(StringComparer.Ordinal)];
        public readonly HashSet<string> Signatures = [with(StringComparer.Ordinal)]; // 名字+参数类型（判隐藏用）
    }

    private void BuildClassModel()
    {
        foreach (var handle in _model.Handles.Values)
        {
            _classes[handle.Name] = new ClassModel { Name = handle.Name, IsHandle = true };
        }

        foreach (var handle in _model.Handles.Values)
        {
            if (handle.BaseName is not null && _model.Handles.ContainsKey(handle.BaseName))
            {
                _classes[handle.Name].BaseName = handle.BaseName;
                _classes[handle.Name].IsRoot = false;
            }
        }

        foreach (var function in _model.Functions)
        {
            var (owner, _, structReceiver) = ResolveOwner(function);
            if (!_classes.TryGetValue(owner, out var created))
            {
                created = new ClassModel { Name = owner };
                // {Struct}Extension 名字本身（分组类撞结构体重名收编进来的静态方法）也是扩展类
                const string suffix = "Extension";
                if (owner.EndsWith(suffix, StringComparison.Ordinal)
                    && _structNames.Contains(owner[..^suffix.Length]))
                    created.IsExtension = true;
                _classes[owner] = created;
            }

            if (structReceiver)
            {
                // 接收者统一记「值 self」形态；ref 形态块由发射期按需生成（"ref " + 同一串）
                created.IsExtension = true;
                created.ExtensionReceiver ??= $"{CsNameOf(function.Sig.Parameters[0].Type)} self";
            }
        }
    }

    /// <summary>
    /// 函数归属：布局结构体接收者 → XxxExtension（C# 14 extension 块）>
    /// this 的句柄类 > 家族句柄类 > 静态分组类。
    /// </summary>
    private (string Owner, bool DropFirst, bool StructReceiver) ResolveOwner(FunctionDecl function)
    {
        var bare = function.Name.StartsWith("obsharp_", StringComparison.Ordinal)
            ? function.Name["obsharp_".Length..]
            : function.Name;
        var family = Naming.FamilyOf(bare);
        var familyClass = family is null ? null : Naming.FamilyClass(family);
        var firstParam = function.Sig.Parameters.Count > 0 ? function.Sig.Parameters[0].Type : null;
        var firstParamHandle = firstParam?.Kind is TypeKind.Handle;

        // 布局结构体接收者：生成 extension 块（vec2_add → Vec2Extension.Add）
        if (firstParam?.Kind is TypeKind.StructPtr)
            return ($"{Naming.TypeName(firstParam.DeclName!)}Extension", DropFirst: true, StructReceiver: true);

        if (familyClass is not null)
        {
            if (firstParamHandle && firstParam!.DeclName == familyClass)
            {
                // create 系返回的就是家族句柄时，首参是 parent 不是 this → 静态工厂
                var firstParamIsFactoryParent = function.Sig.Return.Kind is TypeKind.Handle
                    && function.Sig.Return.DeclName == familyClass;
                return (familyClass, DropFirst: !firstParamIsFactoryParent, StructReceiver: false);
            }

            return (familyClass, DropFirst: false, StructReceiver: false);
        }

        if (family is not null && Naming.FamilyGroups.TryGetValue(family, out var groupClass))
            return (groupClass, false, false);

        if (firstParamHandle)
            return (firstParam!.DeclName!, true, false);

        var group = Naming.GroupClass(function.Header);
        return (ResolveGroupName(group), false, false);
    }

    /// <summary>
    /// 分组类名和布局结构体重名时（dstr.h 的自由函数 vs struct dstr），
    /// 静态方法收进该结构体的 Extension 类（类级，不进 extension 块），不再另立同名类。
    /// </summary>
    private string ResolveGroupName(string group)
        => _structNames.Contains(group) ? $"{group}Extension" : group;

    private static string CsNameOf(TypeRef type)
        => Naming.TypeAlias(type.DeclName!) ?? Naming.TypeName(type.DeclName!);

    // ---- 发射 ----

    public void Emit()
    {
        var output = Path.GetFullPath(_options.Output);
        Directory.CreateDirectory(output);

        foreach (var stale in Directory.EnumerateFiles(output, "*.g.cs"))
            File.Delete(stale);

        // 两遍：先全量命名，再判祖先撞签名（派生类同签名成员要 new 隐藏），最后发射
        var assigned = new List<(FunctionDecl Function, string Owner, string MethodName, bool IsStatic, string Signature, bool StructReceiver)>();
        foreach (var function in _model.Functions)
        {
            var (owner, dropFirst, structReceiver) = ResolveOwner(function);
            var classModel = _classes[owner];
            var methodName = UniqueName(classModel, Naming.MethodName(function.Name));
            var signature = SignatureKey(function, methodName, dropFirst);
            classModel.Signatures.Add(signature);
            assigned.Add((function, owner, methodName, !dropFirst, signature, structReceiver));
        }

        foreach (var (function, owner, methodName, isStatic, signature, structReceiver) in assigned)
        {
            var classModel = _classes[owner];
            var hide = AncestorHasSignature(classModel.BaseName, signature)
                || (classModel.IsHandle && methodName is "Close" or "Dispose" or "GetType" or "ToString");
            EmitFunction(function, owner, methodName, isStatic, hide, structReceiver);
        }

        var written = new List<string>();

        foreach (var classModel in _classes.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
            written.Add(WriteFile(output, $"{classModel.Name}.g.cs", EmitClass(classModel)));

        if (_model.Enums.Count > 0)
            written.Add(WriteFile(output, "Enums.g.cs", EmitEnums()));

        if (_model.Delegates.Count > 0)
            written.Add(WriteFile(output, "Delegates.g.cs", EmitDelegates()));

        if (_model.Structs.Count > 0)
            written.Add(WriteFile(output, "Structs.g.cs", EmitStructs()));

        Console.WriteLine($"OBSharp.Interop.Generator: {_model.Functions.Count} 个函数、" +
                          $"{_model.Handles.Count} 个句柄、{_model.Enums.Count} 个枚举、" +
                          $"{_model.Delegates.Count} 个委托、{_model.Structs.Count} 个结构体 → {output}");
        Console.WriteLine($"  产物 {written.Count} 个 .g.cs 文件");
    }

    private static string WriteFile(string output, string fileName, string content)
    {
        var path = Path.Combine(output, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string FileHeader() =>
        """
        // <auto-generated>
        // 由 OBSharp.Interop.Generator 生成，请勿手改。重新生成会覆盖本文件。
        // </auto-generated>
        #nullable enable

        using System;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;

        """;

    // ---- 单个函数 → 成员 ----

    private void EmitFunction(FunctionDecl function, string owner, string methodName, bool isStatic, bool hide, bool structReceiver)
    {
        var classModel = _classes[owner];

        var publicParams = new List<string>();
        var rawParams = new List<string>();
        var callArgs = new List<string>();
        var preLines = new List<string>();
        var postLines = new List<string>();
        var refSelf = false;    // 接收者是否 ref 形态（可变 self）

        string? inArrayParam = null;        // 入参数组的公开参数名（string[]/句柄[]）
        string? structArrayParam = null;    // 结构体数组参数名

        for (int i = 0; i < function.Sig.Parameters.Count; i++)
        {
            var param = function.Sig.Parameters[i];
            var id = Naming.CamelIdentifier(param.Name);   // 参数名：camelCase
            var tmp = Naming.Identifier(param.Name);       // 生成的临时变量名：Pascal
            if (i is 0 && !isStatic)
            {
                // 接收者：公开签名删掉，raw 签名保留；
                // 句柄传 this，结构体扩展传 self（extension 块的接收者）
                refSelf = structReceiver && param.Type.Pointee?.IsConst is false;
                if (structReceiver && NeedsPointerForm(param.Type))
                {
                    var selfType = CsName(param.Type.DeclName!);
                    rawParams.Add($"{selfType}* {id}");
                    if (param.Type.Pointee?.IsConst is false)
                    {
                        // ref self：要 fixed 才能取址
                        callArgs.Add($"p{tmp}");
                        preLines.Add($"        fixed ({selfType}* p{tmp} = &self)");
                        preLines.Add("        {");
                        postLines.Add("        }");
                    }
                    else
                    {
                        // 按值 self：本身就是固定地址，直接 &self
                        callArgs.Add("&self");
                    }
                }
                else
                {
                    rawParams.Add($"{RawParamType(param.Type)} {id}");
                    callArgs.Add(structReceiver
                        ? param.Type.Pointee?.IsConst is false ? "ref self" : "self"
                        : "this");
                }

                continue;
            }

            switch (param.Type.Kind)
            {
                case TypeKind.OutScalar when param.Name is "__size":
                    rawParams.Add("out nuint size");
                    callArgs.Add("out nuint size");
                    break;

                case TypeKind.Scalar when param.Name is "__len":
                    rawParams.Add("nuint len");
                    callArgs.Add($"(nuint){inArrayParam}.Length");
                    break;

                case TypeKind.Scalar when param.Name is "__count":
                    rawParams.Add($"int {id}Count");
                    callArgs.Add($"{structArrayParam}.Length");
                    break;

                case TypeKind.StringArray:
                case TypeKind.HandleArray:
                    inArrayParam = id;
                    publicParams.Add($"{PublicParamType(param.Type)} {id}");
                    rawParams.Add($"nint* {id}");
                    callArgs.Add($"p{tmp}");
                    preLines.Add(InArrayPrologue(param.Type, id, tmp));
                    postLines.Add(InArrayEpilogue(param.Type, tmp));
                    break;

                case TypeKind.StructPtr when NextIsCount(function, i):
                    structArrayParam = id;
                    publicParams.Add($"{CsName(param.Type.DeclName!)}[] {id}");
                    rawParams.Add($"{CsName(param.Type.DeclName!)}* {id}");
                    callArgs.Add($"p{tmp}");
                    preLines.Add($"        fixed ({CsName(param.Type.DeclName!)}* p{tmp} = {id})");
                    preLines.Add("        {");
                    postLines.Add("        }");
                    break;

                case TypeKind.StructPtr when NeedsPointerForm(param.Type):
                    {
                        // 默认形态：别名类型（System.Numerics）/含别名字段的结构体走指针形，
                        // 公开面照旧 in/ref，这里 fixed 取址
                        var structType = CsName(param.Type.DeclName!);
                        publicParams.Add($"{PublicParamType(param.Type)} {id}");
                        rawParams.Add($"{structType}* {id}");
                        callArgs.Add($"p{tmp}");
                        preLines.Add($"        fixed ({structType}* p{tmp} = &{id})");
                        preLines.Add("        {");
                        postLines.Add("        }");
                        break;
                    }

                default:
                    publicParams.Add($"{PublicParamType(param.Type)} {id}");
                    rawParams.Add($"{RawParamType(param.Type)} {id}");
                    callArgs.Add(param.Type.Kind switch
                    {
                        TypeKind.OutScalar => $"out {id}",
                        TypeKind.RefPtr or TypeKind.DelegatePtr => $"ref {id}",
                        TypeKind.StructPtr when param.Type.Pointee?.IsConst is false => $"ref {id}",
                        _ => id,
                    });
                    break;
            }
        }

        // 返回值
        var returnType = function.Sig.Return;
        var owned = Naming.ReturnsOwnedHandle(function, _model);
        var (rawReturn, returnAttrs) = RawReturn(returnType);
        var publicReturn = PublicReturnType(returnType, owned);
        var call = $"{function.Name}({string.Join(", ", callArgs)})";

        // 方法体：有 fixed 包装（指针形参数/接收者）时，调用在块内、返回在块外
        string body;
        var hasPre = preLines.Count > 0;
        var closes = postLines.Count > 0
            ? $"\n{string.Join("\n", Enumerable.Reverse(postLines))}"
            : string.Empty;
        if (returnType.Kind is TypeKind.Handle && !hasPre)
        {
            body = owned
                ? $"    => new({call}, true); // 拥有句柄：Dispose 时释放"
                : $$"""
                    {
                        nint p = {{call}};
                        return p is 0 ? null : new(p, false); // 借用句柄：不负责释放
                    }
                    """;
        }
        else if (returnType.Kind is TypeKind.StringArray or TypeKind.HandleArray)
        {
            body = hasPre
                ? $$"""
                  {
                  {{string.Join("\n", preLines)}}
                  {{ArrayReturnBody(returnType, call)}}{{closes}}
                      }
                  """
                : ArrayReturnBody(returnType, call);
        }
        else if (hasPre)
        {
            if (returnType.Kind is TypeKind.Handle)
            {
                body = owned
                    ? $$"""
                      {
                              nint __result;
                      {{string.Join("\n", preLines)}}
                              __result = {{call}};
                      {{closes}}
                              return new(__result, true); // 拥有句柄：Dispose 时释放
                          }
                      """
                    : $$"""
                      {
                              nint __result;
                      {{string.Join("\n", preLines)}}
                              __result = {{call}};
                      {{closes}}
                              return __result is 0 ? null : new(__result, false); // 借用句柄：不负责释放
                          }
                      """;
            }
            else if (publicReturn is "void")
            {
                body =
                    $$"""
                    {
                    {{string.Join("\n", preLines)}}
                            {{call}};
                    {{closes}}
                        }
                    """;
            }
            else
            {
                body =
                    $$"""
                    {
                            {{publicReturn}} __result;
                    {{string.Join("\n", preLines)}}
                            __result = {{call}};
                    {{closes}}
                            return __result;
                        }
                    """;
            }
        }
        else
        {
            body = $"    => {call};";
        }

        // Close/Dispose 会撞 SafeHandle 的同名成员：显式 new 隐藏并保住语义（薄封装的
        // Close 是转发 obsharp_widget_close，不是关句柄）
        var hideKeyword = hide ? "new " : string.Empty;
        var modifiers = (isStatic ? "public static " : "public ") + hideKeyword;
        var signature = $"{modifiers}{publicReturn} {methodName}({string.Join(", ", publicParams)})";

        var doc = $"""
            /// <summary>
            /// C ABI: <c>{function.Name}</c>
            /// </summary>
        """;
        var targetList = !structReceiver ? classModel.Members
            : refSelf ? classModel.RefExtensionMembers
            : classModel.ExtensionMembers;
        targetList.Add(
            $$"""
            {{doc}}
                {{signature}}
                {{body}}
            """);

        // 原始 P/Invoke（类内 private static partial）
        var raw = new StringBuilder();
        raw.AppendLine("    [LibraryImport(" + Literal(LibFor(function.Name)) + $", EntryPoint = \"{function.Name}\")]");
        raw.AppendLine("    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]");
        foreach (var attr in returnAttrs)
            raw.AppendLine("    " + attr);
        raw.Append($"    private static partial {rawReturn} {function.Name}({string.Join(", ", rawParams)});");
        classModel.Imports.Add(raw.ToString());
    }

    private static bool NextIsCount(FunctionDecl function, int index)
        => index + 1 < function.Sig.Parameters.Count
            && function.Sig.Parameters[index + 1].Name is "__count";

    private static string UniqueName(ClassModel classModel, string name)
    {
        var candidate = name;
        for (int i = 2; !classModel.UsedNames.Add(candidate); i++)
            candidate = name + i;
        return candidate;
    }

    // ---- 数组入参 ----

    private static string InArrayPrologue(TypeRef type, string id, string tmp)
    {
        if (type.Kind is TypeKind.StringArray)
        {
            return
                $$"""
                        {
                            var ptr{{tmp}} = new nint[{{id}}.Length];
                            try
                            {
                                for (int i = 0; i < {{id}}.Length; i++)
                                    ptr{{tmp}}[i] = Marshal.StringToCoTaskMemUTF8({{id}}[i]);
                                fixed (nint* p{{tmp}} = ptr{{tmp}})
                                {
                """;
        }

        return
            $$"""
                    {
                        var ptr{{tmp}} = new nint[{{id}}.Length];
                        for (int i = 0; i < {{id}}.Length; i++)
                            ptr{{tmp}}[i] = {{id}}[i]?.DangerousGetHandle() ?? 0;
                        fixed (nint* p{{tmp}} = ptr{{tmp}})
                        {
                """;
    }

    private static string InArrayEpilogue(TypeRef type, string tmp)
    {
        if (type.Kind is TypeKind.StringArray)
        {
            return
                $$"""
                                }
                            }
                            finally
                            {
                                foreach (nint ptr in ptr{{tmp}})
                                    if (ptr != 0)
                                        Marshal.FreeCoTaskMem(ptr);
                            }
                        }
                """;
        }

        return
            $$"""
                        }
                    }
                """;
    }

    private static string ArrayReturnBody(TypeRef returnType, string call)
    {
        if (returnType.Kind is TypeKind.StringArray)
        {
            return
                $$"""
                {
                        nint p = {{call}};
                        int n = checked((int)size);
                        var result = new string[n];
                        for (int i = 0; i < n; i++)
                            result[i] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p, i * nint.Size)) ?? string.Empty;
                        return result;
                    }
                """;
        }

        var handle = returnType.DeclName!;
        return
            $$"""
            {
                    nint p = {{call}};
                    int n = checked((int)size);
                    var result = new {{handle}}[n];
                    for (int i = 0; i < n; i++)
                        result[i] = new {{handle}}(Marshal.ReadIntPtr(p, i * nint.Size), false); // 借用句柄
                    return result;
                }
            """;
    }

    // ---- 类 ----

    private string EmitClass(ClassModel classModel)
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader());
        sb.AppendLine($"namespace {_options.Namespace};");
        sb.AppendLine();

        // 结构体扩展类：C# 14 extension 块（vec2_add → Vec2Extension 里的 extension(Vec2 self)）
        if (classModel.IsExtension)
        {
            sb.AppendLine($"public static unsafe partial class {classModel.Name}");
            sb.AppendLine("{");
            if (classModel.ExtensionReceiver is not null && classModel.ExtensionMembers.Count > 0)
            {
                sb.AppendLine($"    extension({classModel.ExtensionReceiver})");
                sb.AppendLine("    {");
                foreach (var member in classModel.ExtensionMembers)
                {
                    sb.AppendLine(member);
                    sb.AppendLine();
                }

                sb.AppendLine("    }");
                sb.AppendLine();
            }

            // 可变接收者单独一个 ref 块：值 self 和 ref self 不能混在同一个 extension 块里
            if (classModel.ExtensionReceiver is not null && classModel.RefExtensionMembers.Count > 0)
            {
                sb.AppendLine($"    extension(ref {classModel.ExtensionReceiver})");
                sb.AppendLine("    {");
                foreach (var member in classModel.RefExtensionMembers)
                {
                    sb.AppendLine(member);
                    sb.AppendLine();
                }

                sb.AppendLine("    }");
                sb.AppendLine();
            }

            foreach (var member in classModel.Members)
            {
                sb.AppendLine(member);
                sb.AppendLine();
            }

            foreach (var import in classModel.Imports)
            {
                sb.AppendLine(import);
                sb.AppendLine();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        // 派生句柄按 C++ 继承关系续到基类上（QCefWidget : QWidget），
        // 这样 Create(QWidget? parent) 才能传派生类型进去
        var baseList = classModel.IsHandle
            ? " : " + (classModel.BaseName ?? "SafeHandle")
                + (classModel.IsRoot ? $", IEquatable<{classModel.Name}>" : string.Empty)
            : string.Empty;

        sb.AppendLine($"public unsafe partial class {classModel.Name}{baseList}");
        sb.AppendLine("{");

        if (classModel.IsHandle)
            EmitHandleInfrastructure(sb, classModel);

        foreach (var member in classModel.Members)
        {
            sb.AppendLine(member);
            sb.AppendLine();
        }

        foreach (var import in classModel.Imports)
        {
            sb.AppendLine(import);
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private void EmitHandleInfrastructure(StringBuilder sb, ClassModel classModel)
    {
        var name = classModel.Name;
        // ctor 是 internal：跨类工厂（Window.Create → QWidget）和派生类 base(...) 链
        // 都要能构造，但对插件程序集不公开
        var ctorBody = classModel.BaseName is null
            ? ": base(IntPtr.Zero, ownsHandle) => SetHandle(ptr);"
            : ": base(ptr, ownsHandle) { }";

        sb.AppendLine($"    internal {name}(nint ptr, bool ownsHandle) {ctorBody}");
        sb.AppendLine();

        if (classModel.IsRoot)
        {
            sb.AppendLine("    public override bool IsInvalid => handle is 0;");
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// 句柄判等：两个包装对象指向同一个原生指针即相等。");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public bool Equals({name}? other) => other is not null && handle.Equals(other.handle);");
            sb.AppendLine();
            sb.AppendLine($"    public override bool Equals(object? obj) => obj is {name} other && Equals(other);");
            sb.AppendLine();
            sb.AppendLine("    public override int GetHashCode() => handle.GetHashCode();");
            sb.AppendLine();
            sb.AppendLine($"    public static bool operator ==({name}? left, {name}? right) => left is null ? right is null : left.Equals(right);");
            sb.AppendLine();
            sb.AppendLine($"    public static bool operator !=({name}? left, {name}? right) => !(left == right);");
            sb.AppendLine();
        }

        // ReleaseHandle：本类自己的销毁函数才在这里发射，子类/父类按继承处理
        var destroy = _model.Handles.TryGetValue(name, out var handle) ? handle.DestroyFunction : null;
        if (destroy is not null)
        {
            sb.AppendLine("    protected override bool ReleaseHandle()");
            sb.AppendLine("    {");
            sb.AppendLine($"        {destroy}(this);");
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        else if (AncestorHasDestroy(name))
        {
            // 继承基类的 ReleaseHandle（QWidget 的销毁覆盖 QCefWidget / QFileDialog）
        }
        else
        {
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// 借用句柄：生命周期归原生侧，这里不释放。");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    protected override bool ReleaseHandle() => true;");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// 方法签名键：名字 + 公开参数类型（C# 的隐藏按签名，不看返回值）。
    /// </summary>
    private string SignatureKey(FunctionDecl function, string methodName, bool dropFirst)
    {
        var types = function.Sig.Parameters
            .Where((p, i) => !(i is 0 && dropFirst) && p.Name is not ("__size" or "__count"))
            .Select(p => PublicParamType(p.Type));
        return methodName + "(" + string.Join(",", types) + ")";
    }

    /// <summary>
    /// 基类链上是否已有同签名方法（派生类要用 new 隐藏）。
    /// </summary>
    private bool AncestorHasSignature(string? className, string signature)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = className; current is not null && seen.Add(current);
             current = _classes.TryGetValue(current, out var c) ? c.BaseName : null)
        {
            if (_classes.TryGetValue(current, out var cm) && cm.Signatures.Contains(signature))
                return true;
        }

        return false;
    }

    private bool AncestorHasDestroy(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = _classes.TryGetValue(name, out var c) ? c.BaseName : null;
             current is not null && seen.Add(current);
             current = _classes.TryGetValue(current, out var c2) ? c2.BaseName : null)
        {
            if (_model.Handles.TryGetValue(current, out var handle) && handle.DestroyFunction is not null)
                return true;
        }

        return false;
    }

    // ---- 枚举 / 委托 / 结构体 ----

    private string EmitEnums()
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader());
        sb.AppendLine($"namespace {_options.Namespace};");
        sb.AppendLine();

        foreach (var scope in _model.Enums.Values.SelectMany(e => e.Scope).Distinct().Order())
        {
            sb.AppendLine($"public partial class {scope}");
            sb.AppendLine("{");
            foreach (var info in _model.Enums.Values.Where(e => e.Scope.Count > 0 && e.Scope[^1] == scope)
                                                    .OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                EmitEnum(sb, info, "    ");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var info in _model.Enums.Values.Where(e => e.Scope.Count is 0)
                                                .OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            EmitEnum(sb, info, string.Empty);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void EmitEnum(StringBuilder sb, EnumDecl info, string indent)
    {
        if (info.Values.Any(v => v.Name.Contains("Option", StringComparison.OrdinalIgnoreCase)
                              || v.Name.Contains("Flag", StringComparison.OrdinalIgnoreCase))
            || info.Name.EndsWith("Option", StringComparison.Ordinal)
            || info.Name.EndsWith("Flags", StringComparison.Ordinal)
            || info.Name.EndsWith("Filter", StringComparison.Ordinal)
            || info.Name.EndsWith("Type", StringComparison.Ordinal))
        {
            sb.AppendLine($"{indent}[Flags]");
        }

        sb.AppendLine($"{indent}public enum {info.CsName} : {info.Underlying}");
        sb.AppendLine($"{indent}{{");
        for (int i = 0; i < info.Values.Count; i++)
        {
            var (valueName, value) = info.Values[i];
            var comma = i + 1 < info.Values.Count ? "," : string.Empty;
            sb.AppendLine($"{indent}    {valueName} = {value}{comma}");
        }

        sb.AppendLine($"{indent}}}");
    }

    private string EmitDelegates()
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader());
        sb.AppendLine($"namespace {_options.Namespace};");
        sb.AppendLine();

        foreach (var info in _model.Delegates.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var parameters = info.Signature.Parameters
                .Select(p => DelegateParamType(p.Type) + " " + Naming.CamelIdentifier(p.Name));
            sb.AppendLine("/// <summary>");
            if (_options.DisableRuntimeMarshalling)
            {
                sb.AppendLine("/// 原生回调（Cdecl）。全 blittable：借用字符串是 UTF-8 的 byte*，");
                sb.AppendLine("/// 用 Marshal.PtrToStringUTF8 复制，不释放原内存。");
            }
            else
            {
                sb.AppendLine("/// 原生回调（Cdecl）。字符串是借用指针，封送时只复制不释放。");
            }

            sb.AppendLine("/// </summary>");
            sb.AppendLine("[UnmanagedFunctionPointer(CallingConvention.Cdecl)]");
            foreach (var attr in DelegateReturnAttrs(info.Signature.Return))
                sb.AppendLine(attr);
            var unsafeKeyword = _options.DisableRuntimeMarshalling ? "unsafe " : string.Empty;
            sb.AppendLine($"public {unsafeKeyword}delegate {DelegateReturn(info.Signature.Return)} {Naming.TypeName(info.Name)}({string.Join(", ", parameters)});");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string EmitStructs()
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader());
        sb.AppendLine($"namespace {_options.Namespace};");
        sb.AppendLine();

        foreach (var info in _model.Structs.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.AppendLine("/// <summary>");
            sb.AppendLine("/// 原生结构体：字段顺序与 C 声明一一对应。");
            sb.AppendLine("/// </summary>");
            sb.AppendLine("[StructLayout(LayoutKind.Sequential)]");
            sb.AppendLine($"public unsafe partial struct {CsName(info.Name)}");
            sb.AppendLine("{");
            foreach (var field in info.Fields)
            {
                if (field.Kind is FieldKind.FixedArray)
                    sb.AppendLine($"    public fixed {field.TypeName} {Naming.Identifier(field.Name)}[{field.Count}];");
                else
                    sb.AppendLine($"    public {FieldType(field)} {Naming.Identifier(field.Name)};");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ---- 类型映射 ----

    /// <summary>
    /// 出参标量的 C# 类型（枚举出参走 EnumRef，其余是标量映射名）。
    /// </summary>
    private string OutScalarType(TypeRef type)
        => type.DeclName is not null ? EnumRef(type) : type.Spelling;

    private static string CsName(string cppName)
        => Naming.TypeAlias(cppName) ?? Naming.TypeName(cppName);

    private readonly Dictionary<string, bool> _structContainsAlias = [with(StringComparer.Ordinal)];

    /// <summary>
    /// 布局结构体（或其嵌套字段）是否引用了 .NET 等价类型（System.Numerics 等）。
    /// 这类类型走 LibraryImport 的 in/ref 封送需要 [assembly: DisableRuntimeMarshalling]（SYSLIB1051）。
    /// </summary>
    private bool StructContainsAlias(string structName)
    {
        if (_structContainsAlias.TryGetValue(structName, out var cached))
            return cached;

        var result = false;
        if (_model.Structs.TryGetValue(structName, out var decl))
        {
            foreach (var field in decl.Fields)
            {
                if (field is { Kind: FieldKind.Scalar, TypeName: not null }
                    && Naming.IsAliasType(field.TypeName))
                {
                    result = true;
                    break;
                }

                if (field is { Kind: FieldKind.Struct, TypeName: not null }
                    && StructContainsAlias(field.TypeName))
                {
                    result = true;
                    break;
                }
            }
        }

        _structContainsAlias[structName] = result;
        return result;
    }

    /// <summary>
    /// 默认形态（未禁运行时封送）下，别名类型/含别名字段的结构体在 raw 签名里走指针形
    /// （Vector2*），公开面照旧 in/ref，方法体里 fixed 取址 —— 不触发 SYSLIB1051。
    /// </summary>
    private bool NeedsPointerForm(TypeRef type)
    {
        if (_options.DisableRuntimeMarshalling)
            return false;

        return Naming.TypeAlias(type.DeclName!) is not null
            || StructContainsAlias(type.DeclName!);
    }

    private string EnumRef(TypeRef type)
        => _model.Enums.TryGetValue(type.DeclName!, out var info) ? info.CsRef : type.DeclName!;

    /// <summary>
    /// LibraryImport 参数类型。
    /// </summary>
    private string RawParamType(TypeRef type) => type.Kind switch
    {
        TypeKind.Void => "void",
        TypeKind.Scalar => type.Spelling,
        TypeKind.Bool => "[MarshalAs(UnmanagedType.U1)] bool",
        TypeKind.String => "[MarshalAs(UnmanagedType.LPUTF8Str)] string?",
        TypeKind.VoidPtr => "nint",
        TypeKind.Handle => type.DeclName + "?",
        TypeKind.StructPtr => (type.Pointee?.IsConst is false ? "ref " : "in ") + CsName(type.DeclName!),
        TypeKind.Enum => EnumRef(type),
        TypeKind.Delegate => type.DeclName + "?",
        TypeKind.DelegatePtr => "ref " + type.DeclName + "?",
        TypeKind.RefPtr => "ref nint",
        TypeKind.OutScalar => "out " + OutScalarType(type),
        _ => throw new NotSupportedException($"原始参数类型不支持：{type.Spelling}"),
    };

    /// <summary>
    /// 公开薄封装参数类型。
    /// </summary>
    private string PublicParamType(TypeRef type) => type.Kind switch
    {
        TypeKind.Scalar => type.Spelling,
        TypeKind.Bool => "bool",
        TypeKind.String => "string?",
        TypeKind.VoidPtr => "nint",
        TypeKind.Handle => type.DeclName + "?",
        TypeKind.StructPtr => (type.Pointee?.IsConst is false ? "ref " : "in ") + CsName(type.DeclName!),
        TypeKind.Enum => EnumRef(type),
        TypeKind.Delegate => type.DeclName + "?",
        TypeKind.DelegatePtr => "ref " + type.DeclName + "?",
        TypeKind.RefPtr => "ref nint",
        TypeKind.OutScalar => "out " + OutScalarType(type),
        TypeKind.StringArray => "string[]",
        TypeKind.HandleArray => type.DeclName + "?[]",
        _ => throw new NotSupportedException($"公开参数类型不支持：{type.Spelling}"),
    };

    private (string Raw, List<string> Attrs) RawReturn(TypeRef type)
    {
        return type.Kind switch
        {
            TypeKind.Void => ("void", []),
            TypeKind.Bool => ("bool", ["[return: MarshalAs(UnmanagedType.U1)]"]),
            TypeKind.String => ("string?", [$"[return: MarshalUsing(typeof({_options.StringMarshaller}))]"]),
            TypeKind.Scalar => (type.Spelling, new List<string>()),
            TypeKind.Enum => (EnumRef(type), []),
            TypeKind.VoidPtr => ("nint", []),
            TypeKind.Handle or TypeKind.StringArray or TypeKind.HandleArray => ("nint", []),
            // 布局结构体指针 / char* 等裸指针 / 函数指针返回：给 nint 由调用方处置
            TypeKind.StructPtr or TypeKind.OutScalar or TypeKind.RefPtr or TypeKind.DelegatePtr
                or TypeKind.Delegate => ("nint", []),
            _ => throw new NotSupportedException($"原始返回类型不支持：{type.Spelling}"),
        };
    }

    private string PublicReturnType(TypeRef type, bool ownedHandle) => type.Kind switch
    {
        TypeKind.Void => "void",
        TypeKind.Bool => "bool",
        TypeKind.String => "string?",
        TypeKind.Scalar => type.Spelling,
        TypeKind.Enum => EnumRef(type),
        TypeKind.VoidPtr => "nint",
        TypeKind.Handle => type.DeclName! + (ownedHandle ? string.Empty : "?"),
        TypeKind.StringArray => "string[]",
        TypeKind.HandleArray => type.DeclName! + "?[]",
        TypeKind.StructPtr or TypeKind.OutScalar or TypeKind.RefPtr or TypeKind.DelegatePtr
            or TypeKind.Delegate => "nint",
        _ => throw new NotSupportedException($"公开返回类型不支持：{type.Spelling}"),
    };

    /// <summary>
    /// 委托参数（回调方向）。默认形态走托管封送（LPUTF8Str 字符串、out 出参）；
    /// --disable-runtime-marshalling 形态全 blittable（借用字符串是 byte*，出参是指针）。
    /// </summary>
    private string DelegateParamType(TypeRef type)
    {
        if (!_options.DisableRuntimeMarshalling)
        {
            return type.Kind switch
            {
                TypeKind.Void => "void",
                TypeKind.Scalar => type.Spelling,
                TypeKind.Bool => "[MarshalAs(UnmanagedType.U1)] bool",
                TypeKind.String => "[MarshalAs(UnmanagedType.LPUTF8Str)] string?",
                TypeKind.VoidPtr => "nint",
                TypeKind.Handle => "nint",
                TypeKind.StructPtr => "nint",
                TypeKind.Enum => EnumRef(type),
                TypeKind.Delegate or TypeKind.DelegatePtr or TypeKind.RefPtr
                    or TypeKind.StringArray or TypeKind.HandleArray => "nint",
                TypeKind.OutScalar => "out " + OutScalarType(type),
                _ => throw new NotSupportedException($"委托参数类型不支持：{type.Spelling}"),
            };
        }

        return type.Kind switch
        {
            TypeKind.Void => "void",
            TypeKind.Scalar => type.Spelling,
            TypeKind.Bool => "byte",        // C++ bool 是 1 字节
            TypeKind.String => "byte*",     // 借用 UTF-8 指针
            TypeKind.VoidPtr => "nint",
            TypeKind.Handle => "nint",
            TypeKind.StructPtr => "nint",
            TypeKind.Enum => EnumRef(type),
            TypeKind.Delegate => "nint",
            TypeKind.DelegatePtr => "nint",
            TypeKind.RefPtr => "nint",
            TypeKind.StringArray or TypeKind.HandleArray => "nint",
            // by-ref 出参在委托里也得是裸指针（禁运行时封送下 out/ref 不能封送）
            TypeKind.OutScalar => OutScalarType(type) + "*",
            _ => throw new NotSupportedException($"委托参数类型不支持：{type.Spelling}"),
        };
    }

    private string DelegateReturn(TypeRef type)
    {
        if (!_options.DisableRuntimeMarshalling)
        {
            return type.Kind switch
            {
                TypeKind.Void => "void",
                TypeKind.Scalar => type.Spelling,
                TypeKind.Bool => "bool",
                TypeKind.String => "string?",
                TypeKind.VoidPtr => "nint",
                TypeKind.Handle => "nint",
                TypeKind.StructPtr => "nint",
                TypeKind.Enum => EnumRef(type),
                TypeKind.Delegate or TypeKind.DelegatePtr or TypeKind.RefPtr
                    or TypeKind.StringArray or TypeKind.HandleArray or TypeKind.OutScalar => "nint",
                _ => throw new NotSupportedException($"委托返回类型不支持：{type.Spelling}"),
            };
        }

        return type.Kind switch
        {
            TypeKind.Void => "void",
            TypeKind.Scalar => type.Spelling,
            TypeKind.Bool => "byte",
            TypeKind.String => "byte*",
            TypeKind.VoidPtr => "nint",
            TypeKind.Handle => "nint",
            TypeKind.StructPtr => "nint",
            TypeKind.Enum => EnumRef(type),
            TypeKind.Delegate => "nint",
            TypeKind.DelegatePtr or TypeKind.RefPtr
                or TypeKind.StringArray or TypeKind.HandleArray or TypeKind.OutScalar => "nint",
            _ => throw new NotSupportedException($"委托返回类型不支持：{type.Spelling}"),
        };
    }

    private List<string> DelegateReturnAttrs(TypeRef type)
    {
        if (_options.DisableRuntimeMarshalling)
            return [];

        return type.Kind switch
        {
            TypeKind.Bool => ["[return: MarshalAs(UnmanagedType.U1)]"],
            TypeKind.String => [$"[return: MarshalUsing(typeof({_options.StringMarshaller}))]"],
            _ => [],
        };
    }

    /// <summary>
    /// 结构体字段：函数指针 → delegate* unmanaged[Cdecl]，字符串 → byte*。
    /// </summary>
    private string FieldType(StructField field) => field.Kind switch
    {
        FieldKind.Pointer => "byte*",
        FieldKind.Scalar => field.TypeName!,
        FieldKind.Opaque => "nint",
        FieldKind.Struct => CsName(field.TypeName!),
        FieldKind.Enum => _model.Enums.TryGetValue(field.TypeName!, out var info) ? info.CsRef : field.TypeName!,
        FieldKind.FnPtr => FnPtrType(field.Signature!),
        _ => throw new NotSupportedException($"结构体字段不支持：{field.Name}"),
    };

    private string FnPtrType(FunctionSig sig)
    {
        // delegate* 的泛型参数顺序是参数在前、返回值在最后
        var parts = sig.Parameters.Select(p => FnPtrParam(p.Type)).ToList();
        parts.Add(FnPtrReturn(sig.Return));
        return $"delegate* unmanaged[Cdecl]<{string.Join(", ", parts)}>";
    }

    private string FnPtrParam(TypeRef type) => type.Kind switch
    {
        TypeKind.Void => "void",
        TypeKind.Scalar => type.Spelling,
        TypeKind.Bool => "byte",
        TypeKind.String => "byte*",
        TypeKind.VoidPtr => "nint",
        TypeKind.Handle => "nint",
        TypeKind.StructPtr => CsName(type.DeclName!) + "*",
        TypeKind.Enum => EnumRef(type),
        TypeKind.Delegate => "nint",
        TypeKind.DelegatePtr => "nint",
        TypeKind.RefPtr => "nint",
        TypeKind.StringArray or TypeKind.HandleArray or TypeKind.StructPtr => "nint",
        TypeKind.OutScalar => OutScalarType(type) + "*",
        _ => throw new NotSupportedException($"函数指针参数不支持：{type.Spelling}"),
    };

    private string FnPtrReturn(TypeRef type) => type.Kind switch
    {
        TypeKind.Void => "void",
        TypeKind.Scalar => type.Spelling,
        TypeKind.Bool => "byte",
        TypeKind.String => "byte*",
        TypeKind.VoidPtr => "nint",
        TypeKind.Handle => "nint",
        TypeKind.StructPtr => CsName(type.DeclName!) + "*",
        TypeKind.Enum => EnumRef(type),
        TypeKind.Delegate or TypeKind.DelegatePtr or TypeKind.RefPtr
            or TypeKind.StringArray or TypeKind.HandleArray or TypeKind.OutScalar => "nint",
        _ => throw new NotSupportedException($"函数指针返回不支持：{type.Spelling}"),
    };

    /// <summary>
    /// 函数名 → 库名（--lib-prefix 前缀映射优先，否则 --lib）。
    /// </summary>
    private string LibFor(string functionName)
    {
        string? bestLib = null;
        var bestLength = -1;
        foreach (var mapping in _options.LibPrefix)
        {
            var eq = mapping.IndexOf('=');
            if (eq <= 0)
                continue;
            var prefix = mapping[..eq];
            if (prefix.Length > bestLength && functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                bestLength = prefix.Length;
                bestLib = mapping[(eq + 1)..];
            }
        }

        return bestLib ?? _options.Lib;
    }

    private static string Literal(string value)
    {
        var quote = ((char)34).ToString(); // = "\""，避开转义引号字面量
        return quote + value.Replace("\\", "\\\\").Replace(quote, "\\" + quote) + quote;
    }
}
