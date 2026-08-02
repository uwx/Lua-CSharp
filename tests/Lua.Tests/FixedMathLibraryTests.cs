using FixedMathSharp;
using Lua.Standard;

namespace Lua.Tests;

public class FixedMathLibraryTests
{
    private LuaState CreateState()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        return state;
    }

    // ---- fixed64(value) constructor ----

    [Test]
    public async Task Fixed64Constructor_FromNumber()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(3.5)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)3.5));
    }

    [Test]
    public async Task Fixed64Constructor_FromString()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64('2.5')");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)2.5));
    }

    [Test]
    public async Task Fixed64Constructor_FromFixed64_ReturnsSame()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(fixed64(4.0))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)4));
    }

    [Test]
    public void Fixed64Constructor_InvalidString_Throws()
    {
        using var state = CreateState();
        Assert.ThrowsAsync<LuaRuntimeException>(
            async () => await state.DoStringAsync("return fixed64('notanumber')").AsTask());
    }

    // ---- fixed64vector3(x, y, z) constructor ----

    [Test]
    public async Task Fixed64Vector3Constructor_FromNumbers()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vector3(1, 2, 3)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(1, 2, 3)));
    }

    [Test]
    public async Task Fixed64Vector3Constructor_FromFixed64()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vector3(fixed64(4), fixed64(5), fixed64(6))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(4, 5, 6)));
    }

    // ---- Fixed64 arithmetic (same-type only) ----

    [Test]
    public async Task Fixed64_Add_SameType()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(2) + fixed64(3)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)5));
    }

    [Test]
    public async Task Fixed64_Sub_SameType()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(10) - fixed64(3)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)7));
    }

    [Test]
    public async Task Fixed64_Mul_SameType()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(4) * fixed64(2.5)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)10));
    }

    [Test]
    public async Task Fixed64_Div_SameType()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(10) / fixed64(4)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)2.5));
    }

    [Test]
    public async Task Fixed64_Unm()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return -fixed64(5)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)(-5)));
    }

    [Test]
    public void Fixed64_CrossTypeArithmetic_Errors()
    {
        using var state = CreateState();
        // Number + Fixed64 should error (not automatically coerced)
        Assert.ThrowsAsync<LuaRuntimeException>(
            async () => await state.DoStringAsync("return 1 + fixed64(2)").AsTask());
    }

    // ---- Fixed64 comparison ----

    [Test]
    public async Task Fixed64_LessThan()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(3) < fixed64(5)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].ToBoolean(), Is.True);
    }

    [Test]
    public async Task Fixed64_LessThanOrEqual()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(5) <= fixed64(5)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].ToBoolean(), Is.True);
    }

    [Test]
    public async Task Fixed64_CompareWithNumber()
    {
        using var state = CreateState();
        // TryReadFixed64 converts Number→Fixed64 for comparison
        var results = await state.DoStringAsync("return fixed64(3) < 5");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].ToBoolean(), Is.True);
    }

    // ---- Fixed64Vector3 arithmetic ----

    [Test]
    public async Task Fixed64Vector3_Add()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vector3(1, 2, 3) + fixed64vector3(4, 5, 6)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(5, 7, 9)));
    }

    [Test]
    public async Task Fixed64Vector3_Sub()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vector3(4, 5, 6) - fixed64vector3(1, 2, 3)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(3, 3, 3)));
    }

    [Test]
    public async Task Fixed64Vector3_MulScalar()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vector3(1, 2, 3) * fixed64(2)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(2, 4, 6)));
    }

    [Test]
    public async Task Fixed64Vector3_MulScalarCommutative()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64(2) * fixed64vector3(1, 2, 3)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(2, 4, 6)));
    }

    [Test]
    public async Task Fixed64Vector3_DivScalar()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vector3(4, 6, 8) / fixed64(2)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(2, 3, 4)));
    }

    [Test]
    public async Task Fixed64Vector3_ComponentWiseMul()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vector3(1, 2, 3) * fixed64vector3(4, 5, 6)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(4, 10, 18)));
    }

    [Test]
    public async Task Fixed64Vector3_Unm()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return -fixed64vector3(1, 2, 3)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(-1, -2, -3)));
    }

    // ---- fixed64vec3.* math functions ----

    [Test]
    public async Task Fixed64Vec3_Normalized()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vec3.normalized(fixed64vector3(3, 0, 0))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(1, 0, 0)));
    }

    [Test]
    public async Task Fixed64Vec3_Cross()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync(
            "local a = fixed64vector3(1, 0, 0); local b = fixed64vector3(0, 1, 0); return fixed64vec3.cross(a, b)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(0, 0, 1)));
    }

    [Test]
    public async Task Fixed64Vec3_Dot()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync(
            "local a = fixed64vector3(1, 0, 0); local b = fixed64vector3(1, 0, 0); return fixed64vec3.dot(a, b)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo(Fixed64.One));
    }

    [Test]
    public async Task Fixed64Vec3_Dot_Perpendicular_Zero()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync(
            "local a = fixed64vector3(1, 0, 0); local b = fixed64vector3(0, 1, 0); return fixed64vec3.dot(a, b)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo(Fixed64.Zero));
    }

    [Test]
    public async Task Fixed64Vec3_Distance()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync(
            "local a = fixed64vector3(0, 0, 0); local b = fixed64vector3(3, 4, 0); return fixed64vec3.distance(a, b)");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)5));
    }

    [Test]
    public async Task Fixed64Vec3_Magnitude()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vec3.magnitude(fixed64vector3(0, 3, 4))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)5));
    }

    [Test]
    public async Task Fixed64Vec3_SqrMagnitude()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vec3.sqrmagnitude(fixed64vector3(1, 2, 3))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(results[0].TryReadFixed64(out var f64), Is.True);
        Assert.That(f64, Is.EqualTo((Fixed64)14)); // 1+4+9=14
    }

    [Test]
    public async Task Fixed64Vec3_Max()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync(
            "return fixed64vec3.max(fixed64vector3(1, 5, 3), fixed64vector3(4, 2, 6))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(4, 5, 6)));
    }

    [Test]
    public async Task Fixed64Vec3_Min()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync(
            "return fixed64vec3.min(fixed64vector3(1, 5, 3), fixed64vector3(4, 2, 6))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(1, 2, 3)));
    }

    [Test]
    public async Task Fixed64Vec3_Lerp()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync(
            "return fixed64vec3.lerp(fixed64vector3(0, 0, 0), fixed64vector3(10, 10, 10), fixed64(0.5))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(5, 5, 5)));
    }

    [Test]
    public async Task Fixed64Vec3_Abs()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return fixed64vec3.abs(fixed64vector3(-1, 2, -3))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(results[0].TryReadFixed64Vector3(out var vec), Is.True);
        Assert.That(vec, Is.EqualTo(new Vector3d(1, 2, 3)));
    }

    [Test]
    public async Task TypeFunction_ReturnsCorrectStrings()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return type(fixed64(1))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].TryReadString(out var s), Is.True);
        Assert.That(s, Is.EqualTo("fixed64"));
    }

    [Test]
    public async Task TypeFunction_ReturnsFixed64Vector3()
    {
        using var state = CreateState();
        var results = await state.DoStringAsync("return type(fixed64vector3(0, 0, 0))");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].TryReadString(out var s), Is.True);
        Assert.That(s, Is.EqualTo("fixed64vector3"));
    }
}
