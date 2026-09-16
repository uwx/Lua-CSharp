using Lua.CodeAnalysis.Compilation.Ast;
using Lua.Runtime;
using Lua.Standard;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Compiler-rewrite plan Milestone 4: <see cref="OpCode.DupTable"/>, the fused all-constant table
/// constructor. As with <see cref="GetImportTests"/>, two separate properties have to hold:
///
/// <list type="number">
/// <item><b>Shape</b> -- the fusion fires on exactly the constructors it should and on nothing
/// else. The fall-back cases carry as much weight as the firing ones: a constructor that is not
/// all-constant has to take the proven per-field path unchanged, and the disassembly shows that
/// directly.</item>
/// <item><b>Indistinguishability</b> -- a copy of the template has to be what the unfused
/// <c>NewTable</c> plus per-field writes would have built: same values, same capacities, same
/// iteration order, and above all <em>one table per evaluation</em> rather than a single table
/// aliased into every copy.</item>
/// </list>
/// </summary>
public class DupTableTests
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
        Assert.That(dump, Does.Contain("DUPTABLE"), $"expected a fused DUPTABLE in:\n{dump}");
    }

    static void NotEmitted(string source)
    {
        var dump = Disassemble(source);
        Assert.That(dump, Does.Not.Contain("DUPTABLE"), $"expected no fused DUPTABLE in:\n{dump}");
    }

    // ------------------------------------------------------------------ shape: fuses

    [Test]
    public void ConstantHashLiteral_Fuses()
    {
        Emitted("local t = { x = 1, y = 2 }\nreturn t");
    }

    [Test]
    public void ConstantArrayLiteral_Fuses()
    {
        Emitted("local t = { 1, 2, 3 }\nreturn t");
    }

    [Test]
    public void MixedLiteral_Fuses()
    {
        Emitted("local t = { 1, 2, x = 3, y = 4 }\nreturn t");
    }

    /// <summary>The confirmed-real pattern this optimization exists for (devtools.luau).</summary>
    [Test]
    public void StyleTablePattern_Fuses()
    {
        Emitted(
            """
            local style = { display = 'flex', inset = 0, gap = 8, cornerRadius = 4 }
            return style
            """
        );
    }

    /// <summary>
    /// The fused instruction is the two-word pair: a DupTable whose A is unbound (0 here, bound
    /// later by the consumer) followed by an ExtraArg carrying the template's index. Nothing of the
    /// per-field path survives -- no NewTable, no SetTable, and no LoadK for the values, which are
    /// not in the constant pool at all on this path.
    /// </summary>
    [Test]
    public void FusedInstruction_IsTwoWordsWithATemplateIndex()
    {
        var dump = Disassemble("local t = { x = 1, y = 2 }\nreturn t");

        var lines = dump.Split('\n');
        var pc = Array.FindIndex(lines, l => l.Contains("DUPTABLE"));
        Assert.That(pc, Is.GreaterThanOrEqualTo(0), dump);

        Assert.That(lines[pc], Does.Contain("DUPTABLE 0"), dump);
        Assert.That(lines[pc + 1], Does.Contain("EXTRAARG 0"), dump);
        Assert.That(lines[pc + 2], Does.Not.Contain("EXTRAARG"), dump);

        Assert.That(dump, Does.Not.Contain("NEWTABLE"), dump);
        Assert.That(dump, Does.Not.Contain("SETTABLE"), dump);
    }

    /// <summary>
    /// Each prototype has its own template pool, so a literal in a nested function is indexed from
    /// 0 in that function and the instruction fetches it relative to the executing closure's own
    /// prototype.
    /// </summary>
    [Test]
    public void LiteralInNestedFunction_FusesInItsOwnPrototype()
    {
        var dump = Disassemble("local function f() return { x = 1, y = 2 } end\nreturn f");
        Assert.That(dump, Does.Contain("DUPTABLE"), dump);
        Assert.That(dump, Does.Contain("EXTRAARG 0"), dump);
    }

    /// <summary>Two literals in one prototype get two slots, in emission order.</summary>
    [Test]
    public void TwoLiterals_GetTwoTemplateSlots()
    {
        var dump = Disassemble("local a = { x = 1, y = 2 }\nlocal b = { p = 3, q = 4 }\nreturn a, b");
        Assert.That(dump, Does.Contain("EXTRAARG 0"), dump);
        Assert.That(dump, Does.Contain("EXTRAARG 1"), dump);
    }

    // ------------------------------------------------------------------ shape: does not fuse

    /// <summary>
    /// One DupTable is two words, so a two-field literal is already a win and a one-field one (a
    /// NewTable plus one SetTable) is not -- it would be a wash on instruction count and a loss on
    /// work, since a copy costs more than one insert.
    /// </summary>
    [Test]
    public void SingleFieldLiteral_DoesNotFuse()
    {
        NotEmitted("local t = { x = 1 }\nreturn t");
    }

    [Test]
    public void EmptyLiteral_DoesNotFuse()
    {
        NotEmitted("local t = {}\nreturn t");
    }

    /// <summary>A computed key is not a compile-time constant, so there is no template to build.</summary>
    [Test]
    public void ComputedKey_DoesNotFuse()
    {
        NotEmitted("local k = 'x'\nlocal t = { y = 1, [k] = 2 }\nreturn t");
    }

    /// <summary>
    /// A foldable but non-string key takes the proven path: an integer key interacts with the array
    /// part through GrowArray's migration, which is exactly the kind of interaction this milestone
    /// should not be betting on for a shape no real code uses.
    /// </summary>
    [Test]
    public void IntegerKey_DoesNotFuse()
    {
        NotEmitted("local t = { y = 1, [1] = 'a' }\nreturn t");
    }

    /// <summary>A non-constant value has no value to put in the template.</summary>
    [Test]
    public void CallValue_DoesNotFuse()
    {
        NotEmitted("local f = tostring\nlocal t = { x = f(1), y = 2 }\nreturn t");
    }

    /// <summary>
    /// A name is not a literal, even when it is a <c>const</c> whose initializer would inline to
    /// one: resolving that needs an authoritative name resolution, which cannot be run speculatively
    /// from a fold check (see the plan's Milestone 4 design note on the deferred M4b increment).
    /// </summary>
    [Test]
    public void ConstNameValue_DoesNotFuse()
    {
        NotEmitted("const v = 1\nlocal t = { x = v, y = 2 }\nreturn t");
    }

    [Test]
    public void LocalNameValue_DoesNotFuse()
    {
        NotEmitted("local v = 1\nlocal t = { x = v, y = 2 }\nreturn t");
    }

    /// <summary>
    /// The aliasing hazard, and the reason a table-valued field never qualifies: a folded nested
    /// literal would be one shared table inside the template, so every copy would hold the *same*
    /// inner table.
    /// </summary>
    [Test]
    public void TableValuedField_DoesNotFuse()
    {
        NotEmitted("local t = { x = {}, y = 2 }\nreturn t");
    }

    /// <summary>A trailing multi-return call is the SetList MultipleReturns case, not a constant.</summary>
    [Test]
    public void TrailingMultiReturn_DoesNotFuse()
    {
        NotEmitted("local f = tostring\nlocal t = { 1, f() }\nreturn t");
    }

    // ------------------------------------------------------------------ behavior

    /// <summary>Every literal type, plus a nil-valued field, in one fused constructor.</summary>
    [Test]
    public async Task FusedLiteral_CarriesEveryValueType()
    {
        var result = await RunAsync(
            """
            local t = { n = 1, f = 1.5, s = "hi", b = true, z = nil, -3, -2.5 }
            return t.n, t.f, t.s, t.b, t.z == nil, t[1], t[2]
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[1].Read<double>(), Is.EqualTo(1.5));
        Assert.That(result[2].Read<string>(), Is.EqualTo("hi"));
        Assert.That(result[3].Read<bool>(), Is.True);
        Assert.That(result[4].Read<bool>(), Is.True);
        Assert.That(result[5].Read<long>(), Is.EqualTo(-3L));
        Assert.That(result[6].Read<double>(), Is.EqualTo(-2.5));
    }

    /// <summary>
    /// Mixed array/hash fields keep their positions: the array part is the unkeyed fields in source
    /// order, and a keyed field between two array fields does not disturb it.
    /// </summary>
    [Test]
    public async Task MixedLiteral_ArrayAndHashCountsAreCorrect()
    {
        var result = await RunAsync(
            "local t = {1, 2, x = 3, 4, y = 5}\nreturn #t, t[1], t[2], t[3], t.x, t.y"
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
        Assert.That(result[1].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[2].Read<long>(), Is.EqualTo(2L));
        Assert.That(result[3].Read<long>(), Is.EqualTo(4L));
        Assert.That(result[4].Read<long>(), Is.EqualTo(3L));
        Assert.That(result[5].Read<long>(), Is.EqualTo(5L));
    }

    /// <summary>
    /// A hole is a slot, not a gap: the array part is written whole, so position 3 stays an explicit
    /// nil and position 4 keeps its value.
    /// </summary>
    [Test]
    public async Task ArrayHole_IsPreserved()
    {
        var result = await RunAsync("local t = { 1, 2, nil, 4 }\nreturn t[1], t[2], t[3] == nil, t[4]");
        Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[1].Read<long>(), Is.EqualTo(2L));
        Assert.That(result[2].Read<bool>(), Is.True);
        Assert.That(result[3].Read<long>(), Is.EqualTo(4L));
    }

    /// <summary>
    /// Past <c>ListItemsPerFlush</c> the unfused path batches its array writes through several
    /// SetLists; the template is written in one pass instead, so this pins that the result is the
    /// same table.
    /// </summary>
    [Test]
    public async Task LongArrayLiteral_IsCorrect()
    {
        var fields = string.Join(", ", Enumerable.Range(1, 60));
        var result = await RunAsync(
            $"local t = {{{fields}}}\nreturn #t, t[1], t[50], t[60], t[61] == nil"
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(60L));
        Assert.That(result[1].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[2].Read<long>(), Is.EqualTo(50L));
        Assert.That(result[3].Read<long>(), Is.EqualTo(60L));
        Assert.That(result[4].Read<bool>(), Is.True);
    }

    /// <summary>
    /// The whole point: the template is copied, not handed out. Three evaluations of one literal
    /// produce three tables, and mutating one leaves the others -- and the template they came from,
    /// which no Lua code can reach -- untouched.
    /// </summary>
    [Test]
    public async Task RepeatedEvaluation_ProducesIndependentTables()
    {
        var result = await RunAsync(
            """
            local ts = {}
            for i = 1, 3 do ts[i] = { x = 1, y = 2 } end
            ts[1].x = 99
            ts[2].z = 'added'
            return ts[1].x, ts[2].x, ts[3].x, ts[3].z == nil, ts[2].z
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(99L));
        Assert.That(result[1].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[2].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[3].Read<bool>(), Is.True);
        Assert.That(result[4].Read<string>(), Is.EqualTo("added"));
    }

    /// <summary>
    /// A copy has no metatable (the template has none either), so a field merely *named* like a
    /// metamethod is an ordinary entry and does nothing -- and a metatable set on a copy afterwards
    /// behaves normally, since the copy is an ordinary table.
    /// </summary>
    [Test]
    public async Task Copy_HasNoMetatableButAcceptsOne()
    {
        var result = await RunAsync(
            """
            local t = { __index = 5, x = 1 }
            local noMeta, missing = getmetatable(t) == nil, t.missing == nil
            setmetatable(t, { __index = function(_, k) return 'via __index: ' .. k end })
            return noMeta, missing, t.hello, t.__index
            """
        );

        Assert.That(result[0].Read<bool>(), Is.True);
        Assert.That(result[1].Read<bool>(), Is.True);
        Assert.That(result[2].Read<string>(), Is.EqualTo("via __index: hello"));
        Assert.That(result[3].Read<long>(), Is.EqualTo(5L));
    }

    /// <summary>
    /// Iteration order is insertion order, and a copy's insertion order is the literal's own field
    /// order -- the same order a hand-built equivalent table would have.
    /// </summary>
    [Test]
    public async Task Copy_IteratesInSourceOrder()
    {
        var result = await RunAsync(
            """
            local fused = { a = 1, b = 2, c = 3 }
            local built = {}
            built.a = 1 built.b = 2 built.c = 3
            local order = {}
            for k in pairs(fused) do order[#order + 1] = k end
            local builtOrder = {}
            for k in pairs(built) do builtOrder[#builtOrder + 1] = k end
            return table.concat(order, ","), table.concat(builtOrder, ",")
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("a,b,c"));
        Assert.That(result[1].Read<string>(), Is.EqualTo("a,b,c"));
    }

    /// <summary>
    /// A nil-valued field is a *dead* entry in the unfused path (assigned, so kept for a
    /// <c>next</c> call that was handed the key, but skipped by <c>pairs</c>); the template replay
    /// goes through the same indexer, so the copy has the same dead entry.
    /// </summary>
    [Test]
    public async Task NilValuedField_IsADeadEntry()
    {
        var result = await RunAsync(
            """
            local t = { z = nil, y = 1 }
            local count = 0
            for _ in pairs(t) do count = count + 1 end
            return count, t.z == nil, next(t) ~= nil
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[1].Read<bool>(), Is.True);
        Assert.That(result[2].Read<bool>(), Is.True);
    }

    /// <summary>
    /// A fused literal used as a value in a larger expression: the result register is bound by the
    /// consumer exactly as NewTable's was, so `t` is a table and not a stray value in the register
    /// the instruction wrote.
    /// </summary>
    [Test]
    public async Task FusedLiteral_InAValuePosition()
    {
        var result = await RunAsync(
            """
            local f = function(a, b) return a.x + b.y end
            local sum = f({ x = 1, y = 2 }, { x = 10, y = 20 })
            local ok = type({ a = 1, b = 2 }) == 'table'
            return sum, ok
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(21L));
        Assert.That(result[1].Read<bool>(), Is.True);
    }
}
