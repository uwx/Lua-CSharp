using Lua.Standard;

namespace Lua.Tests;

/// <summary>
/// Minimal, deterministic reproduction of the db.lua flake (see the compiler-rewrite plan's
/// "known suite flake" section), isolating it from the other 600+ lines of tests-lua/db.lua and
/// its dependence on full-suite timing.
///
/// Root cause, found by direct C#-level tracing of every call-stack push/pop plus the exact
/// <c>PostOperation</c> value at each resume point: <c>LuaVirtualMachine</c>'s Concat-specific
/// metamethod dispatch (the <c>async ValueTask ExecuteBinaryOperationMetaMethod(int, LuaValue,
/// LuaValue, VirtualMachineExecutionContext, OpCode)</c> overload used only by the
/// <see cref="Lua.Runtime.OpCode.Concat"/> handler) is missing the
/// <c>if (func is LuaClosure) { context.Push(newFrame); doRestart = true; return true; }</c>
/// fast path that every *other* metamethod dispatch site in that file has (see the sibling
/// <c>ExecuteBinaryOperationMetaMethod</c> overload used by Add/Sub/Mul/.../Pow, and the
/// Compare/GetTable dispatch code, all of which check this before falling back to
/// <c>await func.Func(...)</c>). Missing that check means a <c>..</c> whose metamethod target is
/// itself a plain Lua closure is *always* invoked through the generic
/// <c>LuaFunction.Func</c>/<c>ExecuteClosureAsync</c> path, which creates a brand-new, nested
/// <c>VirtualMachineExecutionContext</c> (with its own fresh <c>BaseCallStackCount</c>) instead
/// of continuing to run inside the same dispatch loop the way the "doRestart" trampoline does
/// for every other opcode.
///
/// That nesting is harmless as long as the closure's own execution never needs a genuine
/// asynchronous suspension. But if it does -- in this repro and in db.lua, because the test
/// harness's own `assert` replacement does a real <c>await Task.Yield()</c> -- the resume path
/// loses track of whose <c>PostOperation</c> is whose: <c>ExecuteBinaryOperationMetaMethod</c>'s
/// own <c>finally</c> correctly pops the callee's frame and sets the outer context's
/// <c>PostOperation</c> to <c>DontPop</c> (confirmed present immediately before the pop, via a
/// temporary trace), but by the time the *outer* context's own resume code reads that field, it
/// has reverted to <c>None</c> -- so the outer context pops an *extra*, unrelated frame (its own
/// caller's), collapsing the call stack down to empty. From inside the metamethod, this shows up
/// as <c>debug.getinfo(2)</c> (the caller-of-the-metamethod frame) suddenly returning <c>nil</c>
/// -- exactly the symptom that made <c>db.lua</c>'s <c>namewhat == "metamethod"</c> assertion
/// intermittently see <c>namewhat == ""</c> instead.
///
/// This needs several chained metamethod dispatches (or several preceding awaits) before it
/// reproduces reliably in isolation -- likely because the corruption depends on timing between
/// the async continuation resuming on a different thread-pool thread and the synchronous
/// cascade of nested `await`s unwinding, which needs enough real work stacked up to manifest
/// consistently. <see cref="RepeatedConcat_ThroughAwait_NeverLosesTheCallerFrame"/> hammers the
/// path repeatedly to raise the odds of catching it in one run rather than relying on luck.
/// </summary>
public class MetamethodFrameDiagTests
{
    static LuaState CreateStateWithAsyncYield()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        state.Environment["async_yield"] = new LuaFunction(
            "async_yield",
            async (context, ct) =>
            {
                await Task.Yield();
                return context.Return();
            }
        );
        return state;
    }

    [Test]
    public async Task CallerFrame_SurvivesAnAwaitInsideAnIndexMetamethod()
    {
        // __index correctly uses the "doRestart" trampoline (GetTableValueSlowPath /
        // CallGetTableFunc), so this is the *negative* control: it must never lose the frame,
        // confirming the bug is specific to Concat's dispatch, not to "any metamethod that
        // awaits inside a closure".
        var state = CreateStateWithAsyncYield();
        var result = await state.DoStringAsync(
            """
            local lost, what
            local a = setmetatable({}, { __index = function(t, k)
                async_yield()
                local info = debug.getinfo(2)
                lost = info == nil
                what = info and info.what or "nil"
            end })
            local _ = a.foo
            return lost, what
            """
        );

        Assert.That(result[0].ToBoolean(), Is.False, $"level-2 frame missing (what={result[1]})");
    }

    [Test]
    public async Task CallerFrame_IsLostAcrossAnAwaitInsideAConcatMetamethod()
    {
        // __concat is the one dispatch path missing the LuaClosure fast-path check (see the
        // class doc). This is the positive repro: it currently reproduces the flake reliably in
        // isolation, needing only a handful of chained metamethod calls -- no full-suite load,
        // no coroutines, no earlier state in a 600-line file required.
        var state = CreateStateWithAsyncYield();
        var result = await state.DoStringAsync(
            """
            local lost, what
            local a = setmetatable({}, { __concat = function(t, k)
                async_yield()
                local info = debug.getinfo(2)
                lost = info == nil
                what = info and info.what or "nil"
                return "x"
            end })
            local _ = a..a
            return lost, what
            """
        );

        Assert.That(result[0].ToBoolean(), Is.False, $"level-2 frame missing (what={result[1]})");
    }

    [Test]
    public async Task RepeatedConcat_ThroughAwait_NeverLosesTheCallerFrame()
    {
        var state = CreateStateWithAsyncYield();
        var result = await state.DoStringAsync(
            """
            local a = setmetatable({}, { __concat = function(t, k)
                async_yield()
                local info = debug.getinfo(2)
                return info == nil
            end })

            for i = 1, 50 do
                local lost = a..a
                if lost then
                    return true, i
                end
            end

            return false, nil
            """
        );

        Assert.That(
            result[0].ToBoolean(),
            Is.False,
            $"level-2 frame went missing on iteration {result[1]}"
        );
    }
}
