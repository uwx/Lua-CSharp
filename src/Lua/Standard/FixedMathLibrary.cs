using System.Globalization;
using FixedMathSharp;
using Lua.Runtime;

namespace Lua.Standard;

public sealed class FixedMathLibrary
{
    public static readonly FixedMathLibrary Instance = new();

    public FixedMathLibrary()
    {
        var vecLibName = "fixed64vec3";
        VectorFunctions =
        [
            new(vecLibName, "normalized", Normalized),
            new(vecLibName, "cross", Cross),
            new(vecLibName, "dot", Dot),
            new(vecLibName, "distance", Distance),
            new(vecLibName, "sqrdistance", SqrDistance),
            new(vecLibName, "magnitude", Magnitude),
            new(vecLibName, "sqrmagnitude", SqrMagnitude),
            new(vecLibName, "max", Max),
            new(vecLibName, "min", Min),
            new(vecLibName, "lerp", Lerp),
            new(vecLibName, "abs", Abs),
            new(vecLibName, "sign", Sign),
        ];
    }

    public readonly LibraryFunction[] VectorFunctions;

    // ---- Global constructor functions ----

    /// <summary>
    /// fixed64(value) — creates a Fixed64 Lua value.
    /// Accepts a number (double), string (parsed via Fixed64.TryParse), or another Fixed64.
    /// </summary>
    public ValueTask<int> Fixed64Constructor(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (arg0.TryReadFixed64(out var f64))
        {
            return new(context.Return((LuaValue)f64));
        }

        if (arg0.TryReadString(out var str))
        {
            if (Fixed64.TryParse(str, CultureInfo.InvariantCulture, out var parsed))
            {
                return new(context.Return((LuaValue)parsed));
            }

            LuaRuntimeException.BadArgument(context.State, 1, "fixed64",
                $"cannot parse '{str}' as Fixed64");
        }

        LuaRuntimeException.BadArgument(context.State, 1, "fixed64", arg0.TypeToString());
        return default;
    }

    /// <summary>
    /// fixed64vector3(x, y, z) — creates a Fixed64Vector3 Lua value.
    /// Each argument is converted to Fixed64 via TryReadFixed64 (accepts Number or Fixed64).
    /// </summary>
    public ValueTask<int> Fixed64Vector3Constructor(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        var y = context.GetArgument<Fixed64>(1);
        var z = context.GetArgument<Fixed64>(2);

        return new(context.Return((LuaValue)new Vector3d(x, y, z)));
    }

    // ---- Vector math functions (fixed64vec3.*) ----

    public ValueTask<int> Normalized(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var v = context.GetArgument<Vector3d>(0);
        return new(context.Return((LuaValue)Vector3d.GetNormalized(v)));
    }

    public ValueTask<int> Cross(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Vector3d>(0);
        var b = context.GetArgument<Vector3d>(1);
        return new(context.Return((LuaValue)Vector3d.Cross(a, b)));
    }

    public ValueTask<int> Dot(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Vector3d>(0);
        var b = context.GetArgument<Vector3d>(1);
        return new(context.Return((LuaValue)Vector3d.Dot(a, b)));
    }

    public ValueTask<int> Distance(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Vector3d>(0);
        var b = context.GetArgument<Vector3d>(1);
        return new(context.Return((LuaValue)Vector3d.Distance(a, b)));
    }

    public ValueTask<int> SqrDistance(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Vector3d>(0);
        var b = context.GetArgument<Vector3d>(1);
        return new(context.Return((LuaValue)Vector3d.DistanceSquared(a, b)));
    }

    public ValueTask<int> Magnitude(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var v = context.GetArgument<Vector3d>(0);
        return new(context.Return((LuaValue)v.Length()));
    }

    public ValueTask<int> SqrMagnitude(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var v = context.GetArgument<Vector3d>(0);
        return new(context.Return((LuaValue)v.LengthSquared()));
    }

    public ValueTask<int> Max(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Vector3d>(0);
        var b = context.GetArgument<Vector3d>(1);
        return new(context.Return((LuaValue)Vector3d.Max(a, b)));
    }

    public ValueTask<int> Min(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Vector3d>(0);
        var b = context.GetArgument<Vector3d>(1);
        return new(context.Return((LuaValue)Vector3d.Min(a, b)));
    }

    public ValueTask<int> Lerp(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Vector3d>(0);
        var b = context.GetArgument<Vector3d>(1);
        var t = context.GetArgument<Fixed64>(2);
        return new(context.Return((LuaValue)Vector3d.Lerp(a, b, t)));
    }

    public ValueTask<int> Abs(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var v = context.GetArgument<Vector3d>(0);
        return new(context.Return((LuaValue)Vector3d.Abs(v)));
    }

    public ValueTask<int> Sign(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var v = context.GetArgument<Vector3d>(0);
        return new(context.Return((LuaValue)Vector3d.Sign(v)));
    }
}
