using Lua.Runtime;
using Lua.Standard;

namespace Lua.Tests;

/// <summary>
/// Deterministic regression test for the <c>Concat</c> / <c>PostOperation</c> data race that
/// produced the long-standing <c>tests-lua/db.lua</c> flake.
///
/// <para>
/// The VM's opcode handlers do not await their own pending work: they park it in
/// <c>context.Task</c> and return to <c>ExecuteClosureAsyncImpl</c>, which is what awaits it. So
/// from the instant a handler's task exists, a thread-pool thread may already be running its
/// continuation while the dispatching thread is still unwinding out of <c>MoveNext</c> -- two
/// threads genuinely live on one <see cref="LuaState"/>. (A re-entrancy guard over <c>MoveNext</c>
/// and the resume block observes this 4-13 times per full-suite run, always in that shape.) That
/// is harmless for every opcode except <c>Concat</c>, whose pending task is an async helper that
/// closes over the *outer* context and writes <c>PostOperation = DontPop</c> to it -- so a
/// <c>PostOperation</c> write by the dispatcher *after* starting that task races it, and losing
/// that race silently collapses the call stack.
/// </para>
///
/// <para>
/// Rather than wait for the real race (which needed dozens of full-suite runs to surface once),
/// this test stalls the dispatching thread inside exactly that window via the Debug-only
/// <c>LuaVirtualMachine.ConcatSuspendedHookForTests</c> seam, which turns the race into a
/// certainty. With the ordering bug present, both concat cases below fail on every run; with the
/// dispatcher's write hoisted above the task creation, they pass on every run. The <c>__index</c>
/// case is the negative control: that metamethod dispatch uses the "doRestart" trampoline rather
/// than an outer-context-capturing helper, so it must be unaffected either way.
/// </para>
/// </summary>
public class ConcatDispatchRaceTests
{
#if DEBUG
    static LuaState CreateStateWithAsyncYield()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        state.Environment["async_yield"] = new LuaFunction(
            "async_yield",
            async (context, ct) =>
            {
                // A real thread-pool hop, so the helper's continuation genuinely resumes on
                // another thread rather than completing inline.
                await Task.Yield();
                return context.Return();
            }
        );
        return state;
    }

    [SetUp]
    public void ArmTheStall()
    {
        // Long enough that the continuation reliably wins; this only ever runs on the
        // suspending-concat path, of which these scripts have a few dozen.
        LuaVirtualMachine.ConcatSuspendedHookForTests = static () => Thread.Sleep(1);
    }

    [TearDown]
    public void DisarmTheStall()
    {
        LuaVirtualMachine.ConcatSuspendedHookForTests = null;
    }

    [Test]
    public async Task ConcatMetamethodAcrossAnAwait_KeepsTheCallerFrame()
    {
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

        Assert.That(
            result[0].ToBoolean(),
            Is.False,
            $"caller-of-the-metamethod frame went missing (what={result[1]})"
        );
    }

    [Test]
    public async Task RepeatedConcatAcrossAnAwait_KeepsTheCallerFrameEveryTime()
    {
        var state = CreateStateWithAsyncYield();
        var result = await state.DoStringAsync(
            """
            local a = setmetatable({}, { __concat = function(t, k)
                async_yield()
                return debug.getinfo(2) == nil
            end })

            for i = 1, 20 do
                if a..a then
                    return true, i
                end
            end

            return false, nil
            """
        );

        Assert.That(
            result[0].ToBoolean(),
            Is.False,
            $"caller-of-the-metamethod frame went missing on iteration {result[1]}"
        );
    }

    [Test]
    public async Task IndexMetamethodAcrossAnAwait_IsUnaffected()
    {
        // Negative control: __index dispatches through the doRestart trampoline, not through an
        // async helper that captures the outer context, so the stall must change nothing.
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

        Assert.That(
            result[0].ToBoolean(),
            Is.False,
            $"caller-of-the-metamethod frame went missing (what={result[1]})"
        );
    }
#else
    [Test]
    public void OnlyMeaningfulInDebugBuilds()
    {
        // The stall seam this fixture drives is compiled out of Release, so there is nothing to
        // assert here. The Release-side protection is the hoisted write itself; the Debug suite is
        // where the ordering requirement is actually enforced.
        Assert.Pass("ConcatSuspendedHookForTests is Debug-only.");
    }
#endif
}
