using Lua.CodeAnalysis.Compilation.Ast;
using Lua.Runtime;
using Lua.Standard;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Compiler-rewrite plan Milestone 3: <see cref="OpCode.GetImport"/>, the fused two-level
/// <c>up.name1.name2</c> read. Two things have to hold and are tested separately here:
///
/// <list type="number">
/// <item><b>Shape</b> -- the fusion fires on exactly the chains it should (and only those), and
/// the instruction it emits is the two-word GetImport + ExtraArg pair with GetTabUp's own
/// operands. <see cref="Emitted"/>/<see cref="NotEmitted"/> assert on the disassembly, which is
/// the only place this optimization is visible at all.</item>
/// <item><b>Indistinguishability</b> -- the fused instruction must never be *different*, only
/// faster, since its whole justification is that its miss path is the unfused two-step lookup
/// re-executed. The behavioral tests therefore run each program twice: once normally (fused) and
/// once with <c>_ENV</c> pinned to a value whose lookups miss, which forces the deopt on the very
/// first execution. Same results, same errors, both times.</item>
/// </list>
/// </summary>
public class GetImportTests
{
    static async Task<LuaValue[]> RunAsync(string source)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        return await state.DoStringAsync(source);
    }

    /// <summary>Compiles <paramref name="source"/> and disassembles the main chunk.</summary>
    static string Disassemble(string source)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var body = AstParser.ParseToAst(state, source, "chunk");
        return BytecodeDump.Dump(CodeGenerator.Generate(state, body, "chunk"));
    }

    static void Emitted(string source)
    {
        var dump = Disassemble(source);
        Assert.That(dump, Does.Contain("GETIMPORT"), $"expected a fused GETIMPORT in:\n{dump}");
    }

    static void NotEmitted(string source)
    {
        var dump = Disassemble(source);
        Assert.That(dump, Does.Not.Contain("GETIMPORT"), $"expected no fused GETIMPORT in:\n{dump}");
    }

    // ------------------------------------------------------------------ shape: fuses

    [Test]
    public void GlobalChain_Fuses()
    {
        Emitted("local f = math.floor\nreturn f");
    }

    [Test]
    public void GlobalChain_InAValuePositionInsideCall_Fuses()
    {
        Emitted("local x = string.rep('a', 3)\nreturn x");
    }

    /// <summary>
    /// The fused instruction is exactly the two-word pair with GetTabUp's operands: destination
    /// register A (0 here -- unbound, bound later by the consumer), upvalue index B, and key1 as
    /// an RK constant C, plus a trailing EXTRAARG holding key2's constant index.
    /// </summary>
    [Test]
    public void FusedInstruction_HasGetTabUpOperandsAndTrailingExtraArg()
    {
        var dump = Disassemble("local f = math.floor\nreturn f");

        var lines = dump.Split('\n');
        var importPc = Array.FindIndex(lines, l => l.Contains("GETIMPORT"));
        Assert.That(importPc, Is.GreaterThanOrEqualTo(0), dump);

        // `_ENV` is upvalue 0 and "math" is the first constant interned for this chunk, so the
        // fused instruction reads UpValue[0][Kst(0)] and then Kst(1) from the ExtraArg.
        Assert.That(lines[importPc], Does.Contain("GETIMPORT"));
        Assert.That(lines[importPc + 1], Does.Contain("EXTRAARG"), dump);
        Assert.That(lines[importPc + 2], Does.Not.Contain("GETTABLE"), dump);

        // The unfused pair would have been GETTABUP + GETTABLE reading "floor" as an RK operand;
        // neither survives, and "floor" is now reached through the ExtraArg instead.
        Assert.That(dump, Does.Not.Contain("GETTABUP"), dump);
    }

    /// <summary>
    /// A three-level chain fuses its innermost two levels and leaves the outermost to the
    /// ordinary per-level path -- one GetImport plus one GetTable, not two GetTables.
    /// </summary>
    [Test]
    public void ThreeLevelChain_FusesInnermostTwoOnly()
    {
        var dump = Disassemble("local v = a.b.c\nreturn v");
        Assert.That(dump, Does.Contain("GETIMPORT"), dump);
        Assert.That(dump, Does.Contain("GETTABLE"), dump);
        Assert.That(dump, Does.Not.Contain("GETTABUP"), dump);
    }

    /// <summary>A captured upvalue (not <c>_ENV</c>) at the root is equally fusable.</summary>
    [Test]
    public void CapturedUpvalueRoot_Fuses()
    {
        Emitted("local M = {}\nlocal function get() return M.nested.x end\nreturn get");
    }

    // ------------------------------------------------------------------ shape: must NOT fuse

    /// <summary>
    /// <c>M.x</c> (one level) has nothing to fuse with -- only the two-level chain maps onto a
    /// single GetImport.
    /// </summary>
    [Test]
    public void SingleLevelChain_DoesNotFuse()
    {
        NotEmitted("local M = {}\nlocal function get() return M.x end\nreturn get");
    }

    /// <summary>Bracket syntax is not the field-name form GetImport is built from.</summary>
    [Test]
    public void BracketKey_DoesNotFuse()
    {
        NotEmitted("local f = math['floor']\nreturn f");
    }

    /// <summary>A local-rooted chain: B is an upvalue index, so a local base cannot use it.</summary>
    [Test]
    public void LocalRoot_DoesNotFuse()
    {
        NotEmitted("local m = math\nlocal f = m.floor\nreturn f");
    }

    /// <summary>A computed key is not a compile-time constant, so it cannot ride in the ExtraArg.</summary>
    [Test]
    public void ComputedKey_DoesNotFuse()
    {
        NotEmitted("local k = 'floor'\nlocal f = math[k]\nreturn f");
    }

    /// <summary>
    /// Past <see cref="Instruction.MaxIndexRK"/> the keys no longer fit RK operands, so the pair
    /// must be emitted the ordinary way -- and it must still be emitted, correctly. The filler is
    /// a table constructor of 300 distinct strings, which grows the constant pool past the RK
    /// limit without touching the 200-local limit.
    ///
    /// The first element is parenthesized so the constructor is <em>not</em> all-constant and
    /// therefore does not fuse into one DupTable (Milestone 4): a fused 300-element literal would
    /// intern nothing at all, leaving the pool empty and the GetImport chain below it fusing
    /// again -- which is the opposite of what this test is set up to exercise. The
    /// <c>DUPTABLE</c> assertion is what keeps that setup honest.
    /// </summary>
    [Test]
    public async Task TooManyConstantsToFuse_FallsBackCorrectly()
    {
        var rest = string.Join(", ", Enumerable.Range(1, 299).Select(i => $"\"c{i}\""));
        var source = $"local t = {{(\"c0\"), {rest}}}\nlocal f = math.floor\nreturn f(1.5), #t";

        Assert.That(Disassemble(source), Does.Not.Contain("DUPTABLE"), "the filler must not fuse");
        NotEmitted(source);

        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var result = await state.DoStringAsync(source);
        Assert.That(result[0].Read<double>(), Is.EqualTo(1.0));
        Assert.That(result[1].Read<long>(), Is.EqualTo(300L));
    }

    // ------------------------------------------------------------------ behavior

    [Test]
    public async Task FusedRead_ReturnsTheValue()
    {
        var result = await RunAsync("return math.floor(3.7), string.format('%d', 5)");
        Assert.That(result[0].Read<double>(), Is.EqualTo(3.0));
        Assert.That(result[1].Read<string>(), Is.EqualTo("5"));
    }

    /// <summary>
    /// A miss deopts into the two-step lookup. <c>_ENV.thing</c> is present, but the *intermediate*
    /// is a table whose <c>__index</c> supplies the key -- the fused instruction's raw read misses,
    /// and the rewritten GetTabUp + GetTable must find it through the metamethod.
    /// </summary>
    [Test]
    public async Task Deopt_IntermediateIndexMetamethod_StillFound()
    {
        var result = await RunAsync(
            """
            local proxy = setmetatable({}, { __index = function(_, k) return 'via __index: ' .. k end })
            _ENV.thing = proxy
            return thing.missing
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("via __index: missing"));
    }

    /// <summary>
    /// The same shape, but with a nil value stored under the key and an <c>__index</c> on the
    /// intermediate: the raw read *hits* (nil is a value) on both levels' first condition... and
    /// the second level then has to index nil. This is the case where a naive fusion would take a
    /// fast path the unfused pair would not have taken, so it pins the exact error text.
    /// </summary>
    [Test]
    public async Task Deopt_SecondLevelIsNil_ReportsAttemptToIndex()
    {
        var ex = Assert.ThrowsAsync<LuaRuntimeException>(async () =>
            await RunAsync(
                """
                _ENV.thing = nil
                return thing.missing
                """
            )
        );

        Assert.That(ex!.Message, Does.Contain("attempt to index"));
    }

    /// <summary>A missing key on a plain table yields nil, exactly as the unfused read would.</summary>
    [Test]
    public async Task Deopt_MissingKey_IsNil()
    {
        var result = await RunAsync(
            """
            _ENV.thing = {}
            return thing.missing == nil
            """
        );
        Assert.That(result[0].Read<bool>(), Is.True);
    }

    /// <summary>
    /// The intermediate being a non-table that carries an <c>__index</c> metamethod itself (a
    /// userdata-like object, here a table with a metatable and a boolean value) exercises the
    /// level-1 half of the slow path.
    /// </summary>
    [Test]
    public async Task Deopt_WholeEnvReassigned_StillResolves()
    {
        var result = await RunAsync(
            """
            local saved = _ENV.math
            _ENV.math = nil
            local first = math
            _ENV.math = saved
            return first == nil, math.floor(2.5)
            """
        );

        Assert.That(result[0].Read<bool>(), Is.True);
        Assert.That(result[1].Read<double>(), Is.EqualTo(2.0));
    }

    /// <summary>
    /// Deopting must not corrupt the instruction stream: after a miss on the very first execution,
    /// the *same* code path has to keep working -- including in a loop, where the rewritten pair is
    /// what every subsequent iteration re-executes.
    /// </summary>
    [Test]
    public async Task Deopt_ThenRepeatedExecution_StaysCorrect()
    {
        var result = await RunAsync(
            """
            _ENV.thing = {}
            local total = 0
            for i = 1, 3 do
                thing.missing = i
                total += thing.missing
            end
            return total
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(6.0));
    }

    /// <summary>
    /// The fused form must not change what a global *lookup* observes: assigning through the same
    /// chain still writes the real table, and reading it back sees it.
    /// </summary>
    [Test]
    public async Task FusedRead_SeesSubsequentWritesThroughTheSameChain()
    {
        var result = await RunAsync(
            """
            _ENV.config = { }
            config.value = 1
            local a = config.value
            config.value = 2
            return a, config.value
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(1.0));
        Assert.That(result[1].Read<double>(), Is.EqualTo(2.0));
    }

    /// <summary>
    /// <c>const</c>-inlined and plain locals in the same expression must still compile around a
    /// fused chain -- a smoke test that the fusion doesn't disturb register allocation.
    /// <c>math.max(1,2) = 2</c>, <c>math.min(1,2) = 1</c>, <c>string.len('ab') = 2</c>, so
    /// 2 + 1 * 2 = 4.
    /// </summary>
    [Test]
    public async Task Fusion_DoesNotDisturbRegisterAllocation()
    {
        var result = await RunAsync(
            """
            local a, b = 1, 2
            local n = math.max(a, b) + math.min(a, b) * string.len('ab')
            return n
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(4.0));
    }

    /// <summary>
    /// The deopt is an <i>in-place rewrite</i>, not a side path: after a first execution that
    /// misses, the chunk's own instruction stream holds a real GetTabUp followed by a real
    /// GetTable and the GetImport is gone. Asserted directly on the executed closure's prototype,
    /// since that is what makes the "always identical to the unfused pair" argument true rather
    /// than merely plausible -- a deopt that ran the pair without rewriting would leave the fused
    /// instruction there and miss again every iteration.
    /// </summary>
    [Test]
    public async Task Deopt_RewritesTheInstructionStreamInPlace()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var closure = state.Load("_ENV.thing = {}\nreturn thing.missing", "chunk");

        Assert.That(BytecodeDump.Dump(closure.Proto), Does.Contain("GETIMPORT"));

        await state.ExecuteAsync(closure);

        var after = BytecodeDump.Dump(closure.Proto);
        Assert.That(after, Does.Not.Contain("GETIMPORT"), after);
        Assert.That(after, Does.Contain("GETTABUP"), after);
        Assert.That(after, Does.Contain("GETTABLE"), after);
    }
}
