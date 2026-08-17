using Lua;
using Lua.Standard;

namespace Lua.Tests;

/// <summary>
/// Verifies that when a C# function throws, the managed (C#) stack trace is rendered
/// inside the Lua stack traceback, directly under the <c>[C#]: in function '...'</c> frame,
/// instead of only showing the C# function name.
/// </summary>
public class ExceptionTracebackTests
{
    LuaState state = default!;

    [SetUp]
    public void SetUp()
    {
        state = LuaState.Create();
        state.OpenStandardLibraries();
    }

    static void RegisterThrowingFunction(LuaState state, string name = "throwManaged")
    {
        state.Environment[name] = new LuaFunction(
            name,
            (context, ct) =>
            {
                throw new InvalidOperationException("boom");
            }
        );
    }

    [Test]
    public void LuaTraceback_ManagedException_IncludesManagedStackTrace()
    {
        RegisterThrowingFunction(state);

        var ex = Assert.Throws<LuaRuntimeException>(() => state.DoString("throwManaged()"));

        Assert.That(ex!.InnerException, Is.TypeOf<InvalidOperationException>());

        var traceback = ex.LuaTraceback!.ToString();
        TestContext.WriteLine(traceback);

        Assert.That(traceback, Does.Contain("[C#]: in function 'throwManaged'"));
        Assert.That(traceback, Does.Contain("[C#]: at "));
        Assert.That(traceback, Does.Contain("Lua.Tests.ExceptionTracebackTests"));
    }

    [Test]
    public void LuaTraceback_ManagedStackTrace_RenderedUnderDeepestCSharpFrame()
    {
        RegisterThrowingFunction(state);

        var ex = Assert.Throws<LuaRuntimeException>(
            () => state.DoString("local function wrapper() throwManaged() end; wrapper()")
        );

        var traceback = ex!.LuaTraceback!.ToString();
        TestContext.WriteLine(traceback);

        var csharpFrame = traceback.IndexOf("[C#]: in function 'throwManaged'", StringComparison.Ordinal);
        var managedFrame = traceback.IndexOf("[C#]: at ", StringComparison.Ordinal);
        var luaFrame = traceback.IndexOf("'wrapper'", StringComparison.Ordinal);

        Assert.That(csharpFrame, Is.GreaterThanOrEqualTo(0));
        Assert.That(managedFrame, Is.GreaterThan(csharpFrame));
        Assert.That(luaFrame, Is.GreaterThan(managedFrame));
    }

    [Test]
    public void ToString_ManagedException_IncludesManagedTraceInsideLuaTraceback()
    {
        RegisterThrowingFunction(state);

        var ex = Assert.Throws<LuaRuntimeException>(() => state.DoString("throwManaged()"));

        var str = ex!.ToString();
        TestContext.WriteLine(str);

        Assert.That(str, Does.Contain("[C#]: in function 'throwManaged'"));
        Assert.That(str, Does.Contain("[C#]: at "));
        Assert.That(str, Does.Contain("Lua.Tests.ExceptionTracebackTests"));
    }
}
