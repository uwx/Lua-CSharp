using System.Globalization;
using Lua.Standard;

namespace Lua.Tests;

/// <summary>
/// Luau syntax support in the NFM-World fork: literals, floor division, compound
/// assignment, continue, const, if-then-else expressions, attributes, string
/// interpolation and generalized iteration.
/// </summary>
public class LuauSyntaxTests
{
    static async Task<LuaValue[]> RunAsync(string source)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        return await state.DoStringAsync(source);
    }

    // ---------------------------------------------------------------- number literals

    [TestCase("1048576", "1_048_576")]
    [TestCase("4294967295", "0xFFFF_FFFF")]
    [TestCase("2748", "0xABC")]
    [TestCase("2748", "0XABC")]
    [TestCase("85", "0b01010101")]
    [TestCase("85", "0B01010101")]
    [TestCase("85", "0b_0101_0101")]
    [TestCase("10", "1_0")]
    [TestCase("1", "1_")]
    [TestCase("10", "1__0")]
    [TestCase("10.5", "1_0.5")]
    [TestCase("1.5", "1.5_0")]
    [TestCase("1.5", "1_.5")]
    [TestCase("1.5", "1._5")]
    [TestCase("1E+10", "1e1_0")]
    [TestCase("255", "0x_FF")]
    [TestCase("10", "0b_1010")]
    public async Task NumberLiteral_Value(string expected, string literal)
    {
        var result = await RunAsync("return " + literal);
        Assert.That(
            result[0].Read<double>(),
            Is.EqualTo(double.Parse(expected, CultureInfo.InvariantCulture))
        );
    }

    [Test]
    public async Task NumberLiteral_IntegerKinds_KeepIntegerType()
    {
        var result = await RunAsync("local a, b, c = 1_000, 0x10, 0b1010 return a, b, c");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Type, Is.EqualTo(LuaValueType.Integer));
            Assert.That(result[1].Type, Is.EqualTo(LuaValueType.Integer));
            Assert.That(result[2].Type, Is.EqualTo(LuaValueType.Integer));
            Assert.That(result[0].Read<long>(), Is.EqualTo(1000L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(16L));
            Assert.That(result[2].Read<long>(), Is.EqualTo(10L));
        });
    }

    static LuaState CreateState()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        return state;
    }

    /// <summary>Asserts that <paramref name="source"/> fails to compile.</summary>
    static void AssertCompileError(string source)
    {
        var state = CreateState();
        Assert.Throws<LuaCompileException>(() => state.DoString(source));
    }

    [TestCase("0b")]
    [TestCase("0x")]
    [TestCase("0b12")]
    [TestCase("0b2")]
    [TestCase("1e")]
    public void NumberLiteral_Malformed_ThrowsCompileError(string literal)
    {
        AssertCompileError("return " + literal);
    }

    // ---------------------------------------------------------------- string escapes

    [Test]
    public async Task StringEscape_Unicode_EncodesUtf16()
    {
        var result = await RunAsync("return \"\\u{41}\", #\"\\u{41}\", #\"\\u{1F600}\"");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("A"));
            Assert.That(result[1].Read<long>(), Is.EqualTo(1L));
            // Lua-CSharp strings are UTF-16, so an astral code point is two code units
            // (Luau's byte strings store the 4-byte UTF-8 sequence instead).
            Assert.That(result[2].Read<long>(), Is.EqualTo(2L));
        });
    }

    [Test]
    public async Task StringEscape_Unicode_AstralCodePoint()
    {
        var result = await RunAsync("return \"\\u{1F600}\"");
        Assert.That(result[0].Read<string>(), Is.EqualTo(char.ConvertFromUtf32(0x1F600)));
    }

    [Test]
    public async Task StringEscape_Unicode_LoneSurrogate_IsAllowed()
    {
        // Luau accepts surrogates (it just encodes them); the UTF-16 model stores the unit.
        var result = await RunAsync("return #\"\\u{D800}\"");
        Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
    }

    [Test]
    public async Task StringEscape_Z_SkipsWhitespaceIncludingNewline()
    {
        var result = await RunAsync("return \"a\\z\n      b\"");
        Assert.That(result[0].Read<string>(), Is.EqualTo("ab"));
    }

    [Test]
    public async Task StringEscape_Hex_StillWorks()
    {
        var result = await RunAsync("return #\"\\x41\"");
        Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
    }

    [TestCase("return \"\\u{}\"")]
    [TestCase("return \"\\u{110000}\"")]
    [TestCase("return \"\\u41\"")]
    [TestCase("return \"\\u{41\"")]
    [TestCase("return \"\\u{123456789}\"")]
    public void StringEscape_Unicode_Malformed_ThrowsCompileError(string source)
    {
        AssertCompileError(source);
    }

    // ---------------------------------------------------------------- floor division ('//')

    [Test]
    public async Task FloorDivision_IntegerOperands_StayInteger()
    {
        var result = await RunAsync("local a, b = 7, 2 return a // b");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Type, Is.EqualTo(LuaValueType.Integer));
            Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
        });
    }

    [Test]
    public async Task FloorDivision_ConstantFolding()
    {
        var result = await RunAsync("return 7 // 2, -7 // 2, 7 // -2, -7.5 // 2");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(-4L));
            Assert.That(result[2].Read<long>(), Is.EqualTo(-4L));
            Assert.That(result[3].Read<double>(), Is.EqualTo(-4d));
        });
    }

    [Test]
    public async Task FloorDivision_RuntimeOperands()
    {
        var result = await RunAsync(
            """
            local a, b, c = 7, 2, -2
            return a // b, a // c, -a // b, 7.5 // b
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(-4L));
            Assert.That(result[2].Read<long>(), Is.EqualTo(-4L));
            Assert.That(result[3].Read<double>(), Is.EqualTo(3d));
        });
    }

    [Test]
    public async Task FloorDivision_ZeroDivisor_YieldsInfinityOrNaN()
    {
        // Luau: `7 // 0` is inf (there is no integer division error).
        var result = await RunAsync("local z = 0 return 7 // z, -7 // z, 0 // z");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<double>(), Is.EqualTo(double.PositiveInfinity));
            Assert.That(result[1].Read<double>(), Is.EqualTo(double.NegativeInfinity));
            Assert.That(result[2].Read<double>(), Is.NaN);
        });
    }

    [Test]
    public async Task FloorDivision_NumericString_IsCoerced()
    {
        var result = await RunAsync("return \"8\" // 2");
        Assert.That(result[0].Read<double>(), Is.EqualTo(4d));
    }

    [Test]
    public void FloorDivision_InvalidOperand_Throws()
    {
        var state = CreateState();
        var exception = Assert.Throws<LuaRuntimeException>(() => state.DoString("return \"x\" // 2"));
        Assert.That(exception!.Message, Does.Contain("idiv"));
    }

    [Test]
    public async Task FloorDivision_Metamethod_IsCalled()
    {
        var result = await RunAsync(
            """
            local v = setmetatable({}, { __idiv = function() return "idiv" end })
            local w = setmetatable({}, { __idiv = function() return "idiv-rev" end })
            return v // 2, 2 // w
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("idiv"));
            Assert.That(result[1].Read<string>(), Is.EqualTo("idiv-rev"));
        });
    }

    [Test]
    public async Task FloorDivision_Precedence()
    {
        var result = await RunAsync("return 1 + 7 // 2, 7 // 2 * 3, 2 ^ 3 // 2");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<double>(), Is.EqualTo(4d));
            Assert.That(result[1].Read<double>(), Is.EqualTo(9d));
            Assert.That(result[2].Read<double>(), Is.EqualTo(4d));
        });
    }

    [Test]
    public async Task FloorDivision_MinValueByMinusOne_UsesFloatPath()
    {
        var result = await RunAsync(
            """
            local a = -9223372036854775807 - 1
            return a // -1
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(9223372036854775808d));
    }

    [Test]
    public async Task FloorDivision_DoesNotBreakCommentsOrConcat()
    {
        var result = await RunAsync("local a = 6 --// comment\nreturn a, 1 .. 2");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(6L));
            Assert.That(result[1].Read<string>(), Is.EqualTo("12"));
        });
    }

    [Test]
    public async Task FloorDivision_HostHelper_MatchesOperator()
    {
        var state = CreateState();

        var quotient = await state.IDivAsync(new LuaValue(7), new LuaValue(2));
        Assert.That(quotient.Read<double>(), Is.EqualTo(3.0));

        // `__idiv` is dispatched for non-numbers, exactly like the opcode does.
        var table = await state.DoStringAsync(
            """
            return setmetatable({}, { __idiv = function() return "idiv" end })
            """
        );
        var viaMetaMethod = await state.IDivAsync(table[0], new LuaValue(1));
        Assert.That(viaMetaMethod.Read<string>(), Is.EqualTo("idiv"));
    }

    // ---------------------------------------------------------------- compound assignment

    [Test]
    public async Task CompoundAssignment_AllOperators()
    {
        var result = await RunAsync(
            """
            local a = 4
            a += 2
            a -= 1
            a *= 3
            a /= 2
            local b = 9
            b //= 2
            local c = 9
            c %= 2
            local d = 3
            d ^= 2
            local s = "x"
            s ..= "y"
            return a, b, c, d, s
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<double>(), Is.EqualTo(7.5d));
            Assert.That(result[1].Read<double>(), Is.EqualTo(4d));
            Assert.That(result[2].Read<double>(), Is.EqualTo(1d));
            Assert.That(result[3].Read<double>(), Is.EqualTo(9d));
            Assert.That(result[4].Read<string>(), Is.EqualTo("xy"));
        });
    }

    [Test]
    public async Task CompoundAssignment_IntegerTarget_StaysInteger()
    {
        var result = await RunAsync("local a = 7 a += 1 local b = 9 b //= 2 return a, b");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Type, Is.EqualTo(LuaValueType.Integer));
            Assert.That(result[0].Read<long>(), Is.EqualTo(8L));
            Assert.That(result[1].Type, Is.EqualTo(LuaValueType.Integer));
            Assert.That(result[1].Read<long>(), Is.EqualTo(4L));
        });
    }

    [Test]
    public async Task CompoundAssignment_IndexedTarget_IsEvaluatedOnce()
    {
        var result = await RunAsync(
            """
            local calls = 0
            local t = { k = 1 }
            local function key()
                calls += 1
                return "k"
            end
            t[key()] += 10
            return calls, t.k
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(11L));
        });
    }

    [Test]
    public async Task CompoundAssignment_IndexedTarget_UsesNewIndexMetamethod()
    {
        var result = await RunAsync(
            """
            local mt = {
                __index = function() return 5 end,
                __newindex = function(t, k, v) rawset(t, "stored", v) end,
            }
            local v = setmetatable({}, mt)
            v.x += 1
            return rawget(v, "stored")
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(6d));
    }

    [Test]
    public async Task CompoundAssignment_NestedIndexTargets()
    {
        var result = await RunAsync(
            """
            local t = { list = { 1, 2, 3 } }
            t.list[2] += 10
            t["list"][3] ..= "!"
            return t.list[2], t.list[3]
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<double>(), Is.EqualTo(12d));
            Assert.That(result[1].Read<string>(), Is.EqualTo("3!"));
        });
    }

    [Test]
    public async Task CompoundAssignment_UpValueTarget()
    {
        var result = await RunAsync(
            """
            local a = 1
            local function bump() a += 2 end
            bump()
            bump()
            return a
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(5d));
    }

    [Test]
    public async Task CompoundAssignment_GlobalTarget()
    {
        var result = await RunAsync("g = 1 g += 41 return g");
        Assert.That(result[0].Read<double>(), Is.EqualTo(42d));
    }

    [Test]
    public async Task CompoundAssignment_RightHandSideIsFullExpression()
    {
        var result = await RunAsync("local a = 1 a += 2 + 3 * 4 return a");
        Assert.That(result[0].Read<double>(), Is.EqualTo(15d));
    }

    [Test]
    public async Task CompoundAssignment_ConcatChainMerges()
    {
        var result = await RunAsync(
            """
            local function f() return "b" end
            local s = "a"
            s ..= f() .. "c"
            return s
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("abc"));
    }

    [Test]
    public async Task CompoundAssignment_TableFieldAndArrayElement()
    {
        var result = await RunAsync(
            """
            local t = { n = 1, 5 }
            local key = "n"
            t[key] += 1
            t[1] *= 3
            return t.n, t[1]
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<double>(), Is.EqualTo(2d));
            Assert.That(result[1].Read<double>(), Is.EqualTo(15d));
        });
    }

    [TestCase("\"x\" ..= \"y\"")]
    [TestCase("local a = 1\nprint(a += 1)")]
    [TestCase("local a, b = 1, 2\na, b += 1")]
    [TestCase("local a = 1\na = a += 1")]
    public void CompoundAssignment_InvalidTarget_ThrowsCompileError(string source)
    {
        AssertCompileError(source);
    }

    // ---------------------------------------------------------------- if-then-else expressions

    [Test]
    public async Task IfExpression_Basic()
    {
        var result = await RunAsync("return if true then 1 else 2, if false then 1 else 2");

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(2L));
        });
    }

    [Test]
    public async Task IfExpression_ElseIfChain()
    {
        var result = await RunAsync(
            """
            local function pick(n)
                return if n == 1 then "one" elseif n == 2 then "two" elseif n == 3 then "three" else "many"
            end
            return pick(1), pick(2), pick(3), pick(9)
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("one"));
            Assert.That(result[1].Read<string>(), Is.EqualTo("two"));
            Assert.That(result[2].Read<string>(), Is.EqualTo("three"));
            Assert.That(result[3].Read<string>(), Is.EqualTo("many"));
        });
    }

    [Test]
    public async Task IfExpression_OnlySelectedBranchIsEvaluated()
    {
        var result = await RunAsync(
            """
            local calls = {}
            local function note(s)
                calls[#calls + 1] = s
                return s
            end
            local v = if true then note("a") else note("b")
            return v, #calls, calls[1]
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("a"));
            Assert.That(result[1].Read<long>(), Is.EqualTo(1L));
        });
    }

    [Test]
    public async Task IfExpression_ValueWithJumps_IsMaterialized()
    {
        // `and`/`or` and comparisons carry jump lists; they must land in the result register.
        var result = await RunAsync(
            """
            local t = {}
            local a = if true then (1 == 1) else false
            local b = if true then (t.missing or "dflt") else "other"
            local c = if false then "x" elseif 2 > 1 then (t.n or 5) else "y"
            return a, b, c
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].ToBoolean(), Is.True);
            Assert.That(result[1].Read<string>(), Is.EqualTo("dflt"));
            Assert.That(result[2].Read<long>(), Is.EqualTo(5L));
        });
    }

    [Test]
    public async Task IfExpression_MultipleResults_KeepsFirstValue()
    {
        var result = await RunAsync(
            """
            local function two() return 1, 2 end
            local v = if true then two() else nil
            return v
            """
        );

        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
    }

    [Test]
    public async Task IfExpression_InExpressionPositions()
    {
        var result = await RunAsync(
            """
            local t = { if true then "k" else "j" }
            local a = 1 + (if true then 10 else 20)
            local b = tostring(if false then 1 else 2)
            local calls = 0
            local function bump()
                calls += 1
                return calls
            end
            local c = if (if bump() == 1 then true else false) then "yes" else "no"
            return a, b, t[1], c
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(11L));
            Assert.That(result[1].Read<string>(), Is.EqualTo("2"));
            Assert.That(result[2].Read<string>(), Is.EqualTo("k"));
            Assert.That(result[3].Read<string>(), Is.EqualTo("yes"));
        });
    }

    [Test]
    public async Task IfExpression_Nested()
    {
        var result = await RunAsync(
            """
            local function grade(n)
                return if n > 90 then "A" elseif n > 80 then (if n > 85 then "B+" else "B") else "C"
            end
            return grade(95), grade(86), grade(81), grade(10)
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("A"));
            Assert.That(result[1].Read<string>(), Is.EqualTo("B+"));
            Assert.That(result[2].Read<string>(), Is.EqualTo("B"));
            Assert.That(result[3].Read<string>(), Is.EqualTo("C"));
        });
    }

    [Test]
    public async Task IfExpression_InLoopBodyAndCondition()
    {
        var result = await RunAsync(
            """
            local total = 0
            for i = 1, 4 do
                total += if i % 2 == 0 then i else 0
            end
            return total
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(6L));
    }

    [Test]
    public async Task IfStatement_IsUnaffected()
    {
        var result = await RunAsync(
            """
            local function classify(n)
                if n < 0 then
                    return "neg"
                elseif n == 0 then
                    return "zero"
                else
                    return "pos"
                end
            end
            return classify(-1), classify(0), classify(1)
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("neg"));
            Assert.That(result[1].Read<string>(), Is.EqualTo("zero"));
            Assert.That(result[2].Read<string>(), Is.EqualTo("pos"));
        });
    }

    [TestCase("local x = if true then 1")]
    [TestCase("local x = if true then 1 elseif false then 2")]
    [TestCase("return if true then 1 elseif false then 2")]
    public void IfExpression_MissingElse_ThrowsCompileError(string source)
    {
        AssertCompileError(source);
    }

    // ---------------------------------------------------------------- continue

    [Test]
    public async Task Continue_ForLoop_SkipsBodyAndAllowsLaterLocals()
    {
        var result = await RunAsync(
            """
            local seen = ""
            for i = 1, 5 do
                if i % 2 == 0 then continue end
                local doubled = i * 2
                seen = seen .. doubled .. ","
            end
            return seen
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("2,6,10,"));
    }

    [Test]
    public async Task Continue_NumericFor_StillAdvancesTheCounter()
    {
        var result = await RunAsync(
            """
            local count = 0
            for i = 1, 3 do
                count += 1
                continue
            end
            return count
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
    }

    [Test]
    public async Task Continue_WhileLoop()
    {
        var result = await RunAsync(
            """
            local n, total = 0, 0
            while n < 5 do
                n += 1
                if n == 3 then continue end
                local contribution = n
                total += contribution
            end
            return total
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(12L));
    }

    [Test]
    public async Task Continue_GenericForLoop()
    {
        var result = await RunAsync(
            """
            local out = ""
            for i, v in ipairs({ 10, 20, 30, 40 }) do
                if i % 2 == 0 then continue end
                out = out .. v .. ","
            end
            return out
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("10,30,"));
    }

    [Test]
    public async Task Continue_OnlyAffectsTheInnermostLoop()
    {
        var result = await RunAsync(
            """
            local seen = {}
            for i = 1, 2 do
                for j = 1, 3 do
                    if j == 2 then continue end
                    seen[#seen + 1] = i .. ":" .. j
                end
            end
            return table.concat(seen, ",")
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("1:1,1:3,2:1,2:3"));
    }

    [Test]
    public async Task Continue_RepeatLoop()
    {
        var result = await RunAsync(
            """
            local n, total = 0, 0
            repeat
                n += 1
                local contribution = n
                if n == 2 then continue end
                total += contribution
            until n >= 4
            return total
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(8L));
    }

    [Test]
    public async Task Continue_RepeatCondition_MayReadLocalsDeclaredBefore()
    {
        var result = await RunAsync(
            """
            local n = 0
            repeat
                local a = 5
                n += 1
                if n == 2 then continue end
            until a > 0 and n >= 3
            return n
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
    }

    [Test]
    public async Task Continue_ClosesUpValuesOpenedByBodyLocals()
    {
        // The closure captured `captured` before the continue; the jump must close it so the
        // next iteration's local does not feed the old closure.
        var result = await RunAsync(
            """
            local fns = {}
            for i = 1, 3 do
                local captured = i * 10
                local f = function() return captured end
                if i == 2 then continue end
                fns[#fns + 1] = f
            end
            return fns[1](), fns[2]()
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(10L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(30L));
        });
    }

    [Test]
    public async Task Continue_ClosuresCapturePerIterationValues()
    {
        var result = await RunAsync(
            """
            local fns = {}
            for i = 1, 3 do
                if i == 2 then continue end
                local captured = i * 10
                fns[#fns + 1] = function() return captured end
            end
            return fns[1](), fns[2]()
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(10L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(30L));
        });
    }

    [Test]
    public async Task Continue_InDoBlockAndBranches()
    {
        var result = await RunAsync(
            """
            local log = ""
            for i = 1, 4 do
                if i == 1 then
                    do continue end
                elseif i == 2 then
                    continue
                end
                log = log .. i .. ","
            end
            return log
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("3,4,"));
    }

    [Test]
    public async Task Continue_IsContextualKeyword()
    {
        var result = await RunAsync(
            """
            local log = {}
            continue = function() log[#log + 1] = "call" end
            continue()
            local continue = 5
            continue = continue + 1
            local obj = { continue = 10 }
            obj.continue = obj.continue + 1
            return #log, continue, obj.continue
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(6L));
            Assert.That(result[2].Read<long>(), Is.EqualTo(11L));
        });
    }

    [TestCase("continue")]
    [TestCase("local function f() continue end")]
    [TestCase("for i = 1, 3 do\n  local f = function() continue end\n  f()\nend")]
    [TestCase("while true do\n  local f = function() continue end\n  break\nend")]
    public void Continue_OutsideItsLoop_ThrowsCompileError(string source)
    {
        AssertCompileError(source);
    }

    [TestCase("repeat\n  do continue end\n  local a = 5\nuntil a > 0")]
    [TestCase("repeat\n  local n = 0\n  if n == 0 then continue end\n  local a = 1\nuntil a > n")]
    public void Continue_RepeatConditionReadingSkippedLocal_ThrowsCompileError(string source)
    {
        var state = CreateState();
        var exception = Assert.Throws<LuaCompileException>(() => state.DoString(source));
        Assert.That(exception!.Message, Does.Contain("continue"));
    }

    // ---------------------------------------------------------------- const bindings

    [Test]
    public async Task Const_BasicForms()
    {
        var result = await RunAsync(
            """
            const a = 5
            const b: number = 6
            const c, d = 7, 8
            const function f() return 42 end
            return a, b, c, d, f()
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(5L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(6L));
            Assert.That(result[2].Read<long>(), Is.EqualTo(7L));
            Assert.That(result[3].Read<long>(), Is.EqualTo(8L));
            Assert.That(result[4].Read<long>(), Is.EqualTo(42L));
        });
    }

    [Test]
    public async Task Const_ValueStaysMutable()
    {
        var result = await RunAsync(
            """
            const t = {}
            t.x = 1
            t.x += 1
            return t.x
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(2L));
    }

    [Test]
    public async Task Const_LiteralInitializers_AreInlinable()
    {
        var result = await RunAsync(
            """
            const N = 5
            const S = "x"
            const B = true
            local function read() return N + 1, S .. "y", B end
            return read()
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(6L));
            Assert.That(result[1].Read<string>(), Is.EqualTo("xy"));
            Assert.That(result[2].ToBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Const_NonLiteralInitializer_KeepsIdentity()
    {
        var result = await RunAsync(
            """
            const t = {}
            local a = t
            local b = t
            const n = 1 + 2
            return a == b, n
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].ToBoolean(), Is.True);
            Assert.That(result[1].Read<long>(), Is.EqualTo(3L));
        });
    }

    [Test]
    public async Task Const_ScopingAndShadowing()
    {
        var result = await RunAsync(
            """
            const a = 1
            local total = a
            do
                const a = 10
                total += a
            end
            total += a
            return total
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(12L));
    }

    [Test]
    public async Task Const_MarkerDoesNotLeakToLaterLocals()
    {
        // The block-local `hidden` is const at index 0; `reused` later reuses that index.
        var result = await RunAsync(
            """
            do
                const hidden = 1
            end
            local reused = 2
            reused = reused + 1
            return reused
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
    }

    [Test]
    public async Task Const_IsContextualKeyword()
    {
        var result = await RunAsync(
            """
            const = 1
            const = const + 1
            local const = 5
            const = const + 1
            local t = { const = 2 }
            t.const += 1
            return const, t.const
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(6L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(3L));
        });
    }

    [Test]
    public async Task Const_WorksAsFunctionParameterName()
    {
        var result = await RunAsync("local function f(const) return const * 2 end return f(21)");
        Assert.That(result[0].Read<long>(), Is.EqualTo(42L));
    }

    [TestCase("const a = 1\na = 2")]
    [TestCase("const a = 1\na += 2")]
    [TestCase("const a = 1\na //= 2")]
    [TestCase("const a = 1\nlocal function f() a = 2 end")]
    [TestCase("const a = 1\nlocal function f() a ..= \"x\" end")]
    [TestCase("const a, b = 1, 2\nb = 3")]
    [TestCase("const a")]
    [TestCase("const a, b")]
    [TestCase("const function f() end\nf = nil")]
    [TestCase("for const a = 1, 3 do end")]
    public void Const_ReassignmentOrMissingInitializer_ThrowsCompileError(string source)
    {
        AssertCompileError(source);
    }

    // ---------------------------------------------------------------- attributes

    [Test]
    public async Task Attributes_AreParsedAndIgnored()
    {
        var result = await RunAsync(
            """
            @native
            local function fib(n: number): number
                if n <= 1 then return n end
                return fib(n - 1) + fib(n - 2)
            end

            @checked
            local function checkedFn() return 1 end

            @[deprecated { use = "newApi()", reason = "old" }]
            local function oldApi() return 3 end

            @[deprecated] @native
            local function grouped() return 4 end

            @[native, checked]
            local function listed() return 5 end

            @native
            function globalFn() return 7 end

            local tbl = {}
            @native
            function tbl.method() return 8 end

            local expr = @native function() return 6 end
            return fib(10), checkedFn(), oldApi(), grouped(), listed(), globalFn(), tbl.method(), expr()
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(55L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(1L));
            Assert.That(result[2].Read<long>(), Is.EqualTo(3L));
            Assert.That(result[3].Read<long>(), Is.EqualTo(4L));
            Assert.That(result[4].Read<long>(), Is.EqualTo(5L));
            Assert.That(result[5].Read<long>(), Is.EqualTo(7L));
            Assert.That(result[6].Read<long>(), Is.EqualTo(8L));
            Assert.That(result[7].Read<long>(), Is.EqualTo(6L));
        });
    }

    [Test]
    public async Task Attributes_OnConstFunction()
    {
        var result = await RunAsync(
            """
            @native
            const function f() return 9 end
            return f()
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(9L));
    }

    [TestCase("@native local x = 5")]
    [TestCase("@native\nlocal x")]
    [TestCase("@[deprecated] local x = 1")]
    [TestCase("@native\ntype T = number")]
    public void Attributes_OnNonFunctionDeclaration_ThrowsCompileError(string source)
    {
        AssertCompileError(source);
    }

    // ---------------------------------------------------------------- generalized iteration

    [Test]
    public async Task GeneralizedIteration_ArrayOrder_IsConsecutive()
    {
        var result = await RunAsync(
            """
            local out = ""
            for i, v in { 10, 20, 30 } do out = out .. i .. ":" .. v .. "," end
            return out
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("1:10,2:20,3:30,"));
    }

    [Test]
    public async Task GeneralizedIteration_TableVisitsEveryEntry()
    {
        var result = await RunAsync(
            """
            local t = { a = 1, b = 2, c = 3 }
            local seen = {}
            for k, v in t do seen[#seen + 1] = k .. v end
            table.sort(seen)
            return table.concat(seen, ",")
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("a1,b2,c3"));
    }

    [Test]
    public async Task GeneralizedIteration_HonoursIterMetamethod()
    {
        var result = await RunAsync(
            """
            local obj = setmetatable({}, {
                __iter = function(self) return next, { z = 9 }, nil end,
            })
            local out = ""
            for k, v in obj do out = out .. k .. "=" .. v end
            return out
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("z=9"));
    }

    [Test]
    public async Task GeneralizedIteration_IterMaySupplyIteratorAndState()
    {
        var result = await RunAsync(
            """
            local function step(state, control)
                if control >= 3 then return nil end
                return control + 1, (control + 1) * 10
            end
            local obj = setmetatable({}, { __iter = function() return step, nil, 0 end })
            local out = ""
            for i, v in obj do out = out .. i .. ":" .. v .. "," end
            return out
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("1:10,2:20,3:30,"));
    }

    [Test]
    public async Task GeneralizedIteration_IterReturningFewerValues_IsPadded()
    {
        var result = await RunAsync(
            """
            local obj = setmetatable({}, {
                __iter = function() return function() return nil end end,
            })
            local n = 0
            for k, v in obj do n += 1 end
            return n
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(0L));
    }

    [Test]
    public async Task GeneralizedIteration_DoesNotConsultIndexMetamethod()
    {
        var result = await RunAsync(
            """
            local obj = setmetatable({}, { __index = { m = 1 } })
            local n = 0
            for k, v in obj do n += 1 end
            return n
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(0L));
    }

    [Test]
    public async Task GeneralizedIteration_PairsAndIpairsKeepClassicBehaviour()
    {
        var result = await RunAsync(
            """
            local out = ""
            for k, v in pairs({ p = 1 }) do out = out .. k .. "=" .. v end
            local sum = 0
            for i, v in ipairs({ 1, 2, 3 }) do sum += v end
            return out, sum
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("p=1"));
            Assert.That(result[1].Read<long>(), Is.EqualTo(6L));
        });
    }

    [Test]
    public async Task GeneralizedIteration_BareFunctionIsItsOwnIterator()
    {
        // `for x in f do` keeps the classic f(state, control) protocol.
        var result = await RunAsync(
            """
            local calls = 0
            local function it(state, control)
                calls += 1
                if control == nil then return 1 end
                return nil
            end
            local n = 0
            for x in it do n += 1 end
            return n, calls
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(1L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(2L));
        });
    }

    [Test]
    public async Task GeneralizedIteration_ExplicitIteratorTripleIsUnchanged()
    {
        var result = await RunAsync(
            """
            local function step(state, control)
                if control >= 2 then return nil end
                return control + 1
            end
            local sum = 0
            for v in step, nil, 0 do sum += v end
            return sum
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(3L));
    }

    [Test]
    public async Task GeneralizedIteration_WithIndexedAndMethodIterables()
    {
        var result = await RunAsync(
            """
            local M = {}
            M.props = { width = 3, height = 4 }
            function M:area()
                local total = 0
                for k, v in self.props do total += v end
                return total
            end
            local sum = 0
            for i, v in { 5, 6 } do sum += v end
            return M:area(), sum
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<long>(), Is.EqualTo(7L));
            Assert.That(result[1].Read<long>(), Is.EqualTo(11L));
        });
    }

    [Test]
    public async Task GeneralizedIteration_WithCallsInsideTheBody()
    {
        var result = await RunAsync(
            """
            local function tag(k) return k .. "!" end
            local total = 0
            for k, v in { x = 1, y = 2, z = 3 } do
                total += #tag(k)
                total += v
            end
            return total
            """
        );

        Assert.That(result[0].Read<long>(), Is.EqualTo(12L));
    }

    [Test]
    public async Task GeneralizedIteration_NonIterable_Throws()
    {
        var state = CreateState();
        var exception = Assert.Throws<LuaRuntimeException>(() => state.DoString("for k, v in 5 do end"));
        Assert.That(exception!.Message, Does.Contain("attempt to iterate over a number value"));
    }

    [Test]
    public async Task GeneralizedIteration_NonIterableString_Throws()
    {
        var state = CreateState();
        var exception = Assert.Throws<LuaRuntimeException>(
            () => state.DoString("for c in \"abc\" do end")
        );
        Assert.That(exception!.Message, Does.Contain("attempt to iterate over a string value"));
    }

    // ---------------------------------------------------------------- string interpolation

    [Test]
    public async Task Interpolation_LiteralOnly()
    {
        var result = await RunAsync("return `hello`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("hello"));
    }

    [Test]
    public async Task Interpolation_EmptyLiteralIsEmptyString()
    {
        var result = await RunAsync("return ``");
        Assert.That(result[0].Read<string>(), Is.EqualTo(""));
    }

    [Test]
    public async Task Interpolation_HolesAreSubstitutedInOrder()
    {
        var result = await RunAsync("return `a{1}b{2}c`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("a1b2c"));
    }

    [Test]
    public async Task Interpolation_AdjacentHolesAndEmptySections()
    {
        var result = await RunAsync("return `{1}{2}{3}`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("123"));
    }

    [Test]
    public async Task Interpolation_ValuesUseTostringRules()
    {
        // Values are converted exactly like `tostring`, including `__tostring`.
        var result = await RunAsync(
            """
            local t = setmetatable({}, { __tostring = function() return "T!" end })
            return `{nil}|{true}|{false}|{1.5}|{2}|{"s"}|{t}`
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("nil|true|false|1.5|2|s|T!"));
    }

    [Test]
    public async Task Interpolation_BareClosingBraceIsLiteralText()
    {
        // Luau only treats '{' as special, so a '}' needs no escape.
        var result = await RunAsync("return `a}b`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("a}b"));
    }

    [Test]
    public async Task Interpolation_EscapedDelimiters()
    {
        var result = await RunAsync(@"return `\{x}` .. `a\`b` .. `\}`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("{x}a`b}"));
    }

    [Test]
    public async Task Interpolation_UnicodeEscapeInLiteralSection()
    {
        // '\u{' must not open a hole.
        var result = await RunAsync(@"return `\u{41}B\u{1F600}`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("AB😀"));
    }

    [Test]
    public async Task Interpolation_PercentIsLiteralText()
    {
        // Unlike `string.format`, a '%' in the literal (or in a hole's value) is not a
        // format specifier.
        var result = await RunAsync("return `100% {1} %s`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("100% 1 %s"));
    }

    [Test]
    public async Task Interpolation_NestedInterpolatedStringInsideHole()
    {
        var result = await RunAsync("return `a{ `b{1}c` }d`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("ab1cd"));
    }

    [Test]
    public async Task Interpolation_NestedTableConstructorInsideHole()
    {
        var result = await RunAsync("return `{( {1, 2} )[2]}`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("2"));
    }

    [Test]
    public async Task Interpolation_HoleWithStringContainingBraces()
    {
        var result = await RunAsync("return `{ \"}\" }{ \"{\" }`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("}{"));
    }

    [Test]
    public async Task Interpolation_HoleExpressionCanBeAnyExpression()
    {
        var result = await RunAsync(
            """
            local n = 3
            local function f() return n * 2 end
            return `{if n > 2 then "big" else "small"}:{f()}:{3 // 2}`
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("big:6:1"));
    }

    [Test]
    public async Task Interpolation_HoleKeepsOnlyFirstResult()
    {
        var result = await RunAsync(
            """
            local function f() return 1, 2 end
            return `{f()}`
            """
        );

        Assert.That(result[0].Read<string>(), Is.EqualTo("1"));
    }

    [Test]
    public async Task Interpolation_ManyHoles()
    {
        var holes = string.Concat(Enumerable.Range(1, 20).Select(i => $"{{{i}}}"));
        var result = await RunAsync($"return `{holes}`");
        Assert.That(result[0].Read<string>(), Is.EqualTo("1234567891011121314151617181920"));
    }

    [Test]
    public async Task Interpolation_InExpressionPositions()
    {
        var result = await RunAsync(
            """
            local function id(s) return s end
            local t = {}
            t[`k{1}`] = "keyed"
            local nested = function() return `n{2}` end
            return id(`a{1}`) .. `b{2}`, t.k1, nested(), (`x{3}`):upper()
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].Read<string>(), Is.EqualTo("a1b2"));
            Assert.That(result[1].Read<string>(), Is.EqualTo("keyed"));
            Assert.That(result[2].Read<string>(), Is.EqualTo("n2"));
            Assert.That(result[3].Read<string>(), Is.EqualTo("X3"));
        });
    }

    [Test]
    public async Task Interpolation_SuffixingNeedsParentheses()
    {
        var result = await RunAsync("return (`{1}abc`):upper()");
        Assert.That(result[0].Read<string>(), Is.EqualTo("1ABC"));
    }

    [TestCase("return `{{x}}`", "Double braces are not permitted")]
    [TestCase("return `{}`", "expected expression inside '{}'")]
    [TestCase("return `a{1}` .. `", "unfinished interpolated string")]
    [TestCase("return `{1}abc`:upper()", "")]
    [TestCase("local f = function() end\nreturn f`x`", "")]
    [TestCase("return `a{1}{2`", "")]
    public void Interpolation_Malformed_ThrowsCompileError(string source, string expectedMessage)
    {
        var state = CreateState();
        var exception = Assert.Throws<LuaCompileException>(() => state.DoString(source));
        if (expectedMessage.Length > 0)
        {
            Assert.That(exception!.Message, Does.Contain(expectedMessage));
        }
    }

    [Test]
    public void Interpolation_NewlineInsideLiteral_ThrowsCompileError()
    {
        var state = CreateState();
        var exception = Assert.Throws<LuaCompileException>(
            () => state.DoString("return `a\nb`")
        );
        Assert.That(exception!.Message, Does.Contain("unfinished interpolated string"));
    }
}
