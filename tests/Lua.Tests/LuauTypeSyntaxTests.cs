using Lua.Standard;

namespace Lua.Tests;

public class LuauTypeSyntaxTests
{
    static async Task<LuaValue[]> RunAsync(string source)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        return await state.DoStringAsync(source);
    }

    [Test]
    public async Task TypeAlias_Ignored()
    {
        var result = await RunAsync("type Foo = number\nreturn 42");
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task ExportTypeAlias_Ignored()
    {
        var result = await RunAsync("export type Foo = number\nreturn 42");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task TypeAlias_GenericDefaults_Ignored()
    {
        var result = await RunAsync("type Foo<T = number> = T\nreturn 42");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task TypeAlias_MultilineTableType_Ignored()
    {
        var source =
            @"type Point = {
    x: number,
    y: number,
}
return 42";
        var result = await RunAsync(source);
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task TypeAlias_UnionSingleton_Ignored()
    {
        var result = await RunAsync("type Mode = \"fast\" | \"slow\"\nreturn 42");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task TypeAlias_FunctionType_Ignored()
    {
        var result = await RunAsync("type F = (number) -> string\nreturn 42");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task TypeAlias_Typeof_Ignored()
    {
        var result = await RunAsync("type X = typeof(game)\nreturn 42");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task TypeAlias_NestedGeneric_Ignored()
    {
        var result = await RunAsync("type X = Foo<Bar<Baz>>\nreturn 42");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task TypeAlias_ArrowInGenericDefault_Ignored()
    {
        var result = await RunAsync("type X<T = (number) -> string> = T\nreturn 42");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task LocalAnnotation_Ignored()
    {
        var result = await RunAsync("local x: number = 5 return x");
        Assert.That(result[0], Is.EqualTo(new LuaValue(5)));
    }

    [Test]
    public async Task LocalAnnotation_UnknownType_Ignored()
    {
        var result = await RunAsync("local x: NotARealType = 5 return x");
        Assert.That(result[0], Is.EqualTo(new LuaValue(5)));
    }

    [Test]
    public async Task LocalAnnotation_TableType_Ignored()
    {
        var result = await RunAsync("local x: { [unknown]: garbage } = 5 return x");
        Assert.That(result[0], Is.EqualTo(new LuaValue(5)));
    }

    [Test]
    public async Task LocalAnnotation_VariadicTypePack_Ignored()
    {
        var result = await RunAsync("local x: ...bad = 1 return x");
        Assert.That(result[0], Is.EqualTo(new LuaValue(1)));
    }

    [Test]
    public async Task LocalAnnotation_Multiple()
    {
        var result = await RunAsync("local a: string, b: boolean = \"x\", false return a, b");
        Assert.That(result, Has.Length.EqualTo(2));
        Assert.That(result[0], Is.EqualTo(new LuaValue("x")));
        Assert.That(result[1], Is.EqualTo(new LuaValue(false)));
    }

    [Test]
    public async Task LocalAnnotation_NoInit()
    {
        var result = await RunAsync("local c: T return c");
        Assert.That(result[0], Is.EqualTo(LuaValue.Nil));
    }

    [Test]
    public async Task FunctionAnnotation_ParamsAndReturn()
    {
        var result = await RunAsync(
            "local function add(a: number, b: number): number return a + b end return add(2, 3)"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(5)));
    }

    [Test]
    public async Task FunctionAnnotation_TableMethod()
    {
        var result = await RunAsync(
            "local t = {} function t:m(x: string): string return x .. \"!\" end return t:m(\"hi\")"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue("hi!")));
    }

    [Test]
    public async Task FunctionAnnotation_Anonymous()
    {
        var result = await RunAsync(
            "local f = function(x: number): number return x * 2 end return f(21)"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task FunctionAnnotation_VarArg()
    {
        var result = await RunAsync(
            "local function f(...: number) return select(\"#\", ...) end return f(1, 2, 3)"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(3)));
    }

    [Test]
    public async Task GenericFunction_Ignored()
    {
        var result = await RunAsync(
            "local function id<T>(x: T): T return x end return id(7)"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(7)));
    }

    [Test]
    public async Task GenericFunction_TypePack()
    {
        var result = await RunAsync(
            "local function f<T, U...>(x: T, ...: U...) return x end return f(9)"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(9)));
    }

    [Test]
    public async Task GenericInstantiation_Explicit()
    {
        var source =
            "local function identity<T>(x: T): T\n"
            + "  return x\n"
            + "end\n\n"
            + "local a = identity<<number>>(42)\n"
            + "local b = identity<<string>>(\"hi\")\n"
            + "local c = identity<<\"hi\">>(\"hi\")\n"
            + "return a, b, c";
        var result = await RunAsync(source);

        Assert.That(result, Has.Length.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
        Assert.That(result[1], Is.EqualTo(new LuaValue("hi")));
        Assert.That(result[2], Is.EqualTo(new LuaValue("hi")));
    }

    [Test]
    public async Task AsExpression_Cast()
    {
        var result = await RunAsync("local x = (5 :: number) + 1 return x");
        Assert.That(result[0], Is.EqualTo(new LuaValue(6)));
    }

    [Test]
    public async Task AsExpression_NilCast()
    {
        var result = await RunAsync("local y = nil :: any return y");
        Assert.That(result[0], Is.EqualTo(LuaValue.Nil));
    }

    [Test]
    public async Task AsExpression_FunctionTypeCast()
    {
        var result = await RunAsync("local x = 5 :: (number) -> string return x");
        Assert.That(result[0], Is.EqualTo(new LuaValue(5)));
    }

    [Test]
    public async Task TypeFunction_NotBound()
    {
        var result = await RunAsync("type function make() return 123 end return make");
        Assert.That(result[0], Is.EqualTo(LuaValue.Nil));
    }

    [Test]
    public async Task TypeFunction_BodyNotExecuted()
    {
        var result = await RunAsync(
            "local n = 0 type function f(): number n = n + 1 return n end return n"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(0)));
    }

    [Test]
    public async Task ForLoop_NumericAnnotation()
    {
        var result = await RunAsync(
            "local sum = 0 for i: number = 1, 3 do sum = sum + i end return sum"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(6)));
    }

    [Test]
    public async Task ForLoop_GenericAnnotation()
    {
        var result = await RunAsync(
            "local s = 0 for k: string, v: number in pairs({ a = 1, b = 2 }) do s = s + v end return s"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(3)));
    }

    [Test]
    public async Task Label_StillWorks()
    {
        var result = await RunAsync(
            "local i = 0 ::top:: i = i + 1 if i < 3 then goto top end return i"
        );
        Assert.That(result[0], Is.EqualTo(new LuaValue(3)));
    }

    [Test]
    public async Task ContextualKeyword_TypeAsIdentifier()
    {
        var result = await RunAsync("local type = 5 return type");
        Assert.That(result[0], Is.EqualTo(new LuaValue(5)));
    }

    [Test]
    public async Task ContextualKeyword_ExportAsIdentifier()
    {
        var result = await RunAsync("local export = {} export.value = 42 return export.value");
        Assert.That(result[0], Is.EqualTo(new LuaValue(42)));
    }

    [Test]
    public async Task ContextualKeyword_TypeAsField()
    {
        var result = await RunAsync("local t = { type = 1 } return t.type");
        Assert.That(result[0], Is.EqualTo(new LuaValue(1)));
    }

    [Test]
    public void UnbalancedTypeDelimiter_Throws()
    {
        Assert.ThrowsAsync<LuaCompileException>(
            async () =>
            {
                await RunAsync("type Foo = { return 1");
            }
        );
    }

    [Test]
    public void StructuralParamError_Throws()
    {
        Assert.ThrowsAsync<LuaCompileException>(
            async () =>
            {
                await RunAsync("local function f(a: number, ) return 1 end");
            }
        );
    }
}
