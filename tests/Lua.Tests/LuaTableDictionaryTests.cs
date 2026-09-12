using Lua.Internal;
using Lua.Standard;

namespace Lua.Tests;

/// <summary>
/// Guards the semantics of <see cref="LuaTable"/>'s hash part (the embedded string
/// dictionary plus the lazily created generic dictionary).
///
/// These are deliberately written against the public <see cref="LuaTable"/> surface so
/// they stay valid while the dictionaries' internals are replaced: they encode the
/// invariants the storage design has to preserve rather than any particular layout.
/// </summary>
public class LuaTableDictionaryTests
{
    static int CountPairs(LuaTable table)
    {
        var n = 0;
        foreach (var _ in table)
        {
            n++;
        }

        return n;
    }

    static void AssertPairsVisit(LuaTable table, int expectedDistinctKeys)
    {
        var seen = new HashSet<LuaValue>();
        foreach (var pair in table)
        {
            Assert.That(seen.Add(pair.Key), Is.True, $"duplicate key {pair.Key} in pairs()");
            Assert.That(pair.Value.Type, Is.Not.EqualTo(LuaValueType.Nil));
        }

        Assert.That(seen.Count, Is.EqualTo(expectedDistinctKeys));
    }

    [Test]
    public void ManyStringKeys_GrowingAcrossManyResizes_AllReadable()
    {
        const int n = 3000;
        var table = new LuaTable(0, 0);
        for (var i = 0; i < n; i++)
        {
            table["k" + i] = i;
        }

        for (var i = 0; i < n; i++)
        {
            Assert.That(table["k" + i], Is.EqualTo(new LuaValue(i)));
            Assert.That(table.TryGetValue("k" + i, out _), Is.True);
        }

        Assert.That(table.HashMapCount, Is.EqualTo(n));
        AssertPairsVisit(table, n);
    }

    [Test]
    public void ManyStringKeys_WithCapacityHint_DoesNotLoseEntries()
    {
        const int n = 512;
        var table = new LuaTable(0, n);
        for (var i = 0; i < n; i++)
        {
            table["k" + i] = i;
        }

        Assert.That(table.HashMapCount, Is.EqualTo(n));
        AssertPairsVisit(table, n);
    }

    [Test]
    public void SparseIntegerKeys_AllReadable()
    {
        const int n = 1000;
        // The offset has to clear Math.Max(arrayLength * 2, 8) or key 100 would be an
        // array candidate instead of a hash one.
        const int offset = 100;
        var table = new LuaTable(0, 0);
        for (var i = 0; i < n; i++)
        {
            table[(i * 8192) + offset] = i;
        }

        for (var i = 0; i < n; i++)
        {
            Assert.That(table[(i * 8192) + offset], Is.EqualTo(new LuaValue(i)), $"key {i}");
            Assert.That(table[(i * 8192) + 3].Type, Is.EqualTo(LuaValueType.Nil));
        }

        Assert.That(table.HashMapCount, Is.EqualTo(n));
        AssertPairsVisit(table, n);
    }

    [Test]
    public void NegativeAndFractionalKeys_RoundTrip()
    {
        var table = new LuaTable(0, 0);
        table[-1] = "neg";
        table[-9000] = "far-neg";
        table[1.5] = "frac";
        table[100000.25] = "sparse-frac";
        table[true] = "yes";
        table[false] = "no";
        table["s"] = "string";

        Assert.That(table[-1], Is.EqualTo(new LuaValue("neg")));
        Assert.That(table[-9000], Is.EqualTo(new LuaValue("far-neg")));
        Assert.That(table[1.5], Is.EqualTo(new LuaValue("frac")));
        Assert.That(table[100000.25], Is.EqualTo(new LuaValue("sparse-frac")));
        Assert.That(table[true], Is.EqualTo(new LuaValue("yes")));
        Assert.That(table[false], Is.EqualTo(new LuaValue("no")));
        Assert.That(table["s"], Is.EqualTo(new LuaValue("string")));

        Assert.That(table.HashMapCount, Is.EqualTo(7));
        AssertPairsVisit(table, 7);
    }

    [Test]
    public void BooleanAndNumberKeys_AreNotUnified()
    {
        // Boolean stores 0/1 in the same slot as Number, so their hashes collide.
        // They must still be distinct keys.
        var table = new LuaTable(0, 0);
        table[true] = "bool";
        table[1] = "int";

        Assert.That(table[true], Is.EqualTo(new LuaValue("bool")));
        Assert.That(table[1], Is.EqualTo(new LuaValue("int")));
        Assert.That(table[1.0], Is.EqualTo(new LuaValue("int")));
    }

    [Test]
    public void IntegerAndFloatKeys_AreTheSameKey_InHashPart()
    {
        // 100000 is beyond MaxDistance of an empty array part, so it lives in the
        // generic dictionary -- the cross-type Integer/Number equality has to apply
        // there, not just in the array part.
        var table = new LuaTable(0, 0);
        table[100000] = "int";
        table[100000.0] = "float";

        Assert.That(table[100000], Is.EqualTo(new LuaValue("float")));
        Assert.That(table[100000.0], Is.EqualTo(new LuaValue("float")));
        Assert.That(table.HashMapCount, Is.EqualTo(1));
        AssertPairsVisit(table, 1);
    }

    [Test]
    public void IntegerAndFloatKeys_AreTheSameKey_InArrayPart()
    {
        var table = new LuaTable(0, 0);
        table[1] = "int";
        table[1.0] = "float";

        Assert.That(table[1], Is.EqualTo(new LuaValue("float")));
        Assert.That(table[1.0], Is.EqualTo(new LuaValue("float")));
        Assert.That(table.ArrayLength, Is.EqualTo(1));
        AssertPairsVisit(table, 1);
    }

    [Test]
    public void NilAssignment_LeavesNothingIterable_AndIsResettable()
    {
        var table = new LuaTable(0, 0);
        table["x"] = 1;
        table[100000] = 2;
        table[true] = 3;

        table["x"] = LuaValue.Nil;
        table[100000] = LuaValue.Nil;
        table[true] = LuaValue.Nil;

        Assert.That(table["x"].Type, Is.EqualTo(LuaValueType.Nil));
        Assert.That(table[100000].Type, Is.EqualTo(LuaValueType.Nil));
        Assert.That(table[true].Type, Is.EqualTo(LuaValueType.Nil));
        Assert.That(table.TryGetValue("x", out _), Is.False);
        Assert.That(table.ContainsKey(100000), Is.False);
        Assert.That(table.HashMapCount, Is.EqualTo(0));
        AssertPairsVisit(table, 0);

        // The dead entries must be reusable.
        table["x"] = 10;
        table[100000] = 20;
        table[true] = 30;

        Assert.That(table["x"], Is.EqualTo(new LuaValue(10)));
        Assert.That(table[100000], Is.EqualTo(new LuaValue(20)));
        Assert.That(table[true], Is.EqualTo(new LuaValue(30)));
        Assert.That(table.HashMapCount, Is.EqualTo(3));
        AssertPairsVisit(table, 3);
    }

    [Test]
    public void NilOverwriteOfLiveEntry_KeepsHashMapCountExact()
    {
        var table = new LuaTable(0, 0);
        for (var i = 0; i < 64; i++)
        {
            table["k" + i] = i;
        }

        for (var i = 0; i < 64; i += 2)
        {
            table["k" + i] = LuaValue.Nil;
        }

        Assert.That(table.HashMapCount, Is.EqualTo(32));
        AssertPairsVisit(table, 32);
    }

    [Test]
    public void Clear_ReleasesEntries_AndTableIsReusable()
    {
        const int n = 700;
        const int sparseOffset = 100; // clear of the array-candidate window
        var table = new LuaTable(0, 0);
        for (var i = 0; i < n; i++)
        {
            table["k" + i] = i;
            table[(i * 8192) + sparseOffset] = i;
        }

        table.Clear();

        Assert.That(table.HashMapCount, Is.EqualTo(0));
        Assert.That(table.ArrayLength, Is.EqualTo(0));
        AssertPairsVisit(table, 0);
        Assert.That(table["k0"].Type, Is.EqualTo(LuaValueType.Nil));

        // Refill after Clear. This is also the path that exercises the empty-bucket
        // scan fallback on a bucket array that has been filled once already.
        for (var i = 0; i < n; i++)
        {
            table["k" + i] = i * 2;
            table[(i * 8192) + sparseOffset] = i * 3;
        }

        for (var i = 0; i < n; i++)
        {
            Assert.That(table["k" + i], Is.EqualTo(new LuaValue(i * 2)), $"string {i}");
            Assert.That(
                table[(i * 8192) + sparseOffset],
                Is.EqualTo(new LuaValue(i * 3)),
                $"int {i}"
            );
        }

        Assert.That(table.HashMapCount, Is.EqualTo(n * 2));
        AssertPairsVisit(table, n * 2);
    }

    [Test]
    public void ClearThenInsertMany_Repeatedly_StaysConsistent()
    {
        var table = new LuaTable(0, 0);
        for (var round = 0; round < 8; round++)
        {
            for (var i = 0; i < 200; i++)
            {
                table["r" + round + "_" + i] = i;
            }
        }

        for (var round = 0; round < 8; round++)
        {
            table.Clear();
            for (var i = 0; i < 200; i++)
            {
                table["r" + round + "_" + i] = i;
            }

            Assert.That(table.HashMapCount, Is.EqualTo(200), $"round {round}");
            AssertPairsVisit(table, 200);
        }
    }

    [Test]
    public void ArrayGrowth_PromotesHashPartIntegers_WithoutDuplicates()
    {
        // Keys 20 and 24 land in the generic dictionary (they are too far above the
        // array length to be array candidates), then get promoted into the array when
        // it grows past them. That promotion is the only path that removes an entry
        // from a dictionary, so it has to leave iteration and lookup consistent.
        // The array is grown by writing keys that are NOT 20/24, so the promoted values
        // survive: the 16 -> 32 step is what promotes them.
        var table = new LuaTable(0, 0);
        for (var i = 1; i <= 8; i++)
        {
            table[i] = i;
        }

        table[20] = 2000;
        table[24] = 2400;
        Assert.That(table.HashMapCount, Is.EqualTo(2));

        table[16] = 16;
        table[32] = 32;

        Assert.That(table[20], Is.EqualTo(new LuaValue(2000)));
        Assert.That(table[24], Is.EqualTo(new LuaValue(2400)));
        Assert.That(table[16], Is.EqualTo(new LuaValue(16)));
        Assert.That(table[32], Is.EqualTo(new LuaValue(32)));
        Assert.That(table.ArrayLength, Is.EqualTo(32));
        Assert.That(table.HashMapCount, Is.EqualTo(0));

        // Array slots holding a value: 1..8, 16, 20, 24, 32.
        AssertPairsVisit(table, 12);
    }

    [Test]
    public void ArrayGrowth_WithMixedKeys_KeepsStringPartIntact()
    {
        var table = new LuaTable(0, 0);
        for (var i = 0; i < 32; i++)
        {
            table["s" + i] = i;
        }

        table[40] = "hash40";
        table[48] = "hash48";
        Assert.That(table.HashMapCount, Is.EqualTo(34));

        // Grow 0 -> 8 -> 16 -> 32 -> 64 without writing 40/48, so the promoted values
        // survive. The 32 -> 64 step is what promotes them.
        table[8] = 8;
        table[16] = 16;
        table[32] = 32;
        table[64] = 64;

        Assert.That(table[40], Is.EqualTo(new LuaValue("hash40")));
        Assert.That(table[48], Is.EqualTo(new LuaValue("hash48")));
        for (var i = 0; i < 32; i++)
        {
            Assert.That(table["s" + i], Is.EqualTo(new LuaValue(i)));
        }

        Assert.That(table.ArrayLength, Is.EqualTo(64));
        Assert.That(table.HashMapCount, Is.EqualTo(32));

        // Array values at 8, 16, 32, 40, 48, 64 plus the 32 string keys.
        AssertPairsVisit(table, 38);
    }

    [Test]
    public void IterationOrder_IsArrayThenStringThenGeneric()
    {
        var table = new LuaTable(0, 0);
        table["a"] = 1;
        table["b"] = 2;
        table[1] = 10;
        table[2] = 20;
        table[100000] = 30;
        table[true] = 40;

        var keys = new List<LuaValue>();
        foreach (var pair in table)
        {
            keys.Add(pair.Key);
        }

        Assert.That(keys.Count, Is.EqualTo(6));
        Assert.That(keys[0], Is.EqualTo(new LuaValue(1)));
        Assert.That(keys[1], Is.EqualTo(new LuaValue(2)));
        Assert.That(keys[2], Is.EqualTo(new LuaValue("a")));
        Assert.That(keys[3], Is.EqualTo(new LuaValue("b")));
    }

    [Test]
    public void IterationOrder_IsStableAcrossResizes()
    {
        var table = new LuaTable(0, 0);
        var keys = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            var key = "key" + i;
            keys.Add(key);
            table[key] = i;
        }

        var order = new List<LuaValue>();
        foreach (var pair in table)
        {
            order.Add(pair.Key);
        }

        Assert.That(order.Count, Is.EqualTo(keys.Count));
        for (var i = 0; i < keys.Count; i++)
        {
            Assert.That(order[i], Is.EqualTo(new LuaValue(keys[i])), $"index {i}");
        }
    }

    [Test]
    public void TableKeys_UseIdentity()
    {
        var a = new LuaTable();
        var b = new LuaTable();
        var table = new LuaTable(0, 0);
        table[a] = "a";
        table[b] = "b";

        Assert.That(table[a], Is.EqualTo(new LuaValue("a")));
        Assert.That(table[b], Is.EqualTo(new LuaValue("b")));
        Assert.That(table.HashMapCount, Is.EqualTo(2));
        AssertPairsVisit(table, 2);
    }

    [Test]
    public void RemoveAt_And_Insert_KeepArrayConsistent()
    {
        var table = new LuaTable(0, 0);
        for (var i = 1; i <= 5; i++)
        {
            table[i] = i * 10;
        }

        var removed = table.RemoveAt(2);
        Assert.That(removed, Is.EqualTo(new LuaValue(20)));
        Assert.That(table[2], Is.EqualTo(new LuaValue(30)));
        Assert.That(table[4], Is.EqualTo(new LuaValue(50)));

        table.Insert(2, 99);
        Assert.That(table[2], Is.EqualTo(new LuaValue(99)));
        Assert.That(table[3], Is.EqualTo(new LuaValue(30)));
    }

    [Test]
    public async Task Lua_NilDuringPairs_VisitsEveryKeyExactlyOnce()
    {
        // Lua explicitly allows clearing existing fields mid-traversal, so the nil
        // assignment must not reorder or remove the entry that is currently being
        // iterated (tests-lua/nextvar.lua relies on this).
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var result = await state.DoStringAsync(
            """
            local t = { a = 1, b = 2, c = 3, d = 4, e = 5, [100000] = 6, [true] = 7 }
            local seen = 0
            for k, v in pairs(t) do
                assert(t[k] == v, "pairs returned a stale pair")
                t[k] = nil
                seen = seen + 1
            end
            return seen, next(t) == nil
            """
        );

        Assert.That(result[0].TryRead<double>(out var visited), Is.True);
        Assert.That(visited, Is.EqualTo(7.0));
        Assert.That(result[1].ToBoolean(), Is.True);
    }

    [Test]
    public async Task Lua_PairsOverHashPart_MatchesRawLookups()
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var result = await state.DoStringAsync(
            """
            local t = {}
            for i = 1, 200 do
                t["k" .. i] = i
                t[i * 8192 + 7] = -i
            end

            local n = 0
            for k, v in pairs(t) do
                assert(rawget(t, k) == v, "pairs disagree with rawget")
                n = n + 1
            end

            local looked = 0
            for i = 1, 200 do
                if t["k" .. i] == i then looked = looked + 1 end
                if t[i * 8192 + 7] == -i then looked = looked + 1 end
            end

            return n, looked
            """
        );

        Assert.That(result[0].TryRead<double>(out var pairs), Is.True);
        Assert.That(pairs, Is.EqualTo(400.0));
        Assert.That(result[1].TryRead<double>(out var looked), Is.True);
        Assert.That(looked, Is.EqualTo(400.0));
    }

    [Test]
    public async Task Lua_MetamethodKeyWrittenAfterUse_IsStillPickedUp()
    {
        // Writing an "__"-prefixed string key must invalidate the inline metamethod
        // cache, whether the key is new or overwrites an existing one.
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var result = await state.DoStringAsync(
            """
            local mt = {}
            local t = setmetatable({}, mt)
            local before = t.missing          -- resolve via a (currently absent) __index
            mt.__index = function() return "found" end
            local after = t.missing
            return before == nil, after
            """
        );

        Assert.That(result[0].ToBoolean(), Is.True);
        Assert.That(result[1].ToString(), Is.EqualTo("found"));
    }
}

/// <summary>
/// Direct tests for the two hash-part implementations. These cover the paths
/// <see cref="LuaTable"/> cannot reach -- most importantly string-key removal, which only
/// the array part's integer promotion uses on the generic dictionary.
/// </summary>
public class LuaHashDictionaryTests
{
    static List<string> Iterate(ref LuaStringDictionary dictionary)
    {
        var keys = new List<string>();
        var index = 0;
        while (LuaStringDictionary.MoveNext(ref dictionary, dictionary.Version, ref index, out var pair))
        {
            keys.Add(pair.Key.ToString());
        }

        return keys;
    }

    [Test]
    public void StringDictionary_Remove_KeepsLookupsAndIterationConsistent()
    {
        const int n = 512;
        var dictionary = new LuaStringDictionary(0);
        for (var i = 0; i < n; i++)
        {
            dictionary.Insert("k" + i, i);
        }

        for (var i = 0; i < n; i += 2)
        {
            Assert.That(dictionary.Remove("k" + i), Is.True, $"remove k{i}");
        }

        Assert.That(dictionary.Count, Is.EqualTo(n / 2));
        Assert.That(dictionary.LiveCount, Is.EqualTo(n / 2));

        for (var i = 0; i < n; i++)
        {
            var found = dictionary.TryGetValue("k" + i, out var value);
            if (i % 2 == 0)
            {
                Assert.That(found, Is.False, $"k{i} should be gone");
            }
            else
            {
                Assert.That(found, Is.True, $"k{i} should still be present");
                Assert.That(value, Is.EqualTo(new LuaValue(i)));
            }
        }

        var keys = Iterate(ref dictionary);
        Assert.That(keys.Count, Is.EqualTo(n / 2));
        Assert.That(keys.Distinct().Count(), Is.EqualTo(n / 2));
    }

    [Test]
    public void StringDictionary_RemoveAll_ThenRefill()
    {
        const int n = 300;
        var dictionary = new LuaStringDictionary(0);
        for (var i = 0; i < n; i++)
        {
            dictionary.Insert("k" + i, i);
        }

        for (var i = 0; i < n; i++)
        {
            Assert.That(dictionary.Remove("k" + i), Is.True, $"remove k{i}");
        }

        Assert.That(dictionary.Count, Is.EqualTo(0));
        Assert.That(dictionary.TryGetValue("k0", out _), Is.False);
        Assert.That(Iterate(ref dictionary), Is.Empty);

        for (var i = 0; i < n; i++)
        {
            dictionary.Insert("k" + i, i * 10);
        }

        for (var i = 0; i < n; i++)
        {
            Assert.That(dictionary.TryGetValue("k" + i, out var value), Is.True, $"k{i}");
            Assert.That(value, Is.EqualTo(new LuaValue(i * 10)));
        }

        Assert.That(dictionary.LiveCount, Is.EqualTo(n));
        Assert.That(Iterate(ref dictionary).Count, Is.EqualTo(n));
    }

    [Test]
    public void StringDictionary_Remove_InterleavedWithInserts_AcrossResizes()
    {
        // The last slot is swapped into the hole on removal, so repeatedly removing and
        // re-adding while the table grows exercises the slot-to-bucket repair.
        var dictionary = new LuaStringDictionary(0);
        for (var round = 0; round < 400; round++)
        {
            dictionary.Insert("r" + round, round);
            if (round % 3 == 0)
            {
                dictionary.Remove("r" + (round / 3));
            }
        }

        var keys = Iterate(ref dictionary);
        Assert.That(keys.Count, Is.EqualTo(dictionary.LiveCount));
        foreach (var key in keys)
        {
            Assert.That(dictionary.TryGetValue(key, out _), Is.True, key);
        }
    }

    [Test]
    public void StringDictionary_RemoveMissing_ReturnsFalse()
    {
        var dictionary = new LuaStringDictionary(0);
        Assert.That(dictionary.Remove("nope"), Is.False);

        dictionary.Insert("here", 1);
        Assert.That(dictionary.Remove("nope"), Is.False);
        Assert.That(dictionary.TryGetValue("here", out _), Is.True);
    }

    [Test]
    public void StringDictionary_LongCollisionChain_StaysCorrect()
    {
        // Two hundred keys sharing one home index would be a single chain if the hashes
        // collided; either way every key has to remain findable and removable.
        var dictionary = new LuaStringDictionary(0);
        var keys = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var key = "chain_" + i;
            keys.Add(key);
            dictionary.Insert(key, i);
        }

        for (var i = 0; i < keys.Count; i++)
        {
            Assert.That(dictionary.TryGetValue(keys[i], out var value), Is.True, keys[i]);
            Assert.That(value, Is.EqualTo(new LuaValue(i)));
        }

        for (var i = 0; i < keys.Count; i += 2)
        {
            Assert.That(dictionary.Remove(keys[i]), Is.True, keys[i]);
        }

        for (var i = 0; i < keys.Count; i++)
        {
            Assert.That(
                dictionary.TryGetValue(keys[i], out _),
                Is.EqualTo(i % 2 != 0),
                keys[i]
            );
        }
    }

    [Test]
    public void ValueDictionary_Remove_KeepsLookupsAndEnumerationConsistent()
    {
        const int n = 400;
        var dictionary = new LuaValueDictionary(0);
        for (var i = 0; i < n; i++)
        {
            dictionary[(i * 8192) + 100] = i;
        }

        for (var i = 0; i < n; i += 2)
        {
            Assert.That(dictionary.Remove((i * 8192) + 100), Is.True, $"remove {i}");
        }

        Assert.That(dictionary.Count, Is.EqualTo(n / 2));
        Assert.That(dictionary.LiveCount, Is.EqualTo(n / 2));
        Assert.That(dictionary.Remove(12345678), Is.False);

        for (var i = 0; i < n; i++)
        {
            var found = dictionary.TryGetValue((i * 8192) + 100, out var value);
            if (i % 2 == 0)
            {
                Assert.That(found, Is.False, $"{i} should be gone");
            }
            else
            {
                Assert.That(found, Is.True, $"{i} should still be present");
                Assert.That(value, Is.EqualTo(new LuaValue(i)));
            }
        }

        var seen = 0;
        foreach (var pair in dictionary)
        {
            Assert.That(pair.Value.Type, Is.Not.EqualTo(LuaValueType.Nil));
            seen++;
        }

        Assert.That(seen, Is.EqualTo(n / 2));
    }
}
