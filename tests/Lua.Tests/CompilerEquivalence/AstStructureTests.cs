using Lua.CodeAnalysis.Compilation.Ast;
using Lua.Standard;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Structural assertions for each AST node type <see cref="AstParser"/> ("Increment A")
/// produces, per the compiler-rewrite plan's Milestone 1 exit criteria. These check tree shape
/// and resolution results directly, independent of the corpus-driven smoke test in
/// <see cref="AstBuildSmokeTests"/>.
/// </summary>
public class AstStructureTests
{
    static BlockStat Parse(string source)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var body = AstParser.ParseToAst(state, source, "chunk");
        return body;
    }

    [Test]
    public void GlobalNameResolvesToIndexExprAgainstEnv()
    {
        var body = Parse("print(1)");
        var stat = (ExpressionStat)body.Statements[0];
        var call = (CallExpr)stat.Expression;
        var index = (IndexExpr)call.Callee;
        Assert.That(index.IsDotSugar, Is.True);
        Assert.That(((StringExpr)index.Key).Value, Is.EqualTo("print"));
        var env = (NameExpr)index.Object;
        Assert.That(env.Name, Is.EqualTo("_ENV"));
    }

    [Test]
    public void LocalNameResolvesToLocalNameExpr()
    {
        var body = Parse("local x = 1\nlocal y = x");
        var second = (LocalStat)body.Statements[1];
        var name = (NameExpr)second.Initializers[0];
        Assert.That(name.Kind, Is.EqualTo(NameExpr.RefKind.Local));
        Assert.That(name.Name, Is.EqualTo("x"));
    }

    [Test]
    public void CapturedLocalResolvesToUpValueNameExpr()
    {
        var body = Parse("local x = 1\nlocal function f() return x end");
        var fn = (LocalFunctionStat)body.Statements[1];
        var ret = (ReturnStat)fn.Function.Body.Statements[0];
        var name = (NameExpr)ret.Values[0];
        Assert.That(name.Kind, Is.EqualTo(NameExpr.RefKind.UpValue));
        Assert.That(name.Name, Is.EqualTo("x"));
    }

    [Test]
    public void DotAccessAndBracketAccessAreDistinguished()
    {
        var body = Parse("local t = {}\nlocal a = t.x\nlocal b = t[1]");
        var dot = (IndexExpr)((LocalStat)body.Statements[1]).Initializers[0];
        var bracket = (IndexExpr)((LocalStat)body.Statements[2]).Initializers[0];
        Assert.That(dot.IsDotSugar, Is.True);
        Assert.That(dot.Key, Is.TypeOf<StringExpr>());
        Assert.That(bracket.IsDotSugar, Is.False);
        Assert.That(bracket.Key, Is.TypeOf<NumberExpr>());
    }

    [Test]
    public void MethodCallIsKeptDistinctFromPlainCall()
    {
        var body = Parse("local t = {}\nt:m(1)\nt.f(1)");
        var methodStat = (ExpressionStat)body.Statements[1];
        Assert.That(methodStat.Expression, Is.TypeOf<MethodCallExpr>());
        Assert.That(((MethodCallExpr)methodStat.Expression).MethodName, Is.EqualTo("m"));

        var callStat = (ExpressionStat)body.Statements[2];
        Assert.That(callStat.Expression, Is.TypeOf<CallExpr>());
    }

    [Test]
    public void AndOrIsDistinctFromBinaryExpr()
    {
        var body = Parse("local a = 1 + 2\nlocal b = 1 and 2\nlocal c = 1 or 2");
        Assert.That(((LocalStat)body.Statements[0]).Initializers[0], Is.TypeOf<BinaryExpr>());

        var andExpr = (AndOrExpr)((LocalStat)body.Statements[1]).Initializers[0];
        Assert.That(andExpr.IsAnd, Is.True);

        var orExpr = (AndOrExpr)((LocalStat)body.Statements[2]).Initializers[0];
        Assert.That(orExpr.IsAnd, Is.False);
    }

    [Test]
    public void ParenExprWrapsInnerExpression()
    {
        var body = Parse("local a = (1)");
        var paren = (ParenExpr)((LocalStat)body.Statements[0]).Initializers[0];
        Assert.That(paren.Inner, Is.TypeOf<NumberExpr>());
    }

    [Test]
    public void TableConstructorPreservesFieldShapes()
    {
        var body = Parse("local t = {1, 2, x = 3, [4] = 5}");
        var table = (TableConstructorExpr)((LocalStat)body.Statements[0]).Initializers[0];
        Assert.That(table.Fields, Has.Count.EqualTo(4));
        Assert.That(table.Fields[0].Key, Is.Null);
        Assert.That(table.Fields[1].Key, Is.Null);
        Assert.That(((StringExpr)table.Fields[2].Key!).Value, Is.EqualTo("x"));
        Assert.That(table.Fields[3].Key, Is.TypeOf<NumberExpr>());
    }

    [Test]
    public void FunctionExprCapturesParametersAndVararg()
    {
        var body = Parse("local f = function(a, b, ...) end");
        var fn = (FunctionExpr)((LocalStat)body.Statements[0]).Initializers[0];
        Assert.That(fn.Parameters, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(fn.IsVarArg, Is.True);
        Assert.That(fn.IsMethod, Is.False);
    }

    [Test]
    public void MethodFunctionStatementAddsImplicitSelfParameter()
    {
        var body = Parse("local t = {}\nfunction t:m(a) end");
        var stat = (FunctionStat)body.Statements[1];
        Assert.That(stat.Function.IsMethod, Is.True);
        Assert.That(stat.Function.Parameters, Is.EqualTo(new[] { "self", "a" }));
        var target = (IndexExpr)stat.Target;
        Assert.That(((StringExpr)target.Key).Value, Is.EqualTo("m"));
    }

    [Test]
    public void IfStatFlattensElseifChain()
    {
        var body = Parse(
            """
            if 1 then
            elseif 2 then
            elseif 3 then
            else
            end
            """
        );
        var stat = (IfStat)body.Statements[0];
        Assert.That(stat.Branches, Has.Count.EqualTo(3));
        Assert.That(stat.Else, Is.Not.Null);
    }

    [Test]
    public void WhileStatCapturesConditionAndBody()
    {
        var body = Parse("while true do local x = 1 end");
        var stat = (WhileStat)body.Statements[0];
        Assert.That(stat.Condition, Is.TypeOf<TrueExpr>());
        Assert.That(stat.Body.Statements, Has.Count.EqualTo(1));
    }

    [Test]
    public void ForNumericStatDefaultsStepToNull()
    {
        var body = Parse("for i = 1, 10 do end");
        var stat = (ForNumericStat)body.Statements[0];
        Assert.That(stat.Name, Is.EqualTo("i"));
        Assert.That(stat.Step, Is.Null);
    }

    [Test]
    public void ForNumericStatCapturesExplicitStep()
    {
        var body = Parse("for i = 1, 10, 2 do end");
        var stat = (ForNumericStat)body.Statements[0];
        Assert.That(stat.Step, Is.Not.Null);
    }

    [Test]
    public void ForGenericStatCapturesNamesAndExpressions()
    {
        var body = Parse("local t = {}\nfor k, v in pairs(t) do end");
        var stat = (ForGenericStat)body.Statements[1];
        Assert.That(stat.Names, Is.EqualTo(new[] { "k", "v" }));
        Assert.That(stat.Expressions, Has.Count.EqualTo(1));
    }

    [Test]
    public void AssignmentStatCapturesMultipleTargets()
    {
        var body = Parse("local a, b\na, b = 1, 2");
        var stat = (AssignmentStat)body.Statements[1];
        Assert.That(stat.Targets, Has.Count.EqualTo(2));
        Assert.That(stat.Values, Has.Count.EqualTo(2));
    }

    [Test]
    public void ReturnStatCapturesValues()
    {
        var body = Parse("return 1, 2");
        var stat = (ReturnStat)body.Statements[0];
        Assert.That(stat.Values, Has.Count.EqualTo(2));
    }

    [Test]
    public void TypeStatementIsANoOp()
    {
        // `type` is erased at parse time with no runtime effect (SkipTypeAnnotation) even in
        // today's compiler, so it produces an empty statement rather than a dedicated node.
        var body = Parse("type X = number\nexport type Y = string\nlocal a = 1");
        Assert.That(((BlockStat)body.Statements[0]).Statements, Is.Empty);
        Assert.That(((BlockStat)body.Statements[1]).Statements, Is.Empty);
        Assert.That(body.Statements[2], Is.TypeOf<LocalStat>());
    }

    [Test]
    public void RepeatStatCapturesBodyAndCondition()
    {
        var body = Parse("repeat local x = 1 until x == 1");
        var stat = (RepeatStat)body.Statements[0];
        Assert.That(stat.Body.Statements, Has.Count.EqualTo(1));
        Assert.That(stat.Condition, Is.TypeOf<BinaryExpr>());
    }

    [Test]
    public void RepeatConditionSeesBodyLocal()
    {
        // The `until` condition is parsed with the body's scope still open -- `x` here must
        // resolve as the body's local, not a global.
        var body = Parse("repeat local x = 1 until x == 1");
        var stat = (RepeatStat)body.Statements[0];
        var condition = (BinaryExpr)stat.Condition;
        var name = (NameExpr)condition.Left;
        Assert.That(name.Kind, Is.EqualTo(NameExpr.RefKind.Local));
    }

    [Test]
    public void CompoundAssignmentCapturesTargetOpAndValue()
    {
        var body = Parse("local x = 1\nx += 2");
        var stat = (CompoundAssignmentStat)body.Statements[1];
        Assert.That(stat.Target, Is.TypeOf<NameExpr>());
        Assert.That(stat.Value, Is.TypeOf<NumberExpr>());
    }

    [Test]
    public void BreakContinueGotoLabelProduceExpectedNodes()
    {
        var body = Parse(
            """
            while true do
                if true then break end
                continue
            end
            ::top::
            goto top
            """
        );
        var whileStat = (WhileStat)body.Statements[0];
        var ifStat = (IfStat)whileStat.Body.Statements[0];
        Assert.That(ifStat.Branches[0].Then.Statements[0], Is.TypeOf<BreakStat>());
        Assert.That(whileStat.Body.Statements[1], Is.TypeOf<ContinueStat>());
        Assert.That(body.Statements[1], Is.TypeOf<LabelStat>());
        Assert.That(((LabelStat)body.Statements[1]).Name, Is.EqualTo("top"));
        Assert.That(((GotoStat)body.Statements[2]).Name, Is.EqualTo("top"));
    }

    [Test]
    public void ConstStatCapturesNamesAndInitializers()
    {
        var body = Parse("const x = 1");
        var stat = (ConstStat)body.Statements[0];
        Assert.That(stat.Names, Is.EqualTo(new[] { "x" }));
        Assert.That(stat.Initializers[0], Is.TypeOf<NumberExpr>());
    }

    [Test]
    public void ConstFunctionSetsIsConstFlag()
    {
        var body = Parse("const function f() end");
        var stat = (LocalFunctionStat)body.Statements[0];
        Assert.That(stat.IsConst, Is.True);
    }

    [Test]
    public void IfElseExpressionFlattensElseifChain()
    {
        var body = Parse("local a = if 1 then 2 elseif 3 then 4 else 5");
        var expr = (IfElseExpr)((LocalStat)body.Statements[0]).Initializers[0];
        Assert.That(expr.Branches, Has.Count.EqualTo(2));
        Assert.That(expr.Else, Is.TypeOf<NumberExpr>());
    }

    [Test]
    public void InterpolatedStringCapturesLiteralAndHoleParts()
    {
        var body = Parse("""local a = `x = {1}!`""");
        var expr = (InterpolatedStringExpr)((LocalStat)body.Statements[0]).Initializers[0];
        Assert.That(expr.Parts, Has.Count.EqualTo(3));
        Assert.That(expr.Parts[0], Is.TypeOf<StringExpr>());
        Assert.That(((StringExpr)expr.Parts[0]).Value, Is.EqualTo("x = "));
        Assert.That(expr.Parts[1], Is.TypeOf<NumberExpr>());
        Assert.That(((StringExpr)expr.Parts[2]).Value, Is.EqualTo("!"));
    }
}
