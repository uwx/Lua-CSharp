using Microsoft.CodeAnalysis;

namespace Lua.SourceGenerator;

/// <summary>
/// Maps C# types to LuaCATS annotation type names for .d.lua definition file generation.
/// </summary>
static class LuaTypeMapping
{
    /// <summary>
    /// Gets the LuaCATS type name for a C# type symbol.
    /// Returns null for types that should be omitted (void, CancellationToken).
    /// </summary>
    public static string? GetLuaCATSTypeName(
        ITypeSymbol typeSymbol,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        var comparer = SymbolEqualityComparer.Default;

        // --- Well-known mapped types ---
        if (comparer.Equals(typeSymbol, references.Boolean))
            return "boolean";
        if (comparer.Equals(typeSymbol, references.String))
            return "string";
        if (comparer.Equals(typeSymbol, references.Double))
            return "number";
        if (comparer.Equals(typeSymbol, references.Object))
            return "any";
        if (comparer.Equals(typeSymbol, references.LuaValue))
            return "LuaValue";
        if (comparer.Equals(typeSymbol, references.LuaFunction))
            return "function";
        if (comparer.Equals(typeSymbol, references.LuaTable))
            return "table";
        if (comparer.Equals(typeSymbol, references.CancellationToken))
            return null; // skipped — not exposed to Lua

        // --- SpecialType numeric ---
        switch (typeSymbol.SpecialType)
        {
            case SpecialType.System_Single:
            case SpecialType.System_Decimal:
                return "number";
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Int16:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_UInt32:
            case SpecialType.System_UInt64:
            case SpecialType.System_UInt16:
                return "integer";
            case SpecialType.System_Void:
                return null;
        }

        // --- FixedMathSharp types (by name, since not in SymbolReferences) ---
        // Map to manually-defined LuaCATS types from globals.lua.
        var typeFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (typeFullName == "global::FixedMathSharp.Fixed64")
            return "fixed64";
        if (typeFullName == "global::FixedMathSharp.Vector3d")
            return "fixed64vector3";

        // --- Nullable<T> ---
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } nullableType
            && nullableType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            var inner = GetLuaCATSTypeName(
                nullableType.TypeArguments[0], references, compilation, knownTypes);
            return inner == null ? null : $"{inner}?";
        }

        // --- Async unwrapping (Task<T>, ValueTask<T>, UniTask<T>, Awaitable<T>) ---
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } asyncType)
        {
            var asyncFullName = asyncType.ConstructedFrom
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (asyncFullName is "global::System.Threading.Tasks.Task<TResult>"
                or "global::System.Threading.Tasks.ValueTask<TResult>"
                or "global::Cysharp.Threading.Tasks.UniTask<T>"
                or "global::UnityEngine.Awaitable<T>")
            {
                return GetLuaCATSTypeName(
                    asyncType.TypeArguments[0], references, compilation, knownTypes);
            }
        }

        // Non-generic Task / ValueTask / UniTask / Awaitable → no return
        if (typeSymbol is INamedTypeSymbol nonGenericAsync)
        {
            var ngFullName = nonGenericAsync
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (ngFullName is "global::System.Threading.Tasks.Task"
                or "global::System.Threading.Tasks.ValueTask"
                or "global::Cysharp.Threading.Tasks.UniTask"
                or "global::UnityEngine.Awaitable")
            {
                return null;
            }
        }

        // --- Arrays ---
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elem = GetLuaCATSTypeName(
                arrayType.ElementType, references, compilation, knownTypes);
            return elem == null ? "any[]" : $"{elem}[]";
        }

        // --- Collection types (List<T>, IList<T>, etc.) ---
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } collectionType)
        {
            var colFullName = collectionType.ConstructedFrom
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (colFullName is "global::System.Collections.Generic.List<T>"
                or "global::System.Collections.Generic.IList<T>"
                or "global::System.Collections.Generic.IEnumerable<T>"
                or "global::System.Collections.Generic.ICollection<T>"
                or "global::System.Collections.Generic.IReadOnlyList<T>"
                or "global::System.Collections.Generic.IReadOnlyCollection<T>"
                or "global::System.Collections.Generic.HashSet<T>"
                or "global::System.Collections.Generic.ISet<T>")
            {
                var elem = GetLuaCATSTypeName(
                    collectionType.TypeArguments[0], references, compilation, knownTypes);
                return elem == null ? "any[]" : $"{elem}[]";
            }

            if (colFullName is "global::System.Collections.Generic.Dictionary<TKey,TValue>"
                or "global::System.Collections.Generic.IDictionary<TKey,TValue>"
                or "global::System.Collections.Generic.IReadOnlyDictionary<TKey,TValue>")
            {
                var keyType = GetLuaCATSTypeName(
                    collectionType.TypeArguments[0], references, compilation, knownTypes);
                var valueType = GetLuaCATSTypeName(
                    collectionType.TypeArguments[1], references, compilation, knownTypes);
                return $"table<{keyType ?? "any"}, {valueType ?? "any"}>";
            }
        }

        // --- Enums ---
        if (typeSymbol.TypeKind == TypeKind.Enum)
            return typeSymbol.Name;

        // --- Known [LuaObject] types (exact match or open generic) ---
        if (typeSymbol is INamedTypeSymbol namedSymbol)
        {
            TypeMetadata? matchedMeta = null;

            // Exact match
            if (knownTypes.TryGetValue(namedSymbol, out var exactMeta))
                matchedMeta = exactMeta;
            // Open generic definition match (e.g., UnlimitedArray<T> in knownTypes,
            // but the property type is UnlimitedArray<bool> — a constructed generic)
            else if (namedSymbol.IsGenericType
                     && knownTypes.TryGetValue(namedSymbol.ConstructedFrom, out var openMeta))
                matchedMeta = openMeta;

            if (matchedMeta != null)
            {
                // Use the simple name (no <T>) as base, then resolve generic args.
                var baseName = matchedMeta.LuaObjectName ?? namedSymbol.Name;
                return ResolveGenericName(baseName, namedSymbol, references, compilation, knownTypes);
            }
        }

        // --- ILuaUserData implementations ---
        if (compilation.ClassifyCommonConversion(typeSymbol, references.LuaUserData).Exists)
        {
            if (typeSymbol is INamedTypeSymbol { IsGenericType: true } userDataGeneric)
            {
                return ResolveGenericName(
                    typeSymbol.Name, userDataGeneric, references, compilation, knownTypes);
            }
            return typeSymbol.Name;
        }

        // --- Types convertible to LuaValue (light userdata) ---
        if (compilation.ClassifyCommonConversion(typeSymbol, references.LuaValue).Exists)
            return "any";

        // --- Fallback: use the type's short name (with generic args if applicable) ---
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } fallbackGeneric)
        {
            return ResolveGenericName(
                typeSymbol.Name, fallbackGeneric, references, compilation, knownTypes);
        }
        return typeSymbol.Name;
    }

    /// <summary>
    /// Given a base type name and a (possibly constructed) generic type symbol,
    /// produces "Name&lt;A, B&gt;" with recursively-resolved type arguments.
    /// If the type is non-generic, returns just <paramref name="baseName"/>.
    /// </summary>
    static string ResolveGenericName(
        string baseName,
        INamedTypeSymbol typeSymbol,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        if (!typeSymbol.IsGenericType || typeSymbol.TypeArguments.Length == 0)
            return baseName;

        var args = new List<string>(typeSymbol.TypeArguments.Length);
        foreach (var arg in typeSymbol.TypeArguments)
        {
            var resolved = GetLuaCATSTypeName(arg, references, compilation, knownTypes);
            args.Add(resolved ?? "any");
        }

        return $"{baseName}<{string.Join(", ", args)}>";
    }

    /// <summary>
    /// Gets the return type name for a method, handling async unwrapping and void.
    /// </summary>
    public static string? GetMethodReturnTypeName(
        MethodMetadata method,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        if (!method.HasReturnValue)
            return null;

        return GetLuaCATSTypeName(
            method.Symbol.ReturnType, references, compilation, knownTypes);
    }

    /// <summary>
    /// Gets the LuaCATS parameter type name. Returns null for params that
    /// should be skipped (CancellationToken).
    /// </summary>
    public static string? GetParameterTypeName(
        IParameterSymbol parameter,
        SymbolReferences references,
        Compilation compilation,
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes
    )
    {
        return GetLuaCATSTypeName(parameter.Type, references, compilation, knownTypes);
    }

    /// <summary>
    /// Special mapping for Lua metamethod names to their LuaCATS @operator names.
    /// </summary>
    public static string? GetOperatorName(LuaObjectMetamethod metamethod)
    {
        return metamethod switch
        {
            LuaObjectMetamethod.Add => "add",
            LuaObjectMetamethod.Sub => "sub",
            LuaObjectMetamethod.Mul => "mul",
            LuaObjectMetamethod.Div => "div",
            LuaObjectMetamethod.Mod => "mod",
            LuaObjectMetamethod.Pow => "pow",
            LuaObjectMetamethod.Unm => "unm",
            LuaObjectMetamethod.Len => "len",
            LuaObjectMetamethod.Eq => "eq",
            LuaObjectMetamethod.Lt => "lt",
            LuaObjectMetamethod.Le => "le",
            LuaObjectMetamethod.Call => "call",
            LuaObjectMetamethod.Concat => "concat",
            // These metamethods don't have @operator equivalents in LuaCATS:
            LuaObjectMetamethod.Pairs => null,
            LuaObjectMetamethod.IPairs => null,
            LuaObjectMetamethod.ToString => null,
            LuaObjectMetamethod.Index => null,
            LuaObjectMetamethod.NewIndex => null,
            _ => null,
        };
    }
}
