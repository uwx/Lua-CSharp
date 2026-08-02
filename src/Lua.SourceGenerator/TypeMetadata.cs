using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lua.SourceGenerator;

class TypeMetadata
{
    public TypeDeclarationSyntax Syntax { get; }
    public INamedTypeSymbol Symbol { get; }
    public string TypeName { get; }
    public string FullTypeName { get; }
    public string? LuaObjectName { get; }
    public PropertyMetadata[] Properties { get; }
    public MethodMetadata[] Methods { get; }

    public TypeMetadata(
        TypeDeclarationSyntax syntax,
        INamedTypeSymbol symbol,
        SymbolReferences references
    )
    {
        Syntax = syntax;
        Symbol = symbol;

        TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        FullTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Read custom Lua type name from [LuaObject("name")] if set.
        var luaObjectAttr = symbol.GetAttribute(references.LuaObjectAttribute);
        if (luaObjectAttr != null)
        {
            // Check constructor argument first: [LuaObject("name")]
            if (luaObjectAttr.ConstructorArguments.Length > 0
                && luaObjectAttr.ConstructorArguments[0].Value is string ctorName)
            {
                LuaObjectName = ctorName;
            }
            // Then check named property: [LuaObject(Name = "name")]
            else
            {
                foreach (var kvp in luaObjectAttr.NamedArguments)
                {
                    if (kvp.Key == "Name" && kvp.Value.Value is string propName)
                    {
                        LuaObjectName = propName;
                        break;
                    }
                }
            }
        }

        Properties = Symbol
            .GetAllMembers(false)
            .Where(x => x is (IFieldSymbol or IPropertySymbol) and { IsImplicitlyDeclared: false })
            .Where(x =>
            {
                if (!x.ContainsAttribute(references.LuaMemberAttribute))
                {
                    return false;
                }

                if (x.ContainsAttribute(references.LuaIgnoreMemberAttribute))
                {
                    return false;
                }

                if (x is IPropertySymbol p)
                {
                    if (p.IsIndexer)
                    {
                        return false;
                    }
                }

                return true;
            })
            .Select(x => new PropertyMetadata(x, references))
            .ToArray();

        Methods = Symbol
            .GetAllMembers(false)
            .Where(x => x is IMethodSymbol and { IsImplicitlyDeclared: false })
            .Select(x => (IMethodSymbol)x)
            .Where(x =>
            {
                if (x.ContainsAttribute(references.LuaIgnoreMemberAttribute))
                    return false;

                return x.ContainsAttribute(references.LuaMemberAttribute)
                    || x.ContainsAttribute(references.LuaMetamethodAttribute)
                    || x.MethodKind == MethodKind.UserDefinedOperator;
            })
            .Select(x => new MethodMetadata(x, references))
            .ToArray();
    }

    public bool IsPartial()
    {
        return Syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    public bool IsNested()
    {
        return Syntax.Parent is TypeDeclarationSyntax;
    }
}
