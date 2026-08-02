using System.Text;
using Microsoft.CodeAnalysis;

namespace Lua.SourceGenerator;

partial class LuaObjectGenerator
{
    /// <summary>
    /// Emits a LuaCATS .d.lua definition file containing annotations for all
    /// [LuaObject] types and their referenced enums.
    /// </summary>
    static void TryEmitLuaCATS(
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes,
        SymbolReferences references,
        Compilation compilation,
        string outputDirectory,
        string assemblyName,
        in SourceProductionContext context
    )
    {
        try
        {
            var sb = new StringBuilder();

            // --- File header ---
            sb.AppendLine($"---@meta {assemblyName}");
            sb.AppendLine();

            // --- Collect and emit enums ---
            var enums = EnumCollector.Collect(knownTypes, references, compilation);
            foreach (var enumMeta in enums)
            {
                EmitLuaEnum(sb, enumMeta);
            }

            // --- Emit class definitions (deterministic order) ---
            var orderedTypes = knownTypes.Values
                .OrderBy(t => t.FullTypeName, StringComparer.Ordinal)
                .ToList();

            foreach (var typeMeta in orderedTypes)
            {
                EmitLuaClass(sb, typeMeta, references, compilation, knownTypes);
            }

            // --- Write to disk ---
            var fileName = $"{assemblyName}.d.lua";
            var filePath = Path.Combine(outputDirectory, fileName);

            // Ensure directory exists
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.LuaCATSEmitFailed,
                    Location.None,
                    ex.Message
                )
            );
        }
    }

    // ──────────────────────────────────────────────
    //  Enum emission
    // ──────────────────────────────────────────────

    static void EmitLuaEnum(StringBuilder sb, EnumMetadata enumMeta)
    {
        sb.AppendLine($"---@enum {enumMeta.Name}");
        sb.AppendLine($"{enumMeta.Name} = {{");

        foreach (var member in enumMeta.Members)
        {
            var valueStr = member.Value switch
            {
                int i => i.ToString(),
                long l => l.ToString(),
                _ => member.Value?.ToString() ?? "0",
            };
            sb.AppendLine($"    {member.Name} = {valueStr},");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    // ──────────────────────────────────────────────
    //  Class emission
    // ──────────────────────────────────────────────

    static void EmitLuaClass(
        StringBuilder sb,
        TypeMetadata typeMeta,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        var luaName = typeMeta.LuaObjectName ?? typeMeta.TypeName;

        // --- @class declaration with inheritance ---
        var baseTypes = new List<string>();

        // Add base type if it's a [LuaObject]
        if (typeMeta.Symbol.BaseType != null
            && knownTypes.ContainsKey(typeMeta.Symbol.BaseType))
        {
            var baseMeta = knownTypes[typeMeta.Symbol.BaseType];
            baseTypes.Add(baseMeta.LuaObjectName ?? baseMeta.TypeName);
        }

        // Add ILuaUserData if the type implements it directly
        foreach (var iface in typeMeta.Symbol.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, references.LuaUserData))
            {
                // ILuaUserData is implicit — don't add to parent list unless needed
                break;
            }
        }

        if (baseTypes.Count > 0)
        {
            sb.AppendLine($"---@class {luaName} : {string.Join(", ", baseTypes)}");
        }
        else
        {
            sb.AppendLine($"---@class {luaName}");
        }

        // --- @field annotations (must immediately follow @class) ---
        foreach (var prop in typeMeta.Properties)
        {
            EmitLuaField(sb, prop, references, compilation, knownTypes);
        }

        // --- @operator annotations ---
        EmitLuaOperators(sb, typeMeta, references, compilation, knownTypes);

        // --- Class table placeholder ---
        sb.AppendLine($"{luaName} = {{}}");
        sb.AppendLine();

        // --- Method stubs ---
        EmitLuaMethods(sb, typeMeta, references, compilation, knownTypes);

        sb.AppendLine();
    }

    // ──────────────────────────────────────────────
    //  Field emission
    // ──────────────────────────────────────────────

    static void EmitLuaField(
        StringBuilder sb,
        PropertyMetadata prop,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        var typeName = LuaTypeMapping.GetLuaCATSTypeName(
            prop.Type, references, compilation, knownTypes) ?? "any";

        var comment = "";
        if (prop.IsReadOnly && !prop.IsWriteOnly)
            comment = " --- (read-only)";
        else if (prop.IsWriteOnly && !prop.IsReadOnly)
            comment = " --- (write-only)";

        var scope = prop.IsStatic ? "public " : "";

        sb.AppendLine($"---@field {scope}{prop.LuaMemberName} {typeName}{comment}");
    }

    // ──────────────────────────────────────────────
    //  Operator (@operator) emission
    // ──────────────────────────────────────────────

    static void EmitLuaOperators(
        StringBuilder sb,
        TypeMetadata typeMeta,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        var emittedOps = new HashSet<string>();

        foreach (var method in typeMeta.Methods)
        {
            // Only emit @operator for metamethods that map to Lua operators
            if (!method.HasMetamethodAttribute)
                continue;

            var opName = LuaTypeMapping.GetOperatorName(method.Metamethod);
            if (opName == null)
                continue;

            // Deduplicate (auto-detected operator might overlap with explicit metamethod)
            if (!emittedOps.Add(opName))
                continue;

            // Build parameter type list
            var paramTypes = new List<string>();
            foreach (var param in method.Symbol.Parameters)
            {
                // Skip CancellationToken
                if (SymbolEqualityComparer.Default.Equals(
                        param.Type, references.CancellationToken))
                    continue;

                var pt = LuaTypeMapping.GetParameterTypeName(
                    param, references, compilation, knownTypes);
                paramTypes.Add(pt ?? "any");
            }

            // Build return type
            var returnType = LuaTypeMapping.GetMethodReturnTypeName(
                method, references, compilation, knownTypes);

            // Format: @operator add(LVec3):LVec3  or  @operator unm:LVec3
            if (paramTypes.Count > 0)
            {
                sb.Append($"---@operator {opName}({string.Join(", ", paramTypes)})");
            }
            else
            {
                sb.Append($"---@operator {opName}");
            }

            if (returnType != null)
            {
                sb.AppendLine($":{returnType}");
            }
            else
            {
                sb.AppendLine();
            }
        }
    }

    // ──────────────────────────────────────────────
    //  Method emission
    // ──────────────────────────────────────────────

    static void EmitLuaMethods(
        StringBuilder sb,
        TypeMetadata typeMeta,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        var luaName = typeMeta.LuaObjectName ?? typeMeta.TypeName;

        foreach (var method in typeMeta.Methods)
        {
            // Skip pure-metamethod methods (operators already emitted as @operator)
            // Emit methods that have [LuaMember] explicitly
            if (!method.HasMemberAttribute)
            {
                // Auto-detected operators: already covered by @operator
                // Explicit [LuaMetamethod] only: skip unless it has a LuaMemberName
                // that users can call directly
                if (method.IsAutoDetectedOperator || method.HasMetamethodAttribute)
                    continue;
                continue;
            }

            EmitLuaMethodStub(sb, method, luaName, references, compilation, knownTypes);
        }
    }

    static void EmitLuaMethodStub(
        StringBuilder sb,
        MethodMetadata method,
        string luaName,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        // --- @async annotation ---
        if (method.IsAsync)
        {
            sb.AppendLine("---@async");
        }

        // --- Collect regular params, out params, ref params ---
        var regularParams = new List<IParameterSymbol>();
        var outParams = new List<IParameterSymbol>();
        var refParams = new List<IParameterSymbol>();

        foreach (var param in method.Symbol.Parameters)
        {
            // Skip CancellationToken
            if (SymbolEqualityComparer.Default.Equals(
                    param.Type, references.CancellationToken))
                continue;

            if (param.RefKind == RefKind.Out)
                outParams.Add(param);
            else if (param.RefKind == RefKind.Ref)
                refParams.Add(param);
            else
                regularParams.Add(param);
        }

        // --- @param annotations ---
        foreach (var param in regularParams)
        {
            var typeName = LuaTypeMapping.GetParameterTypeName(
                param, references, compilation, knownTypes);

            var optional = param.HasExplicitDefaultValue ? "?" : "";
            sb.AppendLine($"---@param {param.Name}{optional} {typeName ?? "any"}");
        }

        // ref params: appear as both @param and @return
        foreach (var param in refParams)
        {
            var typeName = LuaTypeMapping.GetParameterTypeName(
                param, references, compilation, knownTypes);
            sb.AppendLine($"---@param {param.Name} {typeName ?? "any"}");
        }

        // --- @return annotations ---
        // Main return value
        var mainReturn = LuaTypeMapping.GetMethodReturnTypeName(
            method, references, compilation, knownTypes);
        if (mainReturn != null)
        {
            sb.AppendLine($"---@return {mainReturn}");
        }

        // out params → additional @return values
        foreach (var param in outParams)
        {
            var typeName = LuaTypeMapping.GetParameterTypeName(
                param, references, compilation, knownTypes);
            sb.AppendLine($"---@return {typeName ?? "any"} {param.Name}");
        }

        // ref params → additional @return values (Lua treats ref as in+out)
        foreach (var param in refParams)
        {
            var typeName = LuaTypeMapping.GetParameterTypeName(
                param, references, compilation, knownTypes);
            sb.AppendLine($"---@return {typeName ?? "any"} {param.Name}");
        }

        // --- Build parameter list for function signature ---
        var sigParams = new List<string>();
        foreach (var param in regularParams)
        {
            sigParams.Add(param.Name);
        }
        foreach (var param in refParams)
        {
            sigParams.Add(param.Name);
        }
        // out params are NOT in the function signature (Lua convention)

        // --- Function stub ---
        var methodName = method.LuaMemberName;
        if (method.IsStatic)
        {
            sb.AppendLine(
                $"function {luaName}.{methodName}({string.Join(", ", sigParams)}) end");
        }
        else
        {
            sb.AppendLine(
                $"function {luaName}:{methodName}({string.Join(", ", sigParams)}) end");
        }
    }
}
