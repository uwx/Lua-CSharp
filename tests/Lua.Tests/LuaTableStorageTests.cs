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

    /// <summary>
    /// The in-game crash reached the storage through <c>table.insert</c> from Luau/Sx, so pin the
    /// same path end to end: TableLibrary.Insert's own bounds check (`pos &lt;= #t + 1`) admits
    /// pos == 8 for a length-7 table, and the storage must then take it.
    /// </summary>
    [Test]
    public async Task Lua_TableInsertIntoLastInlineSlot_DoesNotThrow()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var result = await state.DoStringAsync(
            """
            local t = { 1, 2, 3, 4, 5, 6, 7 }
            table.insert(t, 8, 99)
            return #t, t[8], t[1], t[7]
            """
        );

        Assert.Multiple(() =>
        {
            Assert.That(result[0].TryRead<double>(out var length), Is.True);
            Assert.That(length, Is.EqualTo(8.0));
            Assert.That(result[1].TryRead<double>(out var inserted), Is.True);
            Assert.That(inserted, Is.EqualTo(99.0));
            Assert.That(result[2].TryRead<double>(out var first), Is.True);
            Assert.That(first, Is.EqualTo(1.0));
            Assert.That(result[3].TryRead<double>(out var last), Is.True);
            Assert.That(last, Is.EqualTo(7.0));
        });
    }

    // ------------------------------------------------------------------ insert at the inline boundary

    /// <summary>
    /// `table.insert(t, 8, v)` on a table of length 7 is legal Lua (TableLibrary.Insert allows
    /// `pos == #t + 1`), and the result is a length-8 array - exactly the inline buffer's size. It
    /// must fit without promoting, and must not trip the storage's "never grows past 8" assertion,
    /// which is what happened in-game (an UnreachableException from
    /// LuaSmallArrayTableStorage.EnsureArrayCapacityInPlace via table.insert in an Sx effect).
    /// </summary>
    [Test]
    public void Insert_IntoLastInlineSlot_OfLengthSevenTable_FitsWithoutPromoting()
    {
        var table = new LuaTable(7, 0);
        for (var i = 1; i <= 7; i++)
        {
            table[i] = i;
        }

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.SmallArray));

        Assert.DoesNotThrow(() => table.Insert(8, 99));

        Assert.Multiple(() =>
        {
            Assert.That(table.ArrayLength, Is.EqualTo(8));
            Assert.That(table[8], Is.EqualTo(new LuaValue(99)));
            // every earlier element survived the shift right
            for (var i = 1; i <= 7; i++)
            {
                Assert.That(table[i], Is.EqualTo(new LuaValue(i)));
            }
        });
    }

    /// <summary>Inserting into a *full* inline array has to promote: the shift would push the element
    /// in slot 8 out of the buffer, so it must move to a heap array rather than be dropped.</summary>
    [Test]
    public void Insert_IntoFullInlineArray_PromotesAndKeepsEveryElement()
    {
        var table = new LuaTable(8, 0);
        for (var i = 1; i <= 8; i++)
        {
            table[i] = i;
        }

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.SmallArray));

        table.Insert(3, 99);

        Assert.Multiple(() =>
        {
            Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Array));
            Assert.That(table.ArrayLength, Is.EqualTo(9));
            Assert.That(table[3], Is.EqualTo(new LuaValue(99)));
            Assert.That(table[4], Is.EqualTo(new LuaValue(3)));
            Assert.That(table[8], Is.EqualTo(new LuaValue(7)));
            Assert.That(table[9], Is.EqualTo(new LuaValue(8)));
        });
    }

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
        for (var i = 1; i <= LuaSmallArrayTableStorage.Capacity; i++)
        {
            table[i] = i * 10;
        }

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.SmallArray));

        table[LuaSmallArrayTableStorage.Capacity + 1] = 170;

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Array));
        for (var i = 1; i <= LuaSmallArrayTableStorage.Capacity; i++)
        {
            Assert.That(table[i], Is.EqualTo(new LuaValue(i * 10)), $"key {i}");
        }

        Assert.That(table[LuaSmallArrayTableStorage.Capacity + 1], Is.EqualTo(new LuaValue(170)));
        Assert.That(table.ArrayLength, Is.EqualTo(LuaSmallArrayTableStorage.Capacity + 1));
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
    /// A pure-string table has an empty array part, so its whole walk lives in the string part -- a kind
    /// whose <see cref="ILuaTableStorage.MoveNext"/> no-op'ing or reporting "done" immediately reads
    /// through <see cref="LuaTable.GetEnumerator"/> as an empty table rather than as an error, so pin the
    /// two views (enumerator and <c>next</c>) against each other here.
    /// </summary>
    [Test]
    public void NextOrder_PureStringTable_VisitsEveryEntry()
    {
        var table = new LuaTable(0, 4);
        table["a"] = 1;
        table["b"] = 2;
        table["c"] = 3;

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.String));

        var seen = new HashSet<LuaValue>();
        foreach (var pair in table)
        {
            Assert.That(seen.Add(pair.Key), Is.True, $"duplicate key {pair.Key}");
        }

        Assert.That(seen.Count, Is.EqualTo(3));
        Assert.That(seen, Does.Contain(new LuaValue("a")));
        Assert.That(seen, Does.Contain(new LuaValue("b")));
        Assert.That(seen, Does.Contain(new LuaValue("c")));

        Assert.That(table.TryGetNext(LuaValue.Nil, out var first), Is.True);
        Assert.That(table.TryGetNext(first.Key, out var second), Is.True);
        Assert.That(table.TryGetNext(second.Key, out var third), Is.True);
        Assert.That(third.Key, Is.EqualTo(new LuaValue("c")));
        Assert.That(table.TryGetNext(third.Key, out _), Is.False);
    }

    /// <summary>
    /// A `next` cursor is only ever resumed from after <see cref="LuaTable.SlotStillHolds"/> confirms the
    /// slot it names still holds the control key, and a slot only ever means "that entry of the hash
    /// part the table holds *now*". After a promotion the check is therefore against the promoted
    /// storage's own slots -- which, because a promotion preserves insertion order, are the same ones
    /// (so a cursor that validated before still validates after, and resuming from it lands on the same
    /// successor). What must never happen is an unvalidated slot being read.
    /// </summary>
    [Test]
    public void SlotStillHolds_ValidatesAgainstTheCurrentStoragesSlots()
    {
        var table = new LuaTable(0, 4);
        table["a"] = 1;
        table["b"] = 2;

        // No entry yet at all: the slot an unstarted walk would hand to TryNextFromSlot, and one past
        // the end, both read as a miss.
        Assert.That(table.SlotStillHolds(-1, new LuaValue("b")), Is.False);
        Assert.That(table.SlotStillHolds(9, new LuaValue("b")), Is.False);

        // "b" is the second string entry, so slot 1 holds it -- and holds nothing else.
        Assert.That(table.SlotStillHolds(1, new LuaValue("b")), Is.True);
        Assert.That(table.SlotStillHolds(1, new LuaValue("a")), Is.False);

        // Resuming from the slot of the control key yields the entry *after* it, not the key itself.
        Assert.That(table.TryNextFromSlot(0, out var pair, out var slot), Is.True);
        Assert.That(pair.Key, Is.EqualTo(new LuaValue("b")));
        Assert.That(table.SlotStillHolds(slot, pair.Key), Is.True);

        // Promote String -> Value by writing a non-string key.
        var key = new LuaTable(0, 0);
        table[key] = "obj";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        Assert.That(table.SlotStillHolds(slot, new LuaValue("b")), Is.True);

        // ...and the resumed walk still lands on the entry after it.
        Assert.That(table.TryNextFromSlot(slot, out var pairAfter, out _), Is.True);
        Assert.That(pairAfter.Value, Is.EqualTo(new LuaValue("obj")));
    }

    /// <summary>
    /// Walks <paramref name="table"/> exactly the way the VM's <c>TForCallNext</c> does and checks the
    /// cursor contract its <c>pairs</c> loop runs on: the slot <see cref="LuaTable.TryGetNext"/> reports
    /// for an entry has to be one <see cref="LuaTable.SlotStillHolds"/> accepts for that entry's own key,
    /// and resuming from it has to yield that entry's successor -- a storage reporting a slot for the
    /// wrong entry would make <c>pairs</c> silently skip entries. Asserts the cursor branch was actually
    /// reached, so a walk that never reports a slot (the shape of the bug this pins) cannot pass
    /// vacuously.
    /// </summary>
    static void AssertCursorWalkVisitsEveryEntry(LuaTable table)
    {
        var control = LuaValue.Nil;
        var slot = -1;
        var visited = new List<LuaValue>();
        var tookCursorBranch = false;

        while (true)
        {
            KeyValuePair<LuaValue, LuaValue> pair;
            bool hasPair;
            if (slot >= 0 && table.SlotStillHolds(slot, control))
            {
                tookCursorBranch = true;
                hasPair = table.TryNextFromSlot(slot, out pair, out slot);
            }
            else
            {
                hasPair = table.TryGetNext(control, out pair, out slot);
            }

            if (!hasPair)
            {
                break;
            }

            if (slot >= 0)
            {
                Assert.That(
                    table.SlotStillHolds(slot, pair.Key),
                    Is.True,
                    $"reported slot {slot} does not hold {pair.Key}"
                );
            }

            Assert.That(visited, Does.Not.Contain(pair.Key), $"duplicate key {pair.Key}");
            visited.Add(pair.Key);
            control = pair.Key;
        }

        Assert.That(visited, Has.Count.EqualTo(table.HashMapCount + table.ArrayLength));
        Assert.That(tookCursorBranch, Is.True, "the walk never resumed from a reported slot");
    }

    /// <summary>
    /// The other way into the same walk: <c>next(t, k)</c> for a key the caller already holds -- what a
    /// Lua-level <c>next</c> call and the VM's first step after a cursor miss both do. There is no slot to
    /// resume from, so it has to find <paramref name="control"/>'s successor by hashing it and report a
    /// cursor for <em>that</em> entry, or the walk after it pays for a hash lookup on every step.
    /// </summary>
    static void AssertTryGetNextFromKeyReportsACursor(LuaTable table, LuaValue control)
    {
        Assert.That(table.TryGetNext(control, out var pair, out var slot), Is.True, $"no entry after {control}");
        Assert.That(slot, Is.GreaterThanOrEqualTo(0), $"no cursor reported for the entry after {control}");
        Assert.That(table.SlotStillHolds(slot, pair.Key), Is.True, $"reported slot {slot} does not hold {pair.Key}");
    }

    /// <summary>Every entry of this kind is in a hash slot, so every step has a cursor to hand back.</summary>
    [Test]
    public void TryGetNext_Cursor_StringKind_HandsBackASlotTheWalkResumesFrom()
    {
        var table = new LuaTable(0, 4);
        table["a"] = 1;
        table["b"] = 2;
        table["c"] = 3;

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.String));
        AssertCursorWalkVisitsEveryEntry(table);
        AssertTryGetNextFromKeyReportsACursor(table, new LuaValue("a"));
    }

    /// <summary>The array entry reports no cursor (its own integer key is its position); the two string
    /// entries after it do.</summary>
    [Test]
    public void TryGetNext_Cursor_StringAndArrayKind_HandsBackASlotTheWalkResumesFrom()
    {
        var table = new LuaTable(2, 4);
        table[1] = "x";
        table["a"] = 1;
        table["b"] = 2;

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.StringAndArray));
        AssertCursorWalkVisitsEveryEntry(table);
        AssertTryGetNextFromKeyReportsACursor(table, new LuaValue("a"));
    }

    /// <summary>The terminal kind's dictionary entry, reached by crossing out of the array part.</summary>
    [Test]
    public void TryGetNext_Cursor_ValueKind_HandsBackASlotTheWalkResumesFrom()
    {
        var table = new LuaTable(2, 0);
        table[1] = "x";
        // Far enough past the array window that the write promotes out of SmallArray into the terminal
        // kind instead of growing the array, so this entry lives in the generic dictionary.
        table[100000] = "far";

        Assert.That(KindOf(table), Is.EqualTo(LuaTableStorageKind.Value));
        AssertCursorWalkVisitsEveryEntry(table);
        // From the array key, so the successor is the crossing into the dictionary itself.
        AssertTryGetNextFromKeyReportsACursor(table, new LuaValue(1));
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
