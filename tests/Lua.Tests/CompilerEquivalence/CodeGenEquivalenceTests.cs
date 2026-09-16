using Lua.CodeAnalysis.Compilation.Ast;
using Lua.Standard;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Milestone 2 of the compiler-rewrite plan: compiles the same source with today's compiler
/// (<c>state.Load</c>) and with the new AST + <see cref="CodeGenerator"/> pipeline.
///
/// The primary assertion compares <b>structural</b> dumps (opcodes/operands/instruction counts/
/// constants/locals/upvalues -- everything except per-instruction line numbers): these must be
/// byte-identical, since that's what determines actual VM behavior. Per-instruction line numbers
/// are deliberately *not* required to match: the original compiler stamps them as a side effect
/// of its scanner's token-lookahead timing (an implementation quirk of the single-pass design,
/// not a deliberate choice), whereas <see cref="CodeGenerator"/> adopts Luau's model of explicit,
/// AST-position-derived stamps (see Compiler.cpp's <c>setDebugLine</c>/<c>setDebugLineEnd</c>
/// convention) -- deliberately chosen as the more correct behavior even where it now differs
/// from the original. A full (line-including) dump is still logged on any structural mismatch,
/// for debugging, and line-only differences are logged separately without failing the test.
/// </summary>
public class CodeGenEquivalenceTests
{
    static void AssertEquivalent(string source)
    {
        var oldState = LuaState.Create();
        oldState.OpenStandardLibraries();
        var oldClosure = oldState.Load(source, "chunk");

        var newState = LuaState.Create();
        newState.OpenStandardLibraries();
        var body = AstParser.ParseToAst(newState, source, "chunk");
        var newProto = CodeGenerator.Generate(newState, body, "chunk");

        var oldStructural = BytecodeDump.Dump(oldClosure.Proto, includeLines: false);
        var newStructural = BytecodeDump.Dump(newProto, includeLines: false);

        if (newStructural != oldStructural)
        {
            TestContext.Out.WriteLine("=== OLD (full) ===\n" + BytecodeDump.Dump(oldClosure.Proto));
            TestContext.Out.WriteLine("=== NEW (full) ===\n" + BytecodeDump.Dump(newProto));
        }
        else
        {
            var oldFull = BytecodeDump.Dump(oldClosure.Proto);
            var newFull = BytecodeDump.Dump(newProto);
            if (oldFull != newFull)
            {
                TestContext.Out.WriteLine(
                    "Structural match; line numbers differ (expected under the Luau-style "
                        + "line model -- see class doc comment):"
                );
                TestContext.Out.WriteLine("=== OLD (full) ===\n" + oldFull);
                TestContext.Out.WriteLine("=== NEW (full) ===\n" + newFull);
            }
        }

        Assert.That(newStructural, Is.EqualTo(oldStructural));
    }

    [Test]
    public void SimpleLocalAndReturn()
    {
        AssertEquivalent("local x = 1\nreturn x");
    }

    [Test]
    public void Arithmetic()
    {
        AssertEquivalent("local x = 1 + 2 * 3 - 4 / 2\nreturn x");
    }

    [Test]
    public void GlobalCallAndStringLiteral()
    {
        AssertEquivalent("print('hello')");
    }

    [Test]
    public void IfElseif()
    {
        AssertEquivalent(
            """
            local x = 1
            if x == 1 then
                print('one')
            elseif x == 2 then
                print('two')
            else
                print('other')
            end
            """
        );
    }

    [Test]
    public void WhileLoop()
    {
        AssertEquivalent(
            """
            local i = 0
            while i < 10 do
                i = i + 1
            end
            return i
            """
        );
    }

    [Test]
    public void NumericForLoop()
    {
        AssertEquivalent(
            """
            local sum = 0
            for i = 1, 10 do
                sum = sum + i
            end
            return sum
            """
        );
    }

    [Test]
    public void GenericForLoop()
    {
        AssertEquivalent(
            """
            local t = {1, 2, 3}
            local sum = 0
            for i, v in ipairs(t) do
                sum = sum + v
            end
            return sum
            """
        );
    }

    [Test]
    public void TableConstructor()
    {
        AssertEquivalent("local t = {1, 2, x = 3, [4] = 5}\nreturn t");
    }

    [Test]
    public void LargeArrayTableConstructor()
    {
        var items = string.Join(", ", Enumerable.Range(1, 120));
        AssertEquivalent($"local t = {{{items}}}\nreturn t");
    }

    [Test]
    public void Closures()
    {
        AssertEquivalent(
            """
            local function counter()
                local n = 0
                return function()
                    n = n + 1
                    return n
                end
            end
            local c = counter()
            return c(), c(), c()
            """
        );
    }

    [Test]
    public void MethodCall()
    {
        AssertEquivalent(
            """
            local t = {}
            function t:m(a)
                return a
            end
            return t:m(1)
            """
        );
    }

    [Test]
    public void MultipleAssignmentAndReturn()
    {
        AssertEquivalent("local a, b = 1, 2\na, b = b, a\nreturn a, b");
    }

    [Test]
    public void CompoundAssignment()
    {
        AssertEquivalent("local t = {1}\nt[1] += 1\nreturn t[1]");
    }

    [Test]
    public void RepeatUntil()
    {
        AssertEquivalent(
            """
            local i = 0
            repeat
                i = i + 1
            until i == 5
            return i
            """
        );
    }

    [Test]
    public void BreakInsideNestedIfInsideFor()
    {
        AssertEquivalent(
            """
            local function devRemove(node)
                if node.parent then
                    local pc = node.parent.children
                    for i = 1, #pc do
                        if pc[i] == node then
                            table.remove(pc, i)
                            break
                        end
                    end
                end
            end
            return devRemove
            """
        );
    }

    [Test]
    public void BreakAndContinue()
    {
        AssertEquivalent(
            """
            local sum = 0
            for i = 1, 10 do
                if i == 5 then
                    continue
                end
                if i == 8 then
                    break
                end
                sum = sum + i
            end
            return sum
            """
        );
    }

    [Test]
    public void GotoLabel()
    {
        AssertEquivalent(
            """
            local i = 0
            ::top::
            i = i + 1
            if i < 5 then
                goto top
            end
            return i
            """
        );
    }

    [Test]
    public void ConstDeclaration()
    {
        AssertEquivalent("const x = 5\nreturn x");
    }

    [Test]
    public void IfElseExpression()
    {
        AssertEquivalent("local a = if true then 1 else 2\nreturn a");
    }

    [Test]
    public void InterpolatedString()
    {
        AssertEquivalent(
            """
            local x = 1
            local s = `x = {x}!`
            return s
            """
        );
    }

    [Test]
    public void AndOrShortCircuit()
    {
        AssertEquivalent("local a = 1\nlocal b = a and 2 or 3\nreturn b");
    }

    [Test]
    public void VarargFunction()
    {
        AssertEquivalent(
            """
            local function f(...)
                return select('#', ...)
            end
            return f(1, 2, 3)
            """
        );
    }

    [TestCaseSource(typeof(CorpusGoldenTests), nameof(CorpusGoldenTests.CorpusFiles))]
    public void CorpusFileIsByteIdentical(string sourcePath)
    {
        AssertEquivalent(File.ReadAllText(sourcePath));
    }
}
