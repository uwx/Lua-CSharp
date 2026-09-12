using System.Runtime.CompilerServices;
using System.Text;
using Lua.Internal.CompilerServices;
using Lua.Standard;

namespace Lua.Runtime;

/// <summary>
/// Helper functions loaded by <see cref="OpCode.LoadBuiltin"/>. The numeric values appear
/// in the bytecode as the Bx operand, so they are part of the ABI — only append members.
/// </summary>
internal enum LuaBuiltin
{
    /// <summary>`__iter` resolution for Luau generalized iteration.</summary>
    IterResolver = 0,

    /// <summary>Concatenation of interpolated-string parts, using tostring semantics.</summary>
    InterpolationBuilder = 1,
}

/// <summary>
/// Creates the Luau helper functions referenced by <see cref="OpCode.LoadBuiltin"/>. They are
/// ordinary C# functions rather than globals so user code cannot shadow them, and they are not
/// prototype constants so dumped chunks stay serializable.
/// </summary>
static class LuaBuiltins
{
    public static LuaValue[] CreateAll()
    {
        return
        [
            new LuaFunction("__iter", IterResolver),
            new LuaFunction("__interp", InterpolationBuilder),
        ];
    }

    /// <summary>
    /// Resolves the iterator triple for Luau generalized iteration (`for k, v in value do`).
    /// A value with an `__iter` metamethod delegates to it; a table iterates with `next`;
    /// a function is its own iterator (classic `for x in f do` keeps working).
    /// </summary>
    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<int> IterResolver(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var value = context.GetArgumentOrDefault(0);

        if (value.TryGetMetamethod(context.GlobalState, Metamethods.Iter, out var metamethod))
        {
            var results = await context.State.CallAsync(
                metamethod,
                new[] { value },
                cancellationToken
            );

            // Luau pads missing results with nil rather than rejecting the iterator.
            return context.Return(
                results.Length > 0 ? results[0] : LuaValue.Nil,
                results.Length > 1 ? results[1] : LuaValue.Nil,
                results.Length > 2 ? results[2] : LuaValue.Nil
            );
        }

        if (value.TryReadTable(out var table))
        {
            // The shared `next`-backed iterator keeps TForCall's in-place fast path.
            return context.Return(BasicLibrary.Instance.NextFunction, table, LuaValue.Nil);
        }

        if (value.TryReadFunction(out var function))
        {
            return context.Return(function, LuaValue.Nil, LuaValue.Nil);
        }

        throw new LuaRuntimeException(
            context.State,
            $"attempt to iterate over a {value.TypeToString()} value"
        );
    }

    /// <summary>
    /// Builds the string for a Luau interpolated string literal. The compiler pushes the
    /// literal parts and the evaluated expressions as arguments; each is converted with the
    /// same rules as `tostring` (so `__tostring` and number formatting match).
    /// </summary>
    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<int> InterpolationBuilder(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var argumentCount = context.ArgumentCount;
        var builder = new StringBuilder();

        for (var i = 0; i < argumentCount; i++)
        {
            var value = context.Arguments[i];
            if (value.TryReadString(out var text))
            {
                builder.Append(text);
                continue;
            }

            await value.CallToStringAsync(context, cancellationToken);
            builder.Append(context.State.Stack.Pop().Read<string>());
        }

        return context.Return(builder.ToString());
    }
}
