using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FixedMathSharp;
using Lua.Internal;
using Lua.Runtime;
using NFMWorldLibrary.FixedMath;

namespace Lua;

public enum LuaValueType : byte
{
    Nil,
    Boolean,
    String,
    Number,
    Integer,
    Function,
    Thread,
    LightUserData,
    UserData,
    Table,

    // Added NFMW types
    Fixed64,
    Fixed64Vector3,
    Fixed64Angle,
    Fixed64Euler,

    // this is like userdata but the type is wrapped so you don't need to make your type implement ILuaUserData,
    // useful for e.g. making userdatas out of standard library objects
    UserData2,

    // Ugly hacks to save allocation size on UpValue and UpValueSlot
    // Internal type for open upvalues. Not to be used directly except by UpValue.
    // integer = registerIndex, referenceValue = LuaStack
    UpValue,

    // Internal type for UpValueSlot containing an UpValue
    // referenceValue = UpValue
    UpValueCell
}

internal class UserDataObject : ILuaUserData
{
    public object? Value { get; set; }
    public LuaTable? Metatable { get; set; }
}

[InlineArray(24)]
internal struct ValueUnion
{
    private byte _b;
}

[StructLayout(LayoutKind.Auto)]
public readonly struct LuaValue : IEquatable<LuaValue>
{
    public static LuaValue Nil
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new LuaValue(LuaValueType.Nil, null);
    }

    public readonly LuaValueType Type;
    internal readonly object? referenceValue;
    internal readonly ValueUnion valueUnion;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double ReadAsDouble() => MemoryMarshal.Read<double>(valueUnion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long ReadAsInt64() => MemoryMarshal.Read<long>(valueUnion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double ReadAsNumber() => Type == LuaValueType.Integer ? ReadAsInt64() : ReadAsDouble();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ReadAsBool() => ReadAsDouble() != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Fixed64 ReadAsFixed64() => MemoryMarshal.Read<Fixed64>(valueUnion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector3d ReadAsF64Vector3() => MemoryMarshal.Read<Vector3d>(valueUnion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal f64AngleSingle ReadAsF64Angle() => MemoryMarshal.Read<f64AngleSingle>(valueUnion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal f64Euler ReadAsF64Euler() => MemoryMarshal.Read<f64Euler>(valueUnion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal UserDataObject ReadAsUserData2() => Unsafe.As<UserDataObject>(referenceValue!);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuaValue(LuaValueType type, double value, object? referenceValue)
    {
        Type = type;
        this.referenceValue = referenceValue;
        Unsafe.SkipInit(out valueUnion);
        MemoryMarshal.Write(valueUnion, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuaValue(LuaValueType type, long value, object? referenceValue)
    {
        Type = type;
        this.referenceValue = referenceValue;
        Unsafe.SkipInit(out valueUnion);
        MemoryMarshal.Write(valueUnion, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuaValue(LuaValueType type, object? referenceValue)
    {
        Type = type;
        this.referenceValue = referenceValue;
        Unsafe.SkipInit(out valueUnion);
    }

    private bool TryReadNumberAs<T>([MaybeNullWhen(false)] out T result)
    {
        double value = ReadAsDouble();

        if (typeof(T) == typeof(float))
        {
            result = (T) (object) (float) value;
            return true;
        }
        else if (typeof(T) == typeof(byte))
        {
            result = (T) (object) (byte) value;
            return MathEx.IsInteger(value);
        }
        else if (typeof(T) == typeof(sbyte))
        {
            result = (T) (object) (sbyte) value;
            return MathEx.IsInteger(value);
        }
        else if (typeof(T) == typeof(short))
        {
            result = (T) (object) (short) value;
            return MathEx.IsInteger(value);
        }
        else if (typeof(T) == typeof(ushort))
        {
            result = (T) (object) (ushort) value;
            return MathEx.IsInteger(value);
        }
        else if (typeof(T) == typeof(int))
        {
            result = (T) (object) (int) value;
            return MathEx.IsInteger(value);
        }
        else if (typeof(T) == typeof(long))
        {
            result = (T) (object) (long) value;
            return MathEx.IsInteger(value);
        }
        else if (typeof(T) == typeof(uint))
        {
            if (MathEx.IsInteger(value))
            {
                result = (T) (object) checked((uint) value); // TODO: remove checked? 
                return true;
            }
        }
        else if (typeof(T) == typeof(ulong))
        {
            if (MathEx.IsInteger(value))
            {
                result = (T) (object) checked((ulong) value); // TODO: remove checked?
                return true;
            }
        }
        else if (typeof(T) == typeof(Fixed64))
        {
            // Number → Fixed64 conversion (lossy by design)
            result = (T) (object) (Fixed64) value;
            return true;
        }
        else if (value is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadIntegerAs<T>([MaybeNullWhen(false)] out T result)
    {
        long value = ReadAsInt64();

        if (typeof(T) == typeof(double))
        {
            result = (T) (object) (double) value;
            return true;
        }
        else if (typeof(T) == typeof(float))
        {
            result = (T) (object) (float) value;
            return true;
        }
        else if (typeof(T) == typeof(byte))
        {
            result = (T) (object) (byte) value;
            return true;
        }
        else if (typeof(T) == typeof(sbyte))
        {
            result = (T) (object) (sbyte) value;
            return true;
        }
        else if (typeof(T) == typeof(short))
        {
            result = (T) (object) (short) value;
            return true;
        }
        else if (typeof(T) == typeof(ushort))
        {
            result = (T) (object) (ushort) value;
            return true;
        }
        else if (typeof(T) == typeof(int))
        {
            result = (T) (object) (int) value;
            return true;
        }
        else if (typeof(T) == typeof(uint))
        {
            result = (T) (object) (uint) value;
            return true;
        }
        else if (typeof(T) == typeof(ulong))
        {
            result = (T) (object) (ulong) value;
            return true;
        }
        else if (typeof(T) == typeof(Fixed64))
        {
            result = (T) (object) (Fixed64) value;
            return true;
        }
        else if (value is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadBoolAs<T>([MaybeNullWhen(false)] out T result)
    {
        double value = MemoryMarshal.Read<double>(valueUnion);

        if (typeof(T) == typeof(bool))
        {
            result = (T) (object) (value != 0);
            return true;
        }
        else if (typeof(T) == typeof(object))
        {
            result = (T) (object) value;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadStringAs<T>([MaybeNullWhen(false)] out T result)
    {
        if (typeof(T) == typeof(double))
        {
            if (TryParseToDouble(out double exact))
            {
                result = (T) (object) exact;
                return true;
            }
        }
        else if (ReadAsString() is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadFunctionAs<T>([MaybeNullWhen(false)] out T result)
    {
        var value = Unsafe.As<LuaFunction>(referenceValue!);
        if (value is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadThreadAs<T>([MaybeNullWhen(false)] out T result)
    {
        var value = Unsafe.As<LuaState>(referenceValue!);
        if (value is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadLightUserData<T>([MaybeNullWhen(false)] out T result)
    {
        if (referenceValue is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadUserDataAs<T>([MaybeNullWhen(false)] out T result)
    {
        var value = Unsafe.As<ILuaUserData>(referenceValue!);
        if (value is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadUserData2As<T>([MaybeNullWhen(false)] out T result)
    {
        if (ReadAsUserData2().Value is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadTableAs<T>([MaybeNullWhen(false)] out T result)
    {
        var value = Unsafe.As<LuaTable>(referenceValue!);
        if (value is T exact)
        {
            result = exact;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadFixed64As<T>([MaybeNullWhen(false)] out T result)
    {
        var value = ReadAsFixed64();

        if (typeof(T) == typeof(Fixed64))
        {
            result = (T) (object) value;
            return true;
        }
        else if (typeof(T) == typeof(double))
        {
            result = (T) (object) (double) value;
            return true;
        }
        else if (typeof(T) == typeof(object))
        {
            result = (T) (object) value;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadF64Vector3As<T>([MaybeNullWhen(false)] out T result)
    {
        var value = ReadAsF64Vector3();

        if (typeof(T) == typeof(Vector3d))
        {
            result = (T) (object) value;
            return true;
        }
        else if (typeof(T) == typeof(object))
        {
            result = (T) (object) value;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadF64AngleAs<T>([MaybeNullWhen(false)] out T result)
    {
        var value = ReadAsF64Angle();

        if (typeof(T) == typeof(f64AngleSingle))
        {
            result = (T) (object) value;
            return true;
        }
        else if (typeof(T) == typeof(object))
        {
            result = (T) (object) value;
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReadF64EulerAs<T>([MaybeNullWhen(false)] out T result)
    {
        var value = ReadAsF64Euler();

        if (typeof(T) == typeof(f64Euler))
        {
            result = (T) (object) value;
            return true;
        }
        else if (typeof(T) == typeof(object))
        {
            result = (T) (object) value;
            return true;
        }

        result = default;
        return false;
    }

    public bool TryRead<T>([MaybeNullWhen(false)] out T result)
    {
        if (typeof(T) == typeof(LuaValue))
        {
            result = (T) (object) this;
            return true;
        }

        return Type switch
        {
            LuaValueType.Number => TryReadNumberAs(out result),
            LuaValueType.Integer => TryReadIntegerAs(out result),
            LuaValueType.Boolean => TryReadBoolAs(out result),
            LuaValueType.String => TryReadStringAs(out result),
            LuaValueType.Function => TryReadFunctionAs(out result),
            LuaValueType.Thread => TryReadThreadAs(out result),
            LuaValueType.LightUserData => TryReadLightUserData(out result),
            LuaValueType.UserData => TryReadUserDataAs(out result),
            LuaValueType.UserData2 => TryReadUserData2As(out result),
            LuaValueType.Table => TryReadTableAs(out result),
            LuaValueType.Fixed64 => TryReadFixed64As(out result),
            LuaValueType.Fixed64Vector3 => TryReadF64Vector3As(out result),
            LuaValueType.Fixed64Angle => TryReadF64AngleAs(out result),
            LuaValueType.Fixed64Euler => TryReadF64EulerAs(out result),
            _ => ReturnDefault(out result),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ReturnDefault<T>([MaybeNullWhen(false)] out T result)
    {
        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadBool(out bool result)
    {
        if (Type == LuaValueType.Boolean)
        {
            result = ReadAsBool();
            return true;
        }

        result = false;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadNumber(out double result)
    {
        switch (Type)
        {
            case LuaValueType.Number:
                result = ReadAsDouble();
                return true;

            case LuaValueType.Integer:
                result = ReadAsInt64();
                return true;

            default:
                result = 0;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadInteger(out long result)
    {
        switch (Type)
        {
            case LuaValueType.Integer:
                result = ReadAsInt64();
                return true;

            case LuaValueType.Number:
                double value = ReadAsDouble();
                if (MathEx.IsInteger(value))
                {
                    result = (long) value;
                    return true;
                }
                goto default;

            default:
                result = 0;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadTable([MaybeNullWhen(false)] out LuaTable result)
    {
        if (Type == LuaValueType.Table)
        {
            result = Unsafe.As<LuaTable>(referenceValue!);
            return true;
        }

        result = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadFunction([MaybeNullWhen(false)] out LuaFunction result)
    {
        if (Type == LuaValueType.Function)
        {
            result = Unsafe.As<LuaFunction>(referenceValue!);
            return true;
        }

        result = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadString([MaybeNullWhen(false)] out string result)
    {
        if (Type == LuaValueType.String)
        {
            result = ReadAsString();
            return true;
        }

        result = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadFixed64(out Fixed64 result)
    {
        switch (Type)
        {
            case LuaValueType.Fixed64:
                result = ReadAsFixed64();
                return true;

            // Convert Number → Fixed64 (lossy by design)
            case LuaValueType.Number:
                result = (Fixed64) ReadAsDouble();
                return true;

            // Convert Integer → Fixed64
            case LuaValueType.Integer:
                result = (Fixed64) ReadAsInt64();
                return true;

            default:
                result = default;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadFixed64Vector3(out Vector3d result)
    {
        if (Type == LuaValueType.Fixed64Vector3)
        {
            result = ReadAsF64Vector3();
            return true;
        }

        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadFixed64Angle(out f64AngleSingle result)
    {
        if (Type == LuaValueType.Fixed64Angle)
        {
            result = ReadAsF64Angle();
            return true;
        }

        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadFixed64Euler(out f64Euler result)
    {
        if (Type == LuaValueType.Fixed64Euler)
        {
            result = ReadAsF64Euler();
            return true;
        }

        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadDouble(out double result)
    {
        switch (Type)
        {
            case LuaValueType.Number:
                result = ReadAsDouble();
                return true;

            case LuaValueType.Integer:
                result = ReadAsInt64();
                return true;

            // Fixed64 → double coercion (lossy)
            case LuaValueType.Fixed64:
                result = (double) ReadAsFixed64();
                return true;

            default:
                return TryParseToDouble(out result);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal string ReadAsString() => Unsafe.As<string>(referenceValue!);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object ReadAsObject() => referenceValue!;

    bool TryParseToDouble(out double result)
    {
        if (Type != LuaValueType.String)
        {
            result = 0;
            return false;
        }

        var str = ReadAsString();
        var span = str.AsSpan().Trim();
        if (span.Length == 0)
        {
            result = 0;
            return false;
        }

        var sign = 1;
        var first = span[0];
        if (first is '+')
        {
            sign = 1;
            span = span[1..];
        }
        else if (first is '-')
        {
            sign = -1;
            span = span[1..];
        }

        if (span.Length > 2 && span[0] is '0' && span[1] is 'x' or 'X')
        {
            return TryParseHexToDouble(span, sign, out result);
        }

        return double.TryParse(
            str,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result
        );
    }

    private static bool TryParseHexToDouble(ReadOnlySpan<char> span, int sign, out double result)
    {
        // TODO: optimize
        try
        {
            var d = HexConverter.ToDouble(span) * sign;
            result = d;
            return true;
        }
        catch (FormatException)
        {
            result = 0;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Read<T>()
    {
        return TryRead<T>(out var result) ? result : ThrowInvalidConversion<T>(Type);
    }

    [return: NotNullIfNotNull(nameof(@default))]
    public T? ReadOrDefault<T>(T? @default = default)
    {
        return TryRead<T>(out T? result) ? result : @default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T UnsafeRead<T>()
    {
        return Type switch
        {
            LuaValueType.Boolean => (T) (object) ReadAsBool(),
            LuaValueType.Number => (T) (object) ReadAsDouble(),
            LuaValueType.Fixed64 => (T) (object) ReadAsFixed64(),
            LuaValueType.Fixed64Vector3 => (T) (object) ReadAsF64Vector3(),
            LuaValueType.Fixed64Angle => (T) (object) ReadAsF64Angle(),
            LuaValueType.Fixed64Euler => (T) (object) ReadAsF64Euler(),
            LuaValueType.String or
                LuaValueType.Thread or
                LuaValueType.Function or
                LuaValueType.Table or
                LuaValueType.LightUserData or
                LuaValueType.UserData => (T) referenceValue!,
            LuaValueType.UserData2 => (T) ReadAsUserData2().Value!, // TODO: this violates nullability
            _ => default!,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ToBoolean()
    {
        return Type switch
        {
            LuaValueType.Boolean => ReadAsBool(),
            LuaValueType.Nil => false,
            _ => true,
        };
    }

    public static LuaValue FromObject<T>(T obj)
    {
        return obj switch
        {
            null => Nil,
            LuaValue luaValue => luaValue,
            bool boolValue => boolValue,
            double doubleValue => doubleValue,
            string stringValue => stringValue,
            LuaFunction luaFunction => luaFunction,
            LuaTable luaTable => luaTable,
            LuaState luaThread => luaThread,
            ILuaUserData userData => FromUserData(userData),
            int intValue => intValue,
            long longValue => longValue,
            uint uintValue => uintValue,
            ulong ulongValue => ulongValue,
            float floatValue => floatValue,
            Fixed64 fixed64Value => fixed64Value,
            Vector3d vec3Value => vec3Value,
            f64AngleSingle angleValue => new LuaValue(angleValue),
            f64Euler eulerValue => new LuaValue(eulerValue),
            _ => NewLightUserData(obj),
        };
    }

    public static LuaValue FromUserData(ILuaUserData? userData)
    {
        return userData is null ? Nil : new(userData);
    }

    public static LuaValue FromUserData(object? userData, LuaTable metatable)
    {
        return userData is null ? Nil : NewUserData(userData, metatable);
    }

    private static LuaValue NewUserData(object? userData, LuaTable metatable)
    {
        UserDataObject value = new()
        {
            Value = userData,
            Metatable = metatable,
        };
        return new LuaValue(LuaValueType.UserData2, value);
    }

    public static LuaValue FromLightUserData(object? userData)
    {
        return userData is null ? Nil : NewLightUserData(userData);
    }

    private static LuaValue NewLightUserData(object? userData)
    {
        return new LuaValue(LuaValueType.LightUserData, userData);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(bool value) : this(LuaValueType.Boolean, value ? 1.0 : 0.0, null)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(int value) : this((long) value)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(double value) : this(LuaValueType.Number, value, null)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(string value) : this(LuaValueType.String, value)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(LuaFunction value) : this(LuaValueType.Function, value)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(LuaTable value) : this(LuaValueType.Table, value)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(LuaState value) : this(LuaValueType.Thread, value)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(ILuaUserData value) : this(LuaValueType.UserData, value)
    {
    }

    // TODO: expose ValueUnion and move these constructors to dedicated project

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(Fixed64 value)
    {
        Type = LuaValueType.Fixed64;
        referenceValue = null;
        Unsafe.SkipInit(out valueUnion);
        MemoryMarshal.Write(valueUnion, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(Vector3d value)
    {
        Type = LuaValueType.Fixed64Vector3;
        referenceValue = null;
        Unsafe.SkipInit(out valueUnion);
        MemoryMarshal.Write(valueUnion, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(f64AngleSingle value)
    {
        Type = LuaValueType.Fixed64Angle;
        referenceValue = null;
        Unsafe.SkipInit(out valueUnion);
        MemoryMarshal.Write(valueUnion, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(f64Euler value)
    {
        Type = LuaValueType.Fixed64Euler;
        referenceValue = null;
        Unsafe.SkipInit(out valueUnion);
        MemoryMarshal.Write(valueUnion, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(long value) : this(LuaValueType.Integer, value, null)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(int value)
    {
        return new((long) value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(long value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(bool value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(double value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(string? value)
    {
        return value != null ? new(value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(bool? value)
    {
        return value != null ? new(value.Value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(double? value)
    {
        return value != null ? new(value.Value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(LuaTable? value)
    {
        return value != null ? new(value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(LuaFunction? value)
    {
        return value != null ? new(value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(LuaState? value)
    {
        return value != null ? new(value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(Fixed64 value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(Vector3d value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(Fixed64? value)
    {
        return value != null ? new(value.Value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(Vector3d? value)
    {
        return value != null ? new(value.Value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(f64AngleSingle value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(f64Euler value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(f64AngleSingle? value)
    {
        return value != null ? new(value.Value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(f64Euler? value)
    {
        return value != null ? new(value.Value) : Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return Type switch
        {
            LuaValueType.Nil => 0,
            LuaValueType.Boolean or LuaValueType.Number => ReadAsDouble().GetHashCode(),
            LuaValueType.Integer => ((double) ReadAsInt64()).GetHashCode(),
            LuaValueType.Fixed64 => ReadAsFixed64().GetHashCode(),
            LuaValueType.Fixed64Vector3 => ReadAsF64Vector3().GetHashCode(),
            LuaValueType.Fixed64Angle => ReadAsF64Angle().GetHashCode(),
            LuaValueType.Fixed64Euler => ReadAsF64Euler().GetHashCode(),
            LuaValueType.String => ReadAsString().GetHashCode(),
            _ => referenceValue!.GetHashCode(),
        };
    }

    private bool SameTypeEquals(in LuaValue other)
    {
        Debug.Assert(Type == other.Type);
        return Type switch
        {
            LuaValueType.Boolean or LuaValueType.Number => other.ReadAsDouble() == ReadAsDouble(),
            LuaValueType.Integer => other.ReadAsInt64() == ReadAsInt64(),
            LuaValueType.Fixed64 => other.ReadAsFixed64() == ReadAsFixed64(),
            LuaValueType.Fixed64Vector3 => other.ReadAsF64Vector3() == ReadAsF64Vector3(),
            LuaValueType.Fixed64Angle => other.ReadAsF64Angle() == ReadAsF64Angle(),
            LuaValueType.Fixed64Euler => other.ReadAsF64Euler() == ReadAsF64Euler(),
            LuaValueType.String => other.ReadAsString() == ReadAsString(),
            _ => other.referenceValue == referenceValue,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool NumberEquals(in LuaValue other)
    {
        // Cross-type numeric equality: Integer(1) == Number(1.0).
        if (Type is LuaValueType.Integer or LuaValueType.Number &&
            other.Type is LuaValueType.Integer or LuaValueType.Number)
        {
            double left = ReadAsNumber();
            double right = other.ReadAsNumber();
            return left == right;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(in LuaValue other)
    {
        if (other.Type == Type)
        {
            return Type == LuaValueType.Nil || SameTypeEquals(other);
        }
        return NumberEquals(other);
    }

    bool IEquatable<LuaValue>.Equals(LuaValue other) => Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EqualsForDict(in LuaValue other)
    {
        if (other.Type == Type)
        {
            // TODO: handle nil?
            return SameTypeEquals(other);
        }
        return NumberEquals(other);
    }

    public override bool Equals(object? obj)
    {
        return obj is LuaValue value1 && Equals(value1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(in LuaValue a, in LuaValue b)
    {
        return a.Equals(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(in LuaValue a, in LuaValue b)
    {
        return !a.Equals(b);
    }

    public override string? ToString() => ToString(null, CultureInfo.InvariantCulture);

    public string? ToString(string? format, IFormatProvider? formatProvider)
    {
        return Type switch
        {
            LuaValueType.Nil => "nil",
            LuaValueType.Boolean => ReadAsBool() ? "true" : "false",
            LuaValueType.String => ReadAsString(),
            LuaValueType.Number => ReadAsDouble().ToString(format, formatProvider),
            LuaValueType.Integer => ReadAsInt64().ToString(format, formatProvider),
            LuaValueType.Fixed64 => ReadAsFixed64().ToString(),
            LuaValueType.Fixed64Vector3 => ReadAsF64Vector3().ToString(),
            LuaValueType.Fixed64Angle => ReadAsF64Angle().ToString(),
            LuaValueType.Fixed64Euler => ReadAsF64Euler().ToString(),
            LuaValueType.Function => $"function: {referenceValue!.GetHashCode()}",
            LuaValueType.Thread => $"thread: {referenceValue!.GetHashCode()}",
            LuaValueType.Table => $"table: {referenceValue!.GetHashCode()}",
            LuaValueType.LightUserData or LuaValueType.UserData => $"userdata: {referenceValue!.GetHashCode()}",
            LuaValueType.UserData2 => $"userdata: {ReadAsUserData2().Value!.GetHashCode()}",
            _ => "",
        };
    }

    public string TypeToString()
    {
        return ToString(Type);
    }

    public static string ToString(LuaValueType type)
    {
        return type switch
        {
            LuaValueType.Nil => "nil",
            LuaValueType.Boolean => "boolean",
            LuaValueType.String => "string",
            LuaValueType.Number => "number",
            LuaValueType.Integer => "number",
            LuaValueType.Fixed64 => "fixed64",
            LuaValueType.Fixed64Vector3 => "fixed64vector3",
            LuaValueType.Fixed64Angle => "f64angle",
            LuaValueType.Fixed64Euler => "f64euler",
            LuaValueType.Function => "function",
            LuaValueType.Thread => "thread",
            LuaValueType.Table => "table",
            LuaValueType.LightUserData => "light userdata",
            LuaValueType.UserData => "userdata",
            LuaValueType.UserData2 => "userdata",
            _ => "",
        };
    }

    public static bool TryGetLuaValueType(Type type, out LuaValueType result)
    {
        if (
            type == typeof(double)
            || type == typeof(float)
            || type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(uint)
            || type == typeof(ulong)
        )
        {
            result = LuaValueType.Number;
            return true;
        }
        else if (type == typeof(bool))
        {
            result = LuaValueType.Boolean;
            return true;
        }
        else if (type == typeof(string))
        {
            result = LuaValueType.String;
            return true;
        }
        else if (type == typeof(Fixed64))
        {
            result = LuaValueType.Fixed64;
            return true;
        }
        else if (type == typeof(Vector3d))
        {
            result = LuaValueType.Fixed64Vector3;
            return true;
        }
        else if (type == typeof(f64AngleSingle))
        {
            result = LuaValueType.Fixed64Angle;
            return true;
        }
        else if (type == typeof(f64Euler))
        {
            result = LuaValueType.Fixed64Euler;
            return true;
        }
        else if (type.IsAssignableTo(typeof(LuaFunction)))
        {
            result = LuaValueType.Function;
            return true;
        }
        else if (type == typeof(LuaTable))
        {
            result = LuaValueType.Table;
            return true;
        }
        else if (type == typeof(LuaState))
        {
            result = LuaValueType.Thread;
            return true;
        }
        else if (type.IsAssignableTo(typeof(ILuaUserData)))
        {
            result = LuaValueType.UserData;
            return true;
        }

        result = default;
        return false;
    }

    internal ValueTask<int> CallToStringAsync(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (this.TryGetMetamethod(context.GlobalState, Metamethods.ToString, out var metamethod))
        {
            var stack = context.State.Stack;
            stack.Push(metamethod);
            stack.Push(this);
            return LuaVirtualMachine.Call(
                context.State,
                stack.Count - 2,
                stack.Count - 2,
                cancellationToken
            );
        }
        else
        {
            context.State.Stack.Push(ToString());
            return default;
        }
    }

    [DoesNotReturn]
    static T ThrowInvalidConversion<T>(LuaValueType type)
    {
        throw new InvalidOperationException(
            $"Cannot convert LuaValueType.{type} to {typeof(T).FullName}."
        );
    }
}
