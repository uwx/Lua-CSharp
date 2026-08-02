using Microsoft.CodeAnalysis;

namespace Lua.SourceGenerator;

class MethodMetadata
{
    public IMethodSymbol Symbol { get; }
    public bool IsStatic { get; }
    public bool IsAsync { get; }
    public bool HasReturnValue { get; }
    public bool HasMemberAttribute { get; }
    public bool HasMetamethodAttribute { get; }
    public bool IsAutoDetectedOperator { get; }
    public string LuaMemberName { get; }
    public LuaObjectMetamethod Metamethod { get; }

    public MethodMetadata(IMethodSymbol symbol, SymbolReferences references)
    {
        Symbol = symbol;
        IsStatic = symbol.IsStatic;

        var returnType = symbol.ReturnType;
        var fullName =
            (
                returnType.ContainingNamespace.IsGlobalNamespace
                    ? ""
                    : returnType.ContainingNamespace + "."
            ) + returnType.Name;
        IsAsync =
            fullName
                is "System.Threading.Tasks.Task"
                    or "System.Threading.Tasks.ValueTask"
                    or "Cysharp.Threading.Tasks.UniTask"
                    or "UnityEngine.Awaitable";

        HasReturnValue =
            !symbol.ReturnsVoid
            && !(IsAsync && returnType is INamedTypeSymbol n && !n.IsGenericType);

        LuaMemberName = symbol.Name;

        var memberAttribute = symbol.GetAttribute(references.LuaMemberAttribute);
        HasMemberAttribute = memberAttribute != null;

        if (memberAttribute != null)
        {
            if (memberAttribute.ConstructorArguments.Length > 0)
            {
                var value = memberAttribute.ConstructorArguments[0].Value;
                if (value is string str)
                {
                    LuaMemberName = str;
                }
            }
        }

        var metamethodAttribute = symbol.GetAttribute(references.LuaMetamethodAttribute);
        HasMetamethodAttribute = metamethodAttribute != null;

        if (metamethodAttribute != null)
        {
            Metamethod = (LuaObjectMetamethod)
                Enum.Parse(
                    typeof(LuaObjectMetamethod),
                    metamethodAttribute.ConstructorArguments[0].Value!.ToString()
                );
        }
        else if (!HasMemberAttribute && TryGetOperatorMetamethod(symbol) is { } autoMetamethod)
        {
            // Auto-detect operator overloads as metamethods when there's no
            // explicit [LuaMetamethod] or [LuaMember] attribute and all
            // parameter types match the declaring type.
            HasMetamethodAttribute = true;
            IsAutoDetectedOperator = true;
            Metamethod = autoMetamethod;
        }
    }

    /// <summary>
    /// Maps a C# user-defined operator to its corresponding Lua metamethod,
    /// or null if the operator has no Lua metamethod equivalent or the parameter
    /// types don't all match the declaring type.
    /// </summary>
    static LuaObjectMetamethod? TryGetOperatorMetamethod(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.UserDefinedOperator)
            return null;

        // Only generate metamethods for operators whose parameters all match
        // the declaring type — this ensures clean mapping to Lua's metamethod
        // signatures (where the left operand's metatable provides the handler).
        var declaringType = method.ContainingType;
        var comparer = SymbolEqualityComparer.Default;
        foreach (var param in method.Parameters)
        {
            if (!comparer.Equals(param.Type, declaringType))
                return null;
        }

        return method.Name switch
        {
            "op_Addition" => LuaObjectMetamethod.Add,
            "op_Subtraction" when method.Parameters.Length == 2 => LuaObjectMetamethod.Sub,
            "op_Subtraction" when method.Parameters.Length == 1 => LuaObjectMetamethod.Unm,
            "op_Multiply" => LuaObjectMetamethod.Mul,
            "op_Division" => LuaObjectMetamethod.Div,
            "op_Modulus" => LuaObjectMetamethod.Mod,
            "op_UnaryNegation" => LuaObjectMetamethod.Unm,
            "op_Equality" => LuaObjectMetamethod.Eq,
            "op_LessThan" => LuaObjectMetamethod.Lt,
            "op_LessThanOrEqual" => LuaObjectMetamethod.Le,
            _ => null,
        };
    }
}
