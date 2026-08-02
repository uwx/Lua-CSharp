using Microsoft.CodeAnalysis;

namespace Lua.SourceGenerator;

static class SymbolExtensions
{
    public static bool ContainsAttribute(this ISymbol symbol, INamedTypeSymbol attribtue)
    {
        return symbol
            .GetAttributes()
            .Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, attribtue));
    }

    public static AttributeData? GetAttribute(this ISymbol symbol, INamedTypeSymbol attribtue)
    {
        return symbol
            .GetAttributes()
            .FirstOrDefault(x =>
                SymbolEqualityComparer.Default.Equals(x.AttributeClass, attribtue)
            );
    }

    public static IEnumerable<ISymbol> GetAllMembers(
        this INamedTypeSymbol symbol,
        bool withoutOverride = true
    )
    {
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        // Walk base class chain (parent → derived)
        if (symbol.BaseType != null)
        {
            foreach (var item in GetAllMembers(symbol.BaseType))
            {
                if (!withoutOverride || !item.IsOverride)
                {
                    seen.Add(item);
                    yield return item;
                }
            }
        }

        // Walk base interfaces recursively (diamond-deduplicated)
        foreach (var iface in symbol.AllInterfaces)
        {
            foreach (var item in iface.GetMembers())
            {
                if (seen.Add(item))
                {
                    yield return item;
                }
            }
        }

        // Own members last so they shadow base members with the same name
        foreach (var item in symbol.GetMembers())
        {
            if (!withoutOverride || !item.IsOverride)
            {
                if (seen.Add(item))
                {
                    yield return item;
                }
            }
        }
    }
}
