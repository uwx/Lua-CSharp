using System.Globalization;
using FixedMathSharp;
using Lua.Runtime;
using NFMWorldLibrary.FixedMath;

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

        var angleLibName = "f64anglelib";
        AngleFunctions =
        [
            new(angleLibName, "from_radians", AngleFromRadians),
            new(angleLibName, "from_degrees", AngleFromDegrees),
            new(angleLibName, "wrap", AngleWrap),
            new(angleLibName, "wrap_positive", AngleWrapPositive),
            new(angleLibName, "min", AngleMin),
            new(angleLibName, "max", AngleMax),
            new(angleLibName, "degrees", AngleDegrees),
            new(angleLibName, "radians", AngleRadians),
        ];

        var eulerLibName = "f64eulerlib";
        EulerFunctions =
        [
            new(eulerLibName, "wrap", EulerWrap),
            new(eulerLibName, "wrap_positive", EulerWrapPositive),
        ];
    }

    public readonly LibraryFunction[] VectorFunctions;
    public readonly LibraryFunction[] AngleFunctions;
    public readonly LibraryFunction[] EulerFunctions;

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

    // ---- f64Angle constructor ----

    /// <summary>
    /// f64angle(degrees) — creates an f64AngleSingle Lua value.
    /// Accepts a number (interpreted as degrees) or a string.
    /// </summary>
    public ValueTask<int> AngleConstructor(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument(0);

        if (arg0.TryReadFixed64Angle(out var angle))
        {
            return new(context.Return((LuaValue)angle));
        }

        if (arg0.TryReadFixed64(out var f64))
        {
            return new(context.Return((LuaValue)f64AngleSingle.FromDegrees(f64)));
        }

        if (arg0.TryReadString(out var str))
        {
            if (Fixed64.TryParse(str, CultureInfo.InvariantCulture, out var degrees))
            {
                return new(context.Return((LuaValue)f64AngleSingle.FromDegrees(degrees)));
            }

            LuaRuntimeException.BadArgument(context.State, 1, "f64angle",
                $"cannot parse '{str}' as degrees");
        }

        // Accept number → degrees
        if (arg0.TryReadDouble(out var d))
        {
            return new(context.Return((LuaValue)f64AngleSingle.FromDegrees((Fixed64)d)));
        }

        LuaRuntimeException.BadArgument(context.State, 1, "f64angle", arg0.TypeToString());
        return default;
    }

    // ---- f64Euler constructor ----

    /// <summary>
    /// f64euler(yaw, pitch, roll) — creates an f64Euler Lua value.
    /// Each argument is converted to f64AngleSingle (degrees from a number, or an existing angle).
    /// </summary>
    public ValueTask<int> EulerConstructor(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var yaw = GetAngleArg(context, 0);
        var pitch = GetAngleArg(context, 1);
        var roll = GetAngleArg(context, 2);

        return new(context.Return((LuaValue)new f64Euler(yaw, pitch, roll)));
    }

    private static f64AngleSingle GetAngleArg(LuaFunctionExecutionContext context, int index)
    {
        var arg = context.GetArgument(index);

        if (arg.TryReadFixed64Angle(out var angle))
            return angle;

        if (arg.TryReadDouble(out var d))
            return f64AngleSingle.FromDegrees((Fixed64)d);

        if (arg.TryReadFixed64(out var f64))
            return f64AngleSingle.FromDegrees(f64);

        LuaRuntimeException.BadArgument(context.State, index + 1, "f64angle or number",
            arg.TypeToString());
        return default;
    }

    // ---- f64anglelib.* helper functions ----

    public ValueTask<int> AngleFromRadians(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var r = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)f64AngleSingle.FromRadians(r)));
    }

    public ValueTask<int> AngleFromDegrees(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var d = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)f64AngleSingle.FromDegrees(d)));
    }

    public ValueTask<int> AngleWrap(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<f64AngleSingle>(0);
        a.Wrap();
        return new(context.Return((LuaValue)a));
    }

    public ValueTask<int> AngleWrapPositive(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<f64AngleSingle>(0);
        a.WrapPositive();
        return new(context.Return((LuaValue)a));
    }

    public ValueTask<int> AngleMin(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<f64AngleSingle>(0);
        var b = context.GetArgument<f64AngleSingle>(1);
        return new(context.Return((LuaValue)f64AngleSingle.Min(a, b)));
    }

    public ValueTask<int> AngleMax(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<f64AngleSingle>(0);
        var b = context.GetArgument<f64AngleSingle>(1);
        return new(context.Return((LuaValue)f64AngleSingle.Max(a, b)));
    }

    public ValueTask<int> AngleDegrees(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<f64AngleSingle>(0);
        return new(context.Return((LuaValue)a.Degrees));
    }

    public ValueTask<int> AngleRadians(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<f64AngleSingle>(0);
        return new(context.Return((LuaValue)a.Radians));
    }

    // ---- f64eulerlib.* helper functions ----

    public ValueTask<int> EulerWrap(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var e = context.GetArgument<f64Euler>(0);
        return new(context.Return((LuaValue)e.Wrap()));
    }

    public ValueTask<int> EulerWrapPositive(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var e = context.GetArgument<f64Euler>(0);
        return new(context.Return((LuaValue)e.WrapPositive()));
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
