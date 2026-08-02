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
        var typeFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (typeFullName == "global::FixedMathSharp.Fixed64")
            return "number";
        if (typeFullName == "global::FixedMathSharp.Vector3d")
            return "Vector3d";

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

        // --- Known [LuaObject] types ---
        if (typeSymbol is INamedTypeSymbol namedSymbol
            && knownTypes.TryGetValue(namedSymbol, out var typeMeta))
        {
            return typeMeta.LuaObjectName ?? typeMeta.TypeName;
        }

        // --- ILuaUserData implementations ---
        if (compilation.ClassifyCommonConversion(typeSymbol, references.LuaUserData).Exists)
            return typeSymbol.Name;

        // --- Types convertible to LuaValue (light userdata) ---
        if (compilation.ClassifyCommonConversion(typeSymbol, references.LuaValue).Exists)
            return "any";

        // --- Fallback: use the type's short name ---
        return typeSymbol.Name;
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
