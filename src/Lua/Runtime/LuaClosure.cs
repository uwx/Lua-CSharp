using System.Runtime.CompilerServices;
using Lua.CodeAnalysis;
using Lua.Internal;

namespace Lua.Runtime;

public sealed class LuaClosure : LuaFunction
{
    FastListCore<UpValueSlot> upValues;

    public LuaClosure(LuaState state, Prototype proto, LuaTable? environment = null)
        : base(
            proto.ChunkName,
            static (context, ct) => LuaVirtualMachine.ExecuteClosureAsync(context.State, ct)
        )
    {
        Proto = proto;
        if (environment != null)
        {
            upValues = new FastListCore<UpValueSlot>(1);
            upValues.Add(UpValueSlot.FromCell(UpValue.Closed(environment)));
            return;
        }

        if (state.CallStackFrameCount == 0)
        {
            upValues = new FastListCore<UpValueSlot>(1);
            upValues.Add(UpValueSlot.FromCell(state.GlobalState.EnvUpValue));
            return;
        }

        var baseIndex = state.GetCallStackFrames()[^1].Base;
        var upValueCount = proto.UpValues.Length;

        // Size the list exactly: the default Add path would allocate an 8-slot array even
        // for a closure that captures a single local.
        upValues = new FastListCore<UpValueSlot>(upValueCount);

        // add upvalues
        for (var i = 0; i < upValueCount; i++)
        {
            var description = proto.UpValues[i];
            var upValue = GetUpValueFromDescription(
                state.GlobalState,
                state,
                description,
                baseIndex
            );
            upValues.Add(upValue);
        }
    }

    public Prototype Proto { get; }

    public ReadOnlySpan<UpValueSlot> UpValues => upValues.AsSpan();

    internal Span<UpValueSlot> GetUpValuesSpan()
    {
        return upValues.AsSpan();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuaValue GetUpValue(int index)
    {
        return upValues[index].GetValue();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly LuaValue GetUpValueRef(int index)
    {
        return ref UpValueSlot.GetValueRef(ref upValues[index]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetUpValue(int index, LuaValue value)
    {
        upValues[index].SetValue(value);
    }

    internal void SetEnvironment(LuaValue environment)
    {
        if (Proto.UpValues.Length == 0 || Proto.UpValues[0].Name != "_ENV")
        {
            return;
        }

        // A cell rather than an inline value: nested closures capture `_ENV` through this
        // slot and must keep seeing the parent's environment if it is reassigned later.
        var slot = UpValueSlot.FromCell(UpValue.Closed(environment));
        if (upValues.Length == 0)
        {
            upValues.Add(slot);
            return;
        }

        upValues[0] = slot;
    }

    static UpValueSlot GetUpValueFromDescription(
        LuaGlobalState globalState,
        LuaState state,
        UpValueDesc description,
        int baseIndex = 0
    )
    {
        if (description.IsLocal)
        {
            if (description is { Index: 0, Name: "_ENV" })
            {
                return UpValueSlot.FromCell(globalState.EnvUpValue);
            }

            var registerIndex = baseIndex + description.Index;
            if (description.ByValue)
            {
                // Never reassigned after this point: snapshot it instead of opening a cell.
                return UpValueSlot.Inline(state.Stack.UnsafeGet(registerIndex));
            }

            return UpValueSlot.FromCell(state.GetOrAddUpValue(registerIndex));
        }

        if (state.GetCurrentFrame().Function is LuaClosure parentClosure)
        {
            // Copying the parent's slot shares its cell, or copies its (immutable) value.
            return parentClosure.upValues[description.Index];
        }

        throw new();
    }
}
