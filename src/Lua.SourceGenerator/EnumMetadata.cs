using Microsoft.CodeAnalysis;

namespace Lua.SourceGenerator;

/// <summary>
/// Collects and stores metadata about C# enums referenced by [LuaObject] types.
/// </summary>
class EnumMetadata
{
    public INamedTypeSymbol Symbol { get; }
    public string Name { get; }
    public string FullName { get; }
    public EnumMember[] Members { get; }

    public EnumMetadata(INamedTypeSymbol symbol)
    {
        Symbol = symbol;
        Name = symbol.Name;
        FullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        Members = symbol
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => new EnumMember(f.Name, f.ConstantValue))
            .ToArray();
    }

    public readonly struct EnumMember
    {
        public string Name { get; }
        public object? Value { get; }

        public EnumMember(string name, object? value)
        {
            Name = name;
            Value = value;
        }
    }
}

/// <summary>
/// Utility to collect all unique enums referenced across a set of [LuaObject] types.
/// </summary>
static class EnumCollector
{
    /// <summary>
    /// Scans all property types, method parameter types, and method return types
    /// across all known [LuaObject] types and returns deduplicated enum metadata.
    /// </summary>
    public static EnumMetadata[] Collect(
        Dictionary<INamedTypeSymbol, TypeMetadata> knownTypes,
        SymbolReferences references,
        Compilation compilation
    )
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var enums = new List<EnumMetadata>();

        void Visit(ITypeSymbol? type)
        {
            if (type == null)
                return;

            // Unwrap Nullable<T>
            if (type is INamedTypeSymbol { IsGenericType: true } n
                && n.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            {
                Visit(n.TypeArguments[0]);
                return;
            }

            // Unwrap Task<T>, array<T>, List<T>
            if (type is IArrayTypeSymbol arr)
            {
                Visit(arr.ElementType);
                return;
            }

            if (type is INamedTypeSymbol { IsGenericType: true } generic)
            {
                var fullName = generic.ConstructedFrom
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                if (fullName is "global::System.Threading.Tasks.Task<TResult>"
                    or "global::System.Threading.Tasks.ValueTask<TResult>")
                {
                    Visit(generic.TypeArguments[0]);
                    return;
                }

                if (fullName is "global::System.Collections.Generic.List<T>"
                    or "global::System.Collections.Generic.IList<T>"
                    or "global::System.Collections.Generic.IEnumerable<T>"
                    or "global::System.Collections.Generic.ICollection<T>"
                    or "global::System.Collections.Generic.IReadOnlyList<T>"
                    or "global::System.Collections.Generic.IReadOnlyCollection<T>"
                    or "global::System.Collections.Generic.HashSet<T>"
                    or "global::System.Collections.Generic.ISet<T>")
                {
                    Visit(generic.TypeArguments[0]);
                    return;
                }

                if (fullName is "global::System.Collections.Generic.Dictionary<TKey,TValue>"
                    or "global::System.Collections.Generic.IDictionary<TKey,TValue>"
                    or "global::System.Collections.Generic.IReadOnlyDictionary<TKey,TValue>")
                {
                    Visit(generic.TypeArguments[0]);
                    Visit(generic.TypeArguments[1]);
                    return;
                }
            }

            // It's an enum
            if (type is INamedTypeSymbol namedType
                && type.TypeKind == TypeKind.Enum
                && seen.Add(namedType))
            {
                enums.Add(new EnumMetadata(namedType));
            }
        }

        foreach (var typeMeta in knownTypes.Values)
        {
            // Properties
            foreach (var prop in typeMeta.Properties)
            {
                Visit(prop.Type);
            }

            // Methods — return type and all parameters
            foreach (var method in typeMeta.Methods)
            {
                Visit(method.Symbol.ReturnType);
                foreach (var param in method.Symbol.Parameters)
                {
                    // Skip CancellationToken
                    if (SymbolEqualityComparer.Default.Equals(
                            param.Type, references.CancellationToken))
                        continue;
                    Visit(param.Type);
                }
            }
        }

        return enums.ToArray();
    }
}
