using System.Runtime.CompilerServices;

namespace Lua.Runtime;

public sealed class UpValue
{
    LuaValue value;

    /// <summary>
    /// Cached stack owner of an open upvalue. Reading through <see cref="Thread"/> would
    /// dereference the state's current <c>ThreadCoreData</c> on every access, and upvalue
    /// reads are on the hot path of every closure that captures a local (module-level
    /// locals in particular). Null once the upvalue is closed.
    /// </summary>
    LuaStack? stack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<object?, LuaStack?>(ref Unsafe.AsRef(in value.referenceValue));
    }

    public bool IsClosed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => value.Type != LuaValueType.UpValue;
    }

    public int RegisterIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)value.integer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    UpValue(LuaStack? stack, int registerIndex)
    {
        value = new LuaValue(LuaValueType.UpValue, registerIndex, stack);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    UpValue(LuaValue value)
    {
        this.value = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UpValue Open(LuaState state, int registerIndex)
    {
        return new(state.Stack, registerIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UpValue Closed(LuaValue value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue GetValue()
    {
        if (IsClosed)
        {
            return value;
        }

        return stack!.UnsafeGet(RegisterIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly LuaValue GetValueRef()
    {
        if (IsClosed)
        {
            return ref value;
        }

        return ref stack!.UnsafeGet(RegisterIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetValue(LuaValue value)
    {
        if (IsClosed)
        {
            this.value = value;
            return;
        }

        stack!.UnsafeGet(RegisterIndex) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Close()
    {
        if (!IsClosed)
        {
            value = stack!.UnsafeGet(RegisterIndex);
        }
    }
}

/// <summary>
/// One upvalue of a <see cref="LuaClosure"/>: either a value copied at closure creation
/// (the compiler proved the captured local is never reassigned, see
/// <see cref="CodeAnalysis.UpValueDesc.ByValue"/>) or a reference to a shared
/// <see cref="UpValue"/> cell. By-value captures cost no allocation and no indirection.
/// </summary>
public struct UpValueSlot
{
    LuaValue value;

    /// <summary>The shared cell</summary>
    private readonly UpValue? Cell
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<object?, UpValue?>(ref Unsafe.AsRef(in value.referenceValue));
    }

    public static UpValueSlot Inline(LuaValue value)
    {
        return new() { value = value };
    }

    public static UpValueSlot FromCell(UpValue cell)
    {
        return new() { value = new LuaValue(LuaValueType.UpValueCell, cell) };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly LuaValue GetValue()
    {
        return value.Type != LuaValueType.UpValueCell ? value : Cell!.GetValue();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref readonly LuaValue GetValueRef(ref UpValueSlot slot)
    {
        if (slot.value.Type != LuaValueType.UpValueCell)
        {
            return ref slot.value;
        }

        return ref slot.Cell!.GetValueRef();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetValue(LuaValue value)
    {
        if (this.value.Type != LuaValueType.UpValueCell)
        {
            this.value = value;
            return;
        }

        Cell!.SetValue(value);
    }

    /// <summary>
    /// Gives a by-value slot a cell of its own (holding the same value) so it has an
    /// identity that can be shared or compared, as <c>debug.upvalueid</c>/<c>upvaluejoin</c>
    /// need. No-op for slots that already have one.
    /// </summary>
    internal UpValue EnsureCell()
    {
        if (value.Type != LuaValueType.UpValueCell)
        {
            var cell = UpValue.Closed(value);
            value = new LuaValue(LuaValueType.UpValueCell, 0, cell);
            return cell;
        }

        return Cell!;
    }
}
