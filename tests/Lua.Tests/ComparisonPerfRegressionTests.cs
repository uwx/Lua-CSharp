using Lua.Runtime;
using Lua.Standard;

namespace Lua.Tests;

// Regression tests for the interpreter comparison fast paths and the inline
// metamethod cache:
//   - LuaVirtualMachine OP_EQ now follows Lua 5.2 __eq semantics (only same-kind
//     table/userdata operands that carry a metatable may consult a metamethod), so
//     mixed-type and primitive comparisons skip the metamethod probe entirely.
//   - LuaGlobalState.TryGetCachedMetamethod caches (metatable, name) resolutions;
//     the cache is invalidated on setmetatable and on "__"-prefixed table writes.
public class ComparisonPerfRegressionTests
{
    static LuaState CreateState()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        return state;
    }

    [Test]
    public async Task MixedTypeEquality_DoesNotConsultEqMetamethod()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            local t = setmetatable({}, { __eq = function(a, b) return true end })
            return t == 5, t ~= 5, t == nil, t ~= nil
            """
        );

        // Lua 5.2: __eq only applies to same-kind (table/userdata) operands, so
        // mixed-type comparisons must never call the metamethod (previously the fork
        // probed for __eq on any operand type and would have returned true for t == 5).
        Assert.That(result[0].ToBoolean(), Is.False); // t == 5
        Assert.That(result[1].ToBoolean(), Is.True); // t ~= 5
        Assert.That(result[2].ToBoolean(), Is.False); // t == nil
        Assert.That(result[3].ToBoolean(), Is.True); // t ~= nil
    }

    [Test]
    public async Task SameKindSharedMetatable_EqMetamethod_StillFires()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            local mt = { __eq = function(a, b) return a.v == b.v end }
            local a = setmetatable({ v = 1 }, mt)
            local b = setmetatable({ v = 1 }, mt)
            local c = setmetatable({ v = 2 }, mt)
            return a == b, a == c
            """
        );

        Assert.That(result[0].ToBoolean(), Is.True); // a == b (same v, via __eq)
        Assert.That(result[1].ToBoolean(), Is.False); // a == c (different v, via __eq)
    }

    [Test]
    public async Task EqMetamethod_Overwrite_InvalidatesCache()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            local mt = { __eq = function(a, b) return false end }
            local a = setmetatable({}, mt)
            local b = setmetatable({}, mt)
            local r1 = a == b
            mt.__eq = function(a, b) return true end
            local r2 = a == b
            return r1, r2
            """
        );

        Assert.That(result[0].ToBoolean(), Is.False); // cached under the old __eq
        Assert.That(result[1].ToBoolean(), Is.True); // overwrite invalidated the cache
    }

    [Test]
    public async Task Setmetatable_InvalidatesMetamethodCache()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            local mt1 = { __eq = function(a, b) return false end }
            local mt2 = { __eq = function(a, b) return true end }
            local a = setmetatable({}, mt1)
            local b = setmetatable({}, mt1)
            local r1 = a == b
            setmetatable(a, mt2)
            local r2 = a == b
            return r1, r2
            """
        );

        Assert.That(result[0].ToBoolean(), Is.False); // mt1.__eq -> false
        Assert.That(result[1].ToBoolean(), Is.True); // a's new metatable (mt2.__eq) -> true
    }

    [Test]
    public async Task PrimitiveAndDataTable_Inequality_FastPath()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            return 1 ~= 2, "x" ~= "y", true ~= false, 3 == 3, {} ~= {}
            """
        );

        Assert.That(result[0].ToBoolean(), Is.True);
        Assert.That(result[1].ToBoolean(), Is.True);
        Assert.That(result[2].ToBoolean(), Is.True);
        Assert.That(result[3].ToBoolean(), Is.True);
        Assert.That(result[4].ToBoolean(), Is.True); // distinct data tables (no metatable)
    }

    [Test]
    public void LtLe_PlainTables_ThrowsCompareError()
    {
        var state = CreateState();
        var ex = Assert.ThrowsAsync<LuaRuntimeException>(async () =>
            await state.DoStringAsync("local a = {} local b = {} return a < b").AsTask()
        );

        Assert.That(ex!.Message, Does.Contain("attempt to compare"));
    }

    [Test]
    public async Task Pairs_OverEmptyAndSparseTables()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            local function count(t)
                local c = 0
                for _, _ in pairs(t) do c = c + 1 end
                return c
            end
            local empty = {}
            local sparse = { [1] = "a", [5] = "b" }
            return count(empty), count(sparse)
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(0));
        Assert.That(result[1].Read<double>(), Is.EqualTo(2));
    }

    [Test]
    public async Task EqMetamethod_RepeatedComparisons_Consistent()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            local mt = { __eq = function(a, b) return a.k == b.k end }
            local function E(k) return setmetatable({ k = k }, mt) end
            local left = E(1)
            local right = E(2)
            local escape = E(3)
            local total = 0
            for i = 1, 1000 do
                local k = E((i % 3) + 1)
                if k == left then total = total + 1 end
                if k == right then total = total + 1 end
                if k == escape then total = total + 1 end
            end
            return total
            """
        );

        Assert.That(result[0].Read<double>(), Is.EqualTo(1000));
    }

    [Test]
    public async Task TableLength_DenseAndAppend_IsBorder()
    {
        var state = CreateState();
        var result = await state.DoStringAsync(
            """
            local a = {}
            for i = 1, 1000 do a[i] = i end
            local b = {}
            for i = 1, 500 do b[#b + 1] = i end
            local c = {}
            c[3] = 1
            return #a, #b, #c
            """
        );

        // Dense arrays built by indexing or by the `t[#t + 1]` append pattern report
        // their true length in O(1). Sparse `{[3]=1}` has t[1] nil -> border 0.
        Assert.That(result[0].Read<double>(), Is.EqualTo(1000));
        Assert.That(result[1].Read<double>(), Is.EqualTo(500));
        Assert.That(result[2].Read<double>(), Is.EqualTo(0));
    }
}
