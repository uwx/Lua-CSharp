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

        var mathLibName = "f64math";
        MathFunctions =
        [
            new(mathLibName, "sin", Fixed64Sin),
            new(mathLibName, "cos", Fixed64Cos),
            new(mathLibName, "tan", Fixed64Tan),
            new(mathLibName, "asin", Fixed64Asin),
            new(mathLibName, "acos", Fixed64Acos),
            new(mathLibName, "atan", Fixed64Atan),
            new(mathLibName, "atan2", Fixed64Atan2),
            new(mathLibName, "sqrt", Fixed64Sqrt),
            new(mathLibName, "pow", Fixed64Pow),
            new(mathLibName, "ln", Fixed64Ln),
            new(mathLibName, "log2", Fixed64Log2),
            new(mathLibName, "abs", Fixed64Abs),
            new(mathLibName, "floor", Fixed64Floor),
            new(mathLibName, "ceil", Fixed64Ceil),
            new(mathLibName, "round", Fixed64Round),
            new(mathLibName, "min", Fixed64Min),
            new(mathLibName, "max", Fixed64Max),
            new(mathLibName, "clamp", Fixed64Clamp),
            new(mathLibName, "clamp01", Fixed64Clamp01),
            new(mathLibName, "sign", Fixed64Sign),
            new(mathLibName, "lerp", Fixed64Lerp),
            new(mathLibName, "hypot", Fixed64Hypot),
            new(mathLibName, "deg2rad", Fixed64DegToRad),
            new(mathLibName, "rad2deg", Fixed64RadToDeg),
        ];
    }

    public readonly LibraryFunction[] VectorFunctions;
    public readonly LibraryFunction[] AngleFunctions;
    public readonly LibraryFunction[] EulerFunctions;
    public readonly LibraryFunction[] MathFunctions;

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

    // ---- f64math.* scalar math functions (Fixed64) ----

    public ValueTask<int> Fixed64Sin(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Sin(x)));
    }

    public ValueTask<int> Fixed64Cos(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Cos(x)));
    }

    public ValueTask<int> Fixed64Tan(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Tan(x)));
    }

    public ValueTask<int> Fixed64Asin(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Asin(x)));
    }

    public ValueTask<int> Fixed64Acos(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Acos(x)));
    }

    public ValueTask<int> Fixed64Atan(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Atan(x)));
    }

    public ValueTask<int> Fixed64Atan2(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var y = context.GetArgument<Fixed64>(0);
        var x = context.GetArgument<Fixed64>(1);
        return new(context.Return((LuaValue)FixedMath.Atan2(y, x)));
    }

    public ValueTask<int> Fixed64Sqrt(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Sqrt(x)));
    }

    public ValueTask<int> Fixed64Pow(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var b = context.GetArgument<Fixed64>(0);
        var e = context.GetArgument<Fixed64>(1);
        return new(context.Return((LuaValue)FixedMath.Pow(b, e)));
    }

    public ValueTask<int> Fixed64Ln(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Ln(x)));
    }

    public ValueTask<int> Fixed64Log2(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Log2(x)));
    }

    public ValueTask<int> Fixed64Abs(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Abs(x)));
    }

    public ValueTask<int> Fixed64Floor(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Floor(x)));
    }

    public ValueTask<int> Fixed64Ceil(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Ceiling(x)));
    }

    public ValueTask<int> Fixed64Round(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Round(x)));
    }

    public ValueTask<int> Fixed64Min(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Fixed64>(0);
        var b = context.GetArgument<Fixed64>(1);
        return new(context.Return((LuaValue)FixedMath.Min(a, b)));
    }

    public ValueTask<int> Fixed64Max(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Fixed64>(0);
        var b = context.GetArgument<Fixed64>(1);
        return new(context.Return((LuaValue)FixedMath.Max(a, b)));
    }

    public ValueTask<int> Fixed64Clamp(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var v = context.GetArgument<Fixed64>(0);
        var min = context.GetArgument<Fixed64>(1);
        var max = context.GetArgument<Fixed64>(2);
        return new(context.Return((LuaValue)FixedMath.Clamp(v, min, max)));
    }

    public ValueTask<int> Fixed64Clamp01(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var v = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.Clamp01(v)));
    }

    public ValueTask<int> Fixed64Sign(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)(Fixed64)Fixed64.Sign(x)));
    }

    public ValueTask<int> Fixed64Lerp(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Fixed64>(0);
        var b = context.GetArgument<Fixed64>(1);
        var t = context.GetArgument<Fixed64>(2);
        return new(context.Return((LuaValue)Fixed64.Lerp(a, b, t)));
    }

    public ValueTask<int> Fixed64Hypot(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var a = context.GetArgument<Fixed64>(0);
        var b = context.GetArgument<Fixed64>(1);
        return new(context.Return((LuaValue)Fixed64.Hypot(a, b)));
    }

    public ValueTask<int> Fixed64DegToRad(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.DegToRad(x)));
    }

    public ValueTask<int> Fixed64RadToDeg(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = context.GetArgument<Fixed64>(0);
        return new(context.Return((LuaValue)FixedMath.RadToDeg(x)));
    }
}
