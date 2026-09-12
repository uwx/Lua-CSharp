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
    LuaStack? stack;

    public LuaState? Thread { get; }
    public bool IsClosed { get; private set; }
    public int RegisterIndex { get; private set; }

    UpValue(LuaState? state, LuaStack? stack)
    {
        Thread = state;
        this.stack = stack;
    }

    public static UpValue Open(LuaState state, int registerIndex)
    {
        return new(state, state.Stack) { RegisterIndex = registerIndex };
    }

    public static UpValue Closed(LuaValue value)
    {
        return new(null, null) { IsClosed = true, value = value };
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
            // Drop the stack reference: a closed upvalue must not keep the (possibly
            // recycled) stack object alive.
            stack = null;
        }

        IsClosed = true;
    }
}
