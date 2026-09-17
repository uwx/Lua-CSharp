using Lua.Internal;
using Lua.Standard;

namespace Lua.Tests;

/// <summary>
/// Storage-split plan, step 9: regression coverage for the <see cref="ILuaTableStorage"/> promotion
/// graph -- every edge migrates existing entries correctly, and a promotion never loses or duplicates
/// data visible through <see cref="LuaTable"/>'s public surface (the internal storage kind is only
/// inspected here to confirm a promotion actually happened, never as the thing under test).
/// </summary>
public class LuaTableStorageTests
{
    static LuaTableStorageKind KindOf(LuaTable table) => table.DebugStorageKind;

    // ------------------------------------------------------------------ capacity-hint routing

    [Test]
    public void ZeroZero_StartsEmpty()
    {
        var table = new LuaTable(0, 0);
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Empty));
    }

    [Test]
    public void DefaultCtor_StartsEmpty()
    {
        var table = new LuaTable();
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Empty));
    }

    [Test]
    public void SmallArrayCapacityHint_StartsSmallArray()
    {
        var table = new LuaTable(3, 0);
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.SmallArray));
    }

    [Test]
    public void LargeArrayCapacityHint_StartsArray()
    {
        var table = new LuaTable(20, 0);
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Array));
    }

    [Test]
    public void DictionaryOnlyCapacityHint_StartsString()
    {
        var table = new LuaTable(0, 4);
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.String));
    }

    [Test]
    public void MixedCapacityHints_StartStringAndArray()
    {
        var table = new LuaTable(3, 4);
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.StringAndArray));

        var table2 = new LuaTable(20, 4);
        Assert.That(KindOf(table2), Is.EqualTo(LuaTableStorageKind.StringAndArray));
    }

    // ------------------------------------------------------------------ promotion from Empty

    [Test]
    public void Empty_FirstStringWrite_PromotesToString()
    {
        var table = new LuaTable(0, 0);
        table["x"] = 1;
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.String));
        Assert.That(table["x"], Is.EqualTo(new LuaValue(1)));
    }

    [Test]
    public void Empty_FirstSmallArrayWrite_PromotesToSmallArray()
    {
        var table = new LuaTable(0, 0);
        table[1] = "a";
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.SmallArray));
        Assert.That(table[1], Is.EqualTo(new LuaValue("a")));
    }

    [Test]
    public void Empty_FirstGenericWrite_PromotesToValue()
    {
        var table = new LuaTable(0, 0);
        var key = new LuaTable(0, 0);
        table[key] = "v";
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        Assert.That(table[key], Is.EqualTo(new LuaValue("v")));
    }

    // ------------------------------------------------------------------ SmallArray promotion edges

    [Test]
    public void SmallArray_17thElement_SpillsToArray_PreservingExistingEntries()
    {
        var table = new LuaTable(0, 0);
        for (var i = 1; i <= 16; i++)
        {
            table[i] = i * 10;
        }

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.SmallArray));

        table[17] = 170;

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Array));
        for (var i = 1; i <= 16; i++)
        {
            Assert.That(table[i], Is.EqualTo(new LuaValue(i * 10)), $"key {i}");
        }

        Assert.That(table[17], Is.EqualTo(new LuaValue(170)));
        Assert.That(table.ArrayLength, Is.EqualTo(17));
    }

    [Test]
    public void SmallArray_StringKey_SpillsToStringAndArray_PreservingExistingEntries()
    {
        var table = new LuaTable(0, 0);
        table[1] = "a";
        table[2] = "b";

        table["name"] = "value";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.StringAndArray));
        Assert.That(table[1], Is.EqualTo(new LuaValue("a")));
        Assert.That(table[2], Is.EqualTo(new LuaValue("b")));
        Assert.That(table["name"], Is.EqualTo(new LuaValue("value")));
    }

    [Test]
    public void SmallArray_GenericKey_SpillsToValue_PreservingExistingEntries()
    {
        var table = new LuaTable(0, 0);
        table[1] = "a";
        table[2] = "b";

        var key = true;
        table[key] = "flag";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        Assert.That(table[1], Is.EqualTo(new LuaValue("a")));
        Assert.That(table[2], Is.EqualTo(new LuaValue("b")));
        Assert.That(table[key], Is.EqualTo(new LuaValue("flag")));
    }

    [Test]
    public void SmallArray_GetArrayMemory_TriggersReadOnlyPromotionToArray()
    {
        var table = new LuaTable(0, 0);
        table[1] = 1;
        table[2] = 2;
        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.SmallArray));

        var memory = table.GetArrayMemory();

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Array));
        Assert.That(memory.Span[0], Is.EqualTo(new LuaValue(1)));
        Assert.That(memory.Span[1], Is.EqualTo(new LuaValue(2)));
        // GetArraySpan must still agree with the promoted storage.
        Assert.That(table.GetArraySpan()[0], Is.EqualTo(new LuaValue(1)));
        Assert.That(table[1], Is.EqualTo(new LuaValue(1)));
    }

    // ------------------------------------------------------------------ Array promotion edges

    [Test]
    public void Array_StringKey_PromotesToStringAndArray_PreservingArray()
    {
        var table = new LuaTable(20, 0);
        for (var i = 1; i <= 20; i++)
        {
            table[i] = i;
        }

        table["k"] = "v";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.StringAndArray));
        for (var i = 1; i <= 20; i++)
        {
            Assert.That(table[i], Is.EqualTo(new LuaValue(i)), $"key {i}");
        }

        Assert.That(table["k"], Is.EqualTo(new LuaValue("v")));
    }

    [Test]
    public void Array_GenericKey_PromotesToValue_PreservingArray()
    {
        var table = new LuaTable(20, 0);
        for (var i = 1; i <= 20; i++)
        {
            table[i] = i;
        }

        var key = 1.5;
        table[key] = "v";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        for (var i = 1; i <= 20; i++)
        {
            Assert.That(table[i], Is.EqualTo(new LuaValue(i)), $"key {i}");
        }

        Assert.That(table[key], Is.EqualTo(new LuaValue("v")));
    }

    // ------------------------------------------------------------------ String promotion edges

    [Test]
    public void String_ArrayRangeKey_PromotesToStringAndArray_PreservingStrings()
    {
        var table = new LuaTable(0, 4);
        table["a"] = 1;
        table["b"] = 2;

        table[1] = "x";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.StringAndArray));
        Assert.That(table["a"], Is.EqualTo(new LuaValue(1)));
        Assert.That(table["b"], Is.EqualTo(new LuaValue(2)));
        Assert.That(table[1], Is.EqualTo(new LuaValue("x")));
    }

    [Test]
    public void String_GenericKey_PromotesToValue_MigratingStrings()
    {
        var table = new LuaTable(0, 4);
        table["a"] = 1;
        table["b"] = 2;

        var key = new LuaTable(0, 0);
        table[key] = "obj";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        Assert.That(table["a"], Is.EqualTo(new LuaValue(1)));
        Assert.That(table["b"], Is.EqualTo(new LuaValue(2)));
        Assert.That(table[key], Is.EqualTo(new LuaValue("obj")));
    }

    // ------------------------------------------------------------------ StringAndArray promotion edge

    [Test]
    public void StringAndArray_GenericKey_PromotesToValue_PreservingBothParts()
    {
        var table = new LuaTable(3, 4);
        table[1] = "a";
        table[2] = "b";
        table["x"] = 10;
        table["y"] = 20;

        var key = false;
        table[key] = "flag";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        Assert.That(table[1], Is.EqualTo(new LuaValue("a")));
        Assert.That(table[2], Is.EqualTo(new LuaValue("b")));
        Assert.That(table["x"], Is.EqualTo(new LuaValue(10)));
        Assert.That(table["y"], Is.EqualTo(new LuaValue(20)));
        Assert.That(table[key], Is.EqualTo(new LuaValue("flag")));
    }

    // ------------------------------------------------------------------ next()/pairs() across a promotion

    [Test]
    public void NextOrder_PreservedAcrossSmallArrayToStringAndArrayPromotion()
    {
        var table = new LuaTable(0, 0);
        table[1] = "a";
        table[2] = "b";
        table["k1"] = 1;
        table["k2"] = 2;

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.StringAndArray));

        var seen = new HashSet<LuaValue>();
        foreach (var pair in table)
        {
            Assert.That(seen.Add(pair.Key), Is.True, $"duplicate key {pair.Key}");
        }

        Assert.That(seen.Count, Is.EqualTo(4));
        Assert.That(seen, Does.Contain(new LuaValue(1)));
        Assert.That(seen, Does.Contain(new LuaValue(2)));
        Assert.That(seen, Does.Contain(new LuaValue("k1")));
        Assert.That(seen, Does.Contain(new LuaValue("k2")));
    }

    /// <summary>
    /// After a String-part table promotes to LuaValueTableStorage mid-iteration, a `next` cursor
    /// still holding the old (now-stale) string-part slot must not be trusted:
    /// <see cref="LuaTable.SlotStillHolds"/> has to report false so the VM's TForCallNext falls back
    /// to re-hashing the key instead of reading a slot that belongs to a different storage instance.
    /// </summary>
    [Test]
    public void SlotStillHolds_IsFalseAfterPromotionAwayFromFastStringPart()
    {
        var table = new LuaTable(0, 4);
        table["a"] = 1;
        table["b"] = 2;

        Assert.That(table.TryGetNextFromString("a", out var pair, out var slot), Is.True);
        Assert.That(pair.Key, Is.EqualTo(new LuaValue("b")));
        Assert.That(table.SlotStillHolds(slot, "b"), Is.True);

        // Promote String -> Value by writing a non-string key.
        var key = new LuaTable(0, 0);
        table[key] = "obj";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        Assert.That(table.SlotStillHolds(slot, "b"), Is.False);

        // The re-hash fallback still finds the right entries.
        Assert.That(table.TryGetNextFromString("a", out var pairAfter, out _), Is.True);
        Assert.That(pairAfter.Key, Is.EqualTo(new LuaValue("b")));
    }

    /// <summary>A promotion triggered from inside a Lua-level `pairs` loop must not corrupt the
    /// iteration or lose keys that existed before the promotion.</summary>
    [Test]
    public async Task MidPairsIteration_Promotion_DoesNotCorruptIteration()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var result = await state.DoStringAsync(
            """
            local t = {}
            t.a = 1
            t.b = 2
            t.c = 3

            local seen = {}
            local count = 0
            for k, v in pairs(t) do
                count = count + 1
                seen[k] = v
                if k == "b" then
                    -- Non-string key write promotes String -> Value mid-iteration.
                    t[true] = "flag"
                end
            end

            return count, seen.a, seen.b, seen.c, t[true]
            """
        );

        // At least the three original string entries must have been visited once each
        // (order across the promotion boundary is not pinned down further than that).
        Assert.That(result[0].Read<long>(), Is.GreaterThanOrEqualTo(3L));
        Assert.That(result[1].Read<long>(), Is.EqualTo(1L));
        Assert.That(result[2].Read<long>(), Is.EqualTo(2L));
        Assert.That(result[3].Read<long>(), Is.EqualTo(3L));
        Assert.That(result[4].Read<string>(), Is.EqualTo("flag"));
    }
}
