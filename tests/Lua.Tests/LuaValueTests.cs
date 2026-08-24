using FixedMathSharp;
using NFMWorldLibrary.FixedMath;

namespace Lua.Tests;

public class LuaValueTests
{
    [Test]
    public void TryRead_LuaValue_ReturnsOriginalValue()
    {
        LuaValue value = "hello";

        var success = value.TryRead<LuaValue>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(value));
    }

    [Test]
    public void TryRead_LuaValue_SucceedsForNil()
    {
        var success = LuaValue.Nil.TryRead<LuaValue>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(LuaValue.Nil));
    }

    [Test]
    public void Read_LuaValue_ReturnsOriginalValue()
    {
        LuaValue value = 42;

        var result = value.Read<LuaValue>();

        Assert.That(result, Is.EqualTo(value));
    }

    [Test]
    public void TryRead_LuaTable_ReturnsOriginalTable()
    {
        var table = new LuaTable();
        LuaValue value = table;

        var success = value.TryRead<LuaTable>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.SameAs(table));
    }

    [Test]
    public void FromObject_ConvertsUIntToInteger()
    {
        object value = (uint)42;

        var result = LuaValue.FromObject(value);

        Assert.That(result.Type, Is.EqualTo(LuaValueType.Integer));
        Assert.That(result.Read<uint>(), Is.EqualTo((uint)42));
    }

    [Test]
    public void FromObject_ConvertsULongToNumber()
    {
        object value = (ulong)42;

        var result = LuaValue.FromObject(value);

        Assert.That(result.Type, Is.EqualTo(LuaValueType.Number));
        Assert.That(result.Read<ulong>(), Is.EqualTo((ulong)42));
    }

    [Test]
    public void TryGetLuaValueType_ReturnsNumberForUnsignedIntegers()
    {
        Assert.That(LuaValue.TryGetLuaValueType(typeof(uint), out var uintType), Is.True);
        Assert.That(uintType, Is.EqualTo(LuaValueType.Number));

        Assert.That(LuaValue.TryGetLuaValueType(typeof(ulong), out var ulongType), Is.True);
        Assert.That(ulongType, Is.EqualTo(LuaValueType.Number));
    }

    // ---- Fixed64 tests ----

    [Test]
    public void Constructor_Fixed64_SetsCorrectType()
    {
        var f64 = (Fixed64)3.5;

        LuaValue value = new(f64);

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64));
    }

    [Test]
    public void TryRead_Fixed64_FromFixed64()
    {
        var f64 = (Fixed64)42;
        LuaValue value = f64;

        var success = value.TryRead<Fixed64>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(f64));
    }

    [Test]
    public void TryRead_Fixed64_FromNumber()
    {
        LuaValue value = 3.5;

        var success = value.TryRead<Fixed64>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo((Fixed64)3.5));
    }

    [Test]
    public void TryRead_Double_FromFixed64()
    {
        var f64 = (Fixed64)3.5;
        LuaValue value = f64;

        var success = value.TryRead<double>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo((double)f64).Within(0.001));
    }

    [Test]
    public void TryRead_Fixed64_FromStringFails()
    {
        LuaValue value = "3.5";

        var success = value.TryRead<Fixed64>(out _);

        Assert.That(success, Is.False);
    }

    [Test]
    public void TryReadFixed64_InternalMethod()
    {
        LuaValue value = (Fixed64)10;
        var f64 = (Fixed64)10;

        var success = value.TryReadFixed64(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(f64));
    }

    [Test]
    public void TryReadFixed64_ConvertsNumber()
    {
        LuaValue value = 7.5;

        var success = value.TryReadFixed64(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo((Fixed64)7.5));
    }

    [Test]
    public void TryReadDouble_FromFixed64_Coerces()
    {
        var f64 = (Fixed64)2.5;
        LuaValue value = f64;

        var success = value.TryReadDouble(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(2.5).Within(0.001));
    }

    [Test]
    public void FromObject_Fixed64()
    {
        var f64 = (Fixed64)99;

        var result = LuaValue.FromObject((object)f64);

        Assert.That(result.Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(result.Read<Fixed64>(), Is.EqualTo(f64));
    }

    [Test]
    public void GetHashCode_SameFixed64_SameHash()
    {
        LuaValue a = (Fixed64)5;
        LuaValue b = (Fixed64)5;

        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void Equals_Fixed64_SameValue()
    {
        LuaValue a = (Fixed64)3;
        LuaValue b = (Fixed64)3;

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a == b, Is.True);
    }

    [Test]
    public void Equals_Fixed64_DifferentValue()
    {
        LuaValue a = (Fixed64)3;
        LuaValue b = (Fixed64)4;

        Assert.That(a.Equals(b), Is.False);
        Assert.That(a != b, Is.True);
    }

    [Test]
    public void ToString_Fixed64()
    {
        LuaValue value = (Fixed64)3.5;

        var str = value.ToString();

        Assert.That(str, Is.Not.Empty);
        Assert.That(str, Does.Contain("3"));
    }

    [Test]
    public void ImplicitOperator_Fixed64_To_LuaValue()
    {
        var f64 = (Fixed64)10.5;
        LuaValue value = f64; // implicit op

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64));
        Assert.That(value.TryReadFixed64(out var result), Is.True);
        Assert.That(result, Is.EqualTo(f64));
    }

    [Test]
    public void TryGetLuaValueType_ReturnsFixed64()
    {
        Assert.That(LuaValue.TryGetLuaValueType(typeof(Fixed64), out var type), Is.True);
        Assert.That(type, Is.EqualTo(LuaValueType.Fixed64));
    }

    // ---- Fixed64Vector3 tests ----

    [Test]
    public void Constructor_Vector3d_SetsCorrectType()
    {
        var vec = new Vector3d(1, 2, 3);

        LuaValue value = new(vec);

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
    }

    [Test]
    public void TryRead_Vector3d_FromFixed64Vector3()
    {
        var vec = new Vector3d(1, 2, 3);
        LuaValue value = vec;

        var success = value.TryRead<Vector3d>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(vec));
    }

    [Test]
    public void TryRead_Vector3d_FromNonVector3d_Fails()
    {
        LuaValue value = 42;

        var success = value.TryRead<Vector3d>(out _);

        Assert.That(success, Is.False);
    }

    [Test]
    public void TryReadFixed64Vector3_InternalMethod()
    {
        var vec = new Vector3d(4, 5, 6);
        LuaValue value = vec;

        var success = value.TryReadFixed64Vector3(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(vec));
    }

    [Test]
    public void FromObject_Vector3d()
    {
        var vec = new Vector3d(7, 8, 9);

        var result = LuaValue.FromObject((object)vec);

        Assert.That(result.Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(result.Read<Vector3d>(), Is.EqualTo(vec));
    }

    [Test]
    public void GetHashCode_SameVector3d_SameHash()
    {
        LuaValue a = new Vector3d(1, 2, 3);
        LuaValue b = new Vector3d(1, 2, 3);

        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void Equals_Vector3d_SameValue()
    {
        LuaValue a = new Vector3d(1, 2, 3);
        LuaValue b = new Vector3d(1, 2, 3);

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a == b, Is.True);
    }

    [Test]
    public void Equals_Vector3d_DifferentValue()
    {
        LuaValue a = new Vector3d(1, 2, 3);
        LuaValue b = new Vector3d(4, 5, 6);

        Assert.That(a.Equals(b), Is.False);
        Assert.That(a != b, Is.True);
    }

    [Test]
    public void ToString_Fixed64Vector3()
    {
        LuaValue value = new Vector3d(1, 2, 3);

        var str = value.ToString();

        Assert.That(str, Is.Not.Empty);
        Assert.That(str, Does.Contain("1"));
    }

    [Test]
    public void ImplicitOperator_Vector3d_To_LuaValue()
    {
        var vec = new Vector3d(10, 20, 30);
        LuaValue value = vec; // implicit op

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64Vector3));
        Assert.That(value.TryReadFixed64Vector3(out var result), Is.True);
        Assert.That(result, Is.EqualTo(vec));
    }

    [Test]
    public void TryGetLuaValueType_ReturnsVector3d()
    {
        Assert.That(LuaValue.TryGetLuaValueType(typeof(Vector3d), out var type), Is.True);
        Assert.That(type, Is.EqualTo(LuaValueType.Fixed64Vector3));
    }

    [Test]
    public void TypeToString_ReturnsCorrectStrings()
    {
        Assert.That(LuaValue.ToString(LuaValueType.Fixed64), Is.EqualTo("fixed64"));
        Assert.That(LuaValue.ToString(LuaValueType.Fixed64Vector3), Is.EqualTo("fixed64vector3"));
    }

    [Test]
    public void EqualsForDict_Fixed64_SameValue()
    {
        LuaValue a = (Fixed64)5;
        LuaValue b = (Fixed64)5;

        Assert.That(a.EqualsForDict(b), Is.True);
    }

    [Test]
    public void EqualsForDict_Fixed64Vector3_SameValue()
    {
        LuaValue a = new Vector3d(1, 2, 3);
        LuaValue b = new Vector3d(1, 2, 3);

        Assert.That(a.EqualsForDict(b), Is.True);
    }

    // ---- f64AngleSingle tests ----

    [Test]
    public void Constructor_f64AngleSingle_SetsCorrectType()
    {
        var angle = f64AngleSingle.FromDegrees((Fixed64)90);

        LuaValue value = new(angle);

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64Angle));
    }

    [Test]
    public void TryRead_f64AngleSingle_FromFixed64Angle()
    {
        var angle = f64AngleSingle.FromDegrees((Fixed64)45);
        LuaValue value = angle;

        var success = value.TryRead<f64AngleSingle>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(angle));
    }

    [Test]
    public void TryReadFixed64Angle_InternalMethod()
    {
        var angle = f64AngleSingle.FromDegrees((Fixed64)90);
        LuaValue value = angle;

        var success = value.TryReadFixed64Angle(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(angle));
    }

    [Test]
    public void TryReadFixed64Angle_FromNonAngle_Fails()
    {
        LuaValue value = 42;

        var success = value.TryReadFixed64Angle(out _);

        Assert.That(success, Is.False);
    }

    [Test]
    public void FromObject_f64AngleSingle()
    {
        var angle = f64AngleSingle.FromDegrees((Fixed64)180);

        var result = LuaValue.FromObject((object)angle);

        Assert.That(result.Type, Is.EqualTo(LuaValueType.Fixed64Angle));
        Assert.That(result.Read<f64AngleSingle>(), Is.EqualTo(angle));
    }

    [Test]
    public void GetHashCode_Samef64AngleSingle_SameHash()
    {
        LuaValue a = f64AngleSingle.FromDegrees((Fixed64)90);
        LuaValue b = f64AngleSingle.FromDegrees((Fixed64)90);

        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void Equals_f64AngleSingle_SameValue()
    {
        LuaValue a = f64AngleSingle.FromDegrees((Fixed64)180);
        LuaValue b = f64AngleSingle.FromDegrees((Fixed64)180);

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a == b, Is.True);
    }

    [Test]
    public void ImplicitOperator_f64AngleSingle_To_LuaValue()
    {
        var angle = f64AngleSingle.FromDegrees((Fixed64)30);
        LuaValue value = angle;

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64Angle));
        Assert.That(value.TryReadFixed64Angle(out var result), Is.True);
        Assert.That(result, Is.EqualTo(angle));
    }

    [Test]
    public void ToString_f64AngleSingle()
    {
        LuaValue value = f64AngleSingle.FromDegrees((Fixed64)45);

        var str = value.ToString();

        Assert.That(str, Is.Not.Empty);
    }

    [Test]
    public void TryGetLuaValueType_Returnsf64AngleSingle()
    {
        Assert.That(LuaValue.TryGetLuaValueType(typeof(f64AngleSingle), out var type), Is.True);
        Assert.That(type, Is.EqualTo(LuaValueType.Fixed64Angle));
    }

    // ---- f64Euler tests ----

    [Test]
    public void Constructor_f64Euler_SetsCorrectType()
    {
        var euler = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)0),
            f64AngleSingle.FromDegrees((Fixed64)90),
            f64AngleSingle.FromDegrees((Fixed64)0));

        LuaValue value = new(euler);

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64Euler));
    }

    [Test]
    public void TryRead_f64Euler_FromFixed64Euler()
    {
        var euler = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)45),
            f64AngleSingle.FromDegrees((Fixed64)0),
            f64AngleSingle.FromDegrees((Fixed64)0));
        LuaValue value = euler;

        var success = value.TryRead<f64Euler>(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(euler));
    }

    [Test]
    public void TryReadFixed64Euler_InternalMethod()
    {
        var euler = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)10),
            f64AngleSingle.FromDegrees((Fixed64)20),
            f64AngleSingle.FromDegrees((Fixed64)30));
        LuaValue value = euler;

        var success = value.TryReadFixed64Euler(out var result);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(euler));
    }

    [Test]
    public void FromObject_f64Euler()
    {
        var euler = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)1),
            f64AngleSingle.FromDegrees((Fixed64)2),
            f64AngleSingle.FromDegrees((Fixed64)3));

        var result = LuaValue.FromObject((object)euler);

        Assert.That(result.Type, Is.EqualTo(LuaValueType.Fixed64Euler));
        Assert.That(result.Read<f64Euler>(), Is.EqualTo(euler));
    }

    [Test]
    public void GetHashCode_Samef64Euler_SameHash()
    {
        LuaValue a = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)1),
            f64AngleSingle.FromDegrees((Fixed64)2),
            f64AngleSingle.FromDegrees((Fixed64)3));
        LuaValue b = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)1),
            f64AngleSingle.FromDegrees((Fixed64)2),
            f64AngleSingle.FromDegrees((Fixed64)3));

        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void Equals_f64Euler_SameValue()
    {
        LuaValue a = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)1),
            f64AngleSingle.FromDegrees((Fixed64)2),
            f64AngleSingle.FromDegrees((Fixed64)3));
        LuaValue b = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)1),
            f64AngleSingle.FromDegrees((Fixed64)2),
            f64AngleSingle.FromDegrees((Fixed64)3));

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a == b, Is.True);
    }

    [Test]
    public void ImplicitOperator_f64Euler_To_LuaValue()
    {
        var euler = new f64Euler(
            f64AngleSingle.FromDegrees((Fixed64)10),
            f64AngleSingle.FromDegrees((Fixed64)20),
            f64AngleSingle.FromDegrees((Fixed64)30));
        LuaValue value = euler;

        Assert.That(value.Type, Is.EqualTo(LuaValueType.Fixed64Euler));
        Assert.That(value.TryReadFixed64Euler(out var result), Is.True);
        Assert.That(result, Is.EqualTo(euler));
    }

    [Test]
    public void TryGetLuaValueType_Returnsf64Euler()
    {
        Assert.That(LuaValue.TryGetLuaValueType(typeof(f64Euler), out var type), Is.True);
        Assert.That(type, Is.EqualTo(LuaValueType.Fixed64Euler));
    }

    [Test]
    public void TypeToString_ReturnsCorrectAngleStrings()
    {
        Assert.That(LuaValue.ToString(LuaValueType.Fixed64Angle), Is.EqualTo("f64angle"));
        Assert.That(LuaValue.ToString(LuaValueType.Fixed64Euler), Is.EqualTo("f64euler"));
    }
}
