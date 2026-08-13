using Lua.Standard;

namespace Lua.Tests;

/// <summary>
/// Tests for the synchronous execution entry points (DoString, Execute, DoFile, Run).
/// Sync execution runs the chunk to completion and throws LuaYieldException when the
/// script would have to suspend (e.g. an async C# function). Coroutine yields absorbed
/// synchronously by coroutine.resume / coroutine.wrap are allowed.
/// </summary>
public class SyncExecutionTests
{
    static LuaState CreateState(Action<LuaState>? configure = null)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        configure?.Invoke(state);
        return state;
    }

    static void RegisterWaitFunction(LuaState state)
    {
        state.Environment["wait"] = new LuaFunction(
            "wait",
            async (context, ct) =>
            {
                await Task.Delay(100, ct);
                return context.Return();
            }
        );
    }

    [Test]
    public void DoString_PureCompute_ReturnsResults()
    {
        var state = CreateState();

        var results = state.DoString("return (2 + 3) * 4");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Read<double>(), Is.EqualTo(20));
    }

    [Test]
    public void DoString_AsyncHostFunction_ThrowsLuaYieldException()
    {
        var state = CreateState();
        RegisterWaitFunction(state);

        Assert.Throws<LuaYieldException>(() => state.DoString("wait()"));
    }

    [Test]
    public void DoString_ResumeOfYieldingCoroutine_IsAllowed_ReturnsResults()
    {
        var state = CreateState();

        var results = state.DoString(
            """
            local co = coroutine.create(function(a)
                local b = coroutine.yield(a + 1)
                return b * 2
            end)
            local _, x = coroutine.resume(co, 10)
            local _, y = coroutine.resume(co, 5)
            return x + y
            """
        );

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Read<double>(), Is.EqualTo(21));
    }

    [Test]
    public void DoString_YieldOutsideCoroutine_ThrowsLuaRuntimeException()
    {
        var state = CreateState();

        Assert.Throws<LuaRuntimeException>(() => state.DoString("coroutine.yield()"));
    }

    [Test]
    public void DoString_PCallOfSuspendingFunction_ReturnsFalseAndErrorMessage()
    {
        var state = CreateState();
        RegisterWaitFunction(state);

        var results = state.DoString(
            """
            local ok, err = pcall(function() wait() end)
            return ok, err
            """
        );

        Assert.That(results[0].ToBoolean(), Is.False);
        Assert.That(
            results[1].Read<string>(),
            Does.Contain("attempt to yield during synchronous execution")
        );
    }

    [Test]
    public void DoString_SyncModeFlagIsRestored_AfterThrow()
    {
        var state = CreateState();
        RegisterWaitFunction(state);

        Assert.Throws<LuaYieldException>(() => state.DoString("wait()"));

        // The flag must be restored so subsequent async execution works and still
        // completes synchronously for pure compute.
        var task = state.DoStringAsync("return 1");
        Assert.That(task.IsCompleted, Is.True);
        Assert.That(task.GetAwaiter().GetResult()[0].Read<double>(), Is.EqualTo(1));
    }

    [Test]
    public void DoString_NestedSyncCall_FromHostFunction_Works()
    {
        var state = CreateState();
        state.Environment["nested"] = new LuaFunction(
            "nested",
            (context, ct) =>
            {
                var results = context.State.DoString("return 42");
                return new(context.Return(results[0]));
            }
        );

        var results = state.DoString("return nested()");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Read<double>(), Is.EqualTo(42));
    }

    [Test]
    public void Execute_ByteChunk_ReturnsResults()
    {
        var state = CreateState();

        var results = state.Execute("return 1 + 2"u8, "@bytes.lua");

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Read<double>(), Is.EqualTo(3));
    }

    [Test]
    public void Run_Closure_LeavesResultsOnStack()
    {
        var state = CreateState();
        var closure = state.Load("return 3", "@run.lua");

        var count = state.Run(closure);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(state.Stack[state.Stack.Count - 1].Read<double>(), Is.EqualTo(3));
    }

    [Test]
    public void DoString_BufferOverload_WritesResultsToBuffer()
    {
        var state = CreateState();
        var buffer = new LuaValue[4];

        var count = state.DoString("return 1, 2, 3", buffer);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(buffer[0].Read<double>(), Is.EqualTo(1));
        Assert.That(buffer[1].Read<double>(), Is.EqualTo(2));
        Assert.That(buffer[2].Read<double>(), Is.EqualTo(3));
    }

    [Test]
    public void DoFile_BuiltInFileSystem_ReturnsResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "return 6 * 7");
            var state = CreateState();

            var results = state.DoFile(path);

            Assert.That(results, Has.Length.EqualTo(1));
            Assert.That(results[0].Read<double>(), Is.EqualTo(42));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
