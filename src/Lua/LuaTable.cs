using System.Collections;
using System.Runtime.CompilerServices;
using Lua.Internal;

namespace Lua;

public sealed class LuaTable : IEnumerable<KeyValuePair<LuaValue, LuaValue>>
{
    public LuaTable()
        : this(8, 8) { }

    public LuaTable(int arrayCapacity, int dictionaryCapacity)
    {
        array = arrayCapacity > 1 ? new LuaValue[arrayCapacity] : [];
        // The compiler's hash-size hint counts a literal's named fields, which are
        // almost always strings, so it sizes the string part. The generic part is
        // only created if a non-string, non-array key ever shows up.
        stringDictionary = new(dictionaryCapacity);
        LuaTableDiagnostics.RecordTableCreated();
    }

    LuaValue[] array;

    // Hash part is split by key kind. String keys (the overwhelmingly common,
    // record-shaped case) live in an embedded struct whose entries hold a bare
    // string reference instead of a full LuaValue -- 48 bytes/entry vs 80 -- and
    // which costs no separate object per table. Everything else (booleans,
    // non-array numbers, tables, functions, userdata as keys) goes to a lazily
    // created generic dictionary that most tables never allocate.
    // Both use a signature-bucket layout: see LuaStringDictionary for the invariants.
    LuaStringDictionary stringDictionary;
    LuaValueDictionary? dictionary;
    LuaTable? metatable;

    const int MaxArraySize = 1 << 24;
    const int MaxDistance = 1 << 12;

    public LuaValue this[in LuaValue key]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (key.Type is LuaValueType.String)
            {
                return stringDictionary.TryGetValue(key.ReadAsString(), out var sv) ? sv : LuaValue.Nil;
            }

            if (key.Type is LuaValueType.Nil)
            {
                ThrowIndexIsNil();
            }

            if (TryGetInteger(key, out var index))
            {
                if (index > 0 && index <= array.Length)
                {
                    // Arrays in Lua are 1-origin...
                    return array[index - 1];
                }
            }

            if (dictionary is not null && dictionary.TryGetValue(key, out var value))
            {
                return value;
            }

            return LuaValue.Nil;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if (key.Type is LuaValueType.String)
            {
                stringDictionary.Insert(key.ReadAsString(), value);
                return;
            }

            if (key.TryReadNumber(out var d))
            {
                if (double.IsNaN(d))
                {
                    ThrowIndexIsNaN();
                }

                if (MathEx.IsInteger(d))
                {
                    var index = (int)d;

                    var distance = index - array.Length;
                    if (distance > MaxDistance)
                    {
                        GetOrCreateDictionary()[key] = value;
                        return;
                    }

                    if (0 < index && index < MaxArraySize && index <= Math.Max(array.Length * 2, 8))
                    {
                        if (array.Length < index)
                        {
                            GrowArray(index);
                        }

                        array[index - 1] = value;
                        return;
                    }
                }
            }

            GetOrCreateDictionary()[key] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    LuaValueDictionary GetOrCreateDictionary()
    {
        return dictionary ??= new(0);
    }

    public int HashMapCount
    {
        get
        {
            // Both dictionaries keep nil-valued entries (so `next` can still find a key
            // that was just set to nil), so liveness has to be counted, not derived.
            var count = stringDictionary.LiveCount;
            if (dictionary is not null)
            {
                count += dictionary.LiveCount;
            }

            return count;
        }
    }

    public int ArrayLength
    {
        get
        {
            var a = array;
            var len = a.Length;
            if (len == 0)
            {
                return 0;
            }

            // Fast path: the array part is filled up to its size (last slot non-nil),
            // so its size is a valid border. This is the common case for arrays built
            // by append (`t[#t + 1] = ...`) or dense indexing (`t[i] = ...`), and makes
            // `#` O(1) instead of the previous O(n) linear scan.
            if (a[len - 1].Type is not LuaValueType.Nil)
            {
                return len;
            }

            // No element at key 1 (first slot nil) -> border is 0.
            if (a[0].Type is LuaValueType.Nil)
            {
                return 0;
            }

            // Otherwise binary-search for the largest n in [1..len] with a[n-1] non-nil
            // (the array border). O(log n); for a dense prefix this is exactly the old
            // "first nil" boundary, and for holey arrays it yields a valid Lua border.
            var lo = 1;
            var hi = len;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) >> 1;
                if (a[mid - 1].Type is not LuaValueType.Nil)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }
    }

    public LuaTable? Metatable
    {
        get => metatable;
        set => metatable = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        if (key.Type is LuaValueType.String)
        {
            return stringDictionary.TryGetValue(key.ReadAsString(), out value)
                && value.Type is not LuaValueType.Nil;
        }

        if (key.Type is LuaValueType.Nil)
        {
            value = default;
            return false;
        }

        if (TryGetInteger(key, out var index))
        {
            if (index > 0 && index <= array.Length)
            {
                value = array[index - 1];
                return value.Type is not LuaValueType.Nil;
            }
        }

        if (dictionary is null)
        {
            value = default;
            return false;
        }

        return dictionary.TryGetValue(key, out value) && value.Type is not LuaValueType.Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref LuaValue FindValue(in LuaValue key)
    {
        if (key.Type is LuaValueType.String)
        {
            return ref stringDictionary.FindValue(key.ReadAsString(), out _);
        }

        if (key.Type is LuaValueType.Nil)
        {
            ThrowIndexIsNil();
        }

        if (TryGetInteger(key, out var index))
        {
            if (index > 0 && index <= array.Length)
            {
                return ref array[index - 1];
            }
        }

        if (dictionary is null)
        {
            return ref Unsafe.NullRef<LuaValue>();
        }

        return ref dictionary.FindValue(key, out _);
    }

    public bool ContainsKey(in LuaValue key)
    {
        return TryGetValue(key, out _);
    }

    public LuaValue RemoveAt(int index)
    {
        var arrayIndex = index - 1;
        var value = array[arrayIndex];

        if (arrayIndex < array.Length - 1)
        {
            array.AsSpan(arrayIndex + 1).CopyTo(array.AsSpan(arrayIndex));
        }

        array[^1] = default;

        return value;
    }

    public void Insert(int index, in LuaValue value)
    {
        if (index <= 0 || index > array.Length + 1)
        {
            throw new IndexOutOfRangeException();
        }

        var arrayIndex = index - 1;
        var distance = index - array.Length;
        if (distance > MaxDistance)
        {
            GetOrCreateDictionary()[index] = value;
            return;
        }

        if (index > array.Length || array[^1].Type != LuaValueType.Nil)
        {
            GrowArray(array.Length + 1);
        }

        if (arrayIndex != array.Length - 1)
        {
            array
                .AsSpan(arrayIndex, array.Length - arrayIndex - 1)
                .CopyTo(array.AsSpan(arrayIndex + 1));
        }

        array[arrayIndex] = value;
    }

    /// <summary>
    /// Lua `next` semantics. Iteration order is array part, then string keys, then
    /// everything else.
    /// </summary>
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair)
    {
        if (key.Type is LuaValueType.String)
        {
            if (stringDictionary.TryGetNext(key.ReadAsString(), out pair, out var found))
            {
                return true;
            }

            // Key was the last string entry: continue into the generic part.
            return found && TryGetFirstGeneric(out pair);
        }

        var index = -1;
        if (key.Type is LuaValueType.Nil)
        {
            index = 0;
        }
        else if (TryGetInteger(key, out var integer) && integer > 0 && integer <= array.Length)
        {
            index = integer;
        }

        if (index != -1)
        {
            var span = array.AsSpan(index);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].Type is not LuaValueType.Nil)
                {
                    pair = new(index + i + 1, span[i]);
                    return true;
                }
            }

            if (stringDictionary.TryGetFirstFrom(0, out pair))
            {
                return true;
            }

            return TryGetFirstGeneric(out pair);
        }

        if (dictionary is not null && dictionary.TryGetNext(key, out pair))
        {
            return true;
        }

        pair = default;
        return false;
    }

    /// <summary>
    /// <c>next(t, k)</c> for a string control key, also reporting the slot the result lives
    /// in (see <see cref="TryNextFromSlot"/>). Mirrors the string branch of
    /// <see cref="TryGetNext"/> exactly, including falling through to the generic part once
    /// the string part is exhausted.
    /// </summary>
    internal bool TryGetNextFromString(
        string key,
        out KeyValuePair<LuaValue, LuaValue> pair,
        out int slot
    )
    {
        slot = -1;

        // A null ref means the key itself is not in the string part -- the same condition
        // TryGetNext reports through `found == false`.
        ref var valueRef = ref stringDictionary.FindValue(key, out var keySlot);
        if (Unsafe.IsNullRef(ref valueRef))
        {
            pair = default;
            return false;
        }

        if (stringDictionary.TryGetFirstFrom(keySlot + 1, out pair, out slot))
        {
            return true;
        }

        return TryGetFirstGeneric(out pair);
    }

    /// <summary>
    /// Resumes a string-keyed traversal at <paramref name="slot"/>: the first non-nil entry
    /// after it, then the generic part. Lets a caller that already knows which slot a key
    /// occupies skip hashing it again. <paramref name="nextSlot"/> is -1 when the result
    /// came from (or there is nothing left in) the generic part.
    /// </summary>
    internal bool TryNextFromSlot(
        int slot,
        out KeyValuePair<LuaValue, LuaValue> pair,
        out int nextSlot
    )
    {
        if (stringDictionary.TryGetFirstFrom(slot + 1, out pair, out nextSlot))
        {
            return true;
        }

        return TryGetFirstGeneric(out pair);
    }

    /// <summary>
    /// True while <paramref name="slot"/> still holds exactly <paramref name="key"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SlotStillHolds(int slot, string key)
    {
        return stringDictionary.SlotHasKey(slot, key);
    }

    bool TryGetFirstGeneric(out KeyValuePair<LuaValue, LuaValue> pair)
    {
        var dict = dictionary;
        if (dict is not null)
        {
            // MoveNext already skips nil-valued entries.
            var i = 0;
            return LuaValueDictionary.MoveNext(dict, dict.Version, ref i, out pair);
        }

        pair = default;
        return false;
    }

    public void Clear()
    {
        array.AsSpan().Clear();
        stringDictionary.Clear();
        dictionary?.Clear();
    }

    public Memory<LuaValue> GetArrayMemory()
    {
        return array.AsMemory();
    }

    public Span<LuaValue> GetArraySpan()
    {
        return array.AsSpan();
    }

    internal void EnsureArrayCapacity(int newCapacity)
    {
        if (array.Length < newCapacity) GrowArray(newCapacity);
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: the independent copy <see cref="Lua.Runtime.OpCode.DupTable"/> stores
    /// into its destination register, of the all-constant template that instruction names. One
    /// <c>Clone()</c> per part -- the array and both hash structures -- so the copy is identical to
    /// the template at its exact capacities, which is what makes a fused literal indistinguishable
    /// from the unfused NewTable + per-field writes that built the template.
    ///
    /// No metatable is copied (a template never has one) and no metamethod can fire: the copy is
    /// fresh with no live entries, so <c>__index</c>/<c>__newindex</c> are unreachable and
    /// <see cref="MetamethodCache"/> needs no invalidation -- even a template field literally named
    /// <c>__index</c> is inert on a metatable-less table.
    /// </summary>
    internal LuaTable CloneTemplate()
    {
        var clone = new LuaTable(0, 0);
        clone.array = (LuaValue[])array.Clone();
        clone.stringDictionary = stringDictionary.Clone();
        clone.dictionary = dictionary?.Clone();
        return clone;
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: the hash part only, in insertion order, for serializing a
    /// template (<c>Dump.cs</c>). The two dictionaries are copied directly rather than walked
    /// through <see cref="GetEnumerator"/> on purpose: that enumerator interleaves the array part,
    /// and deciding "is this key an array slot?" from outside would mean duplicating the indexer's
    /// key routing. Nothing here can be an array-range integer key -- writing one routes it to the
    /// array, and <see cref="GrowArray"/> migrates any the array later grows over -- so a caller can
    /// write this list alongside <see cref="GetArraySpan"/> without the two overlapping.
    /// <para>
    /// This is the <em>whole</em> hash part, dead (nil-valued) entries included, which is why it
    /// cannot use the public enumerator: that one skips them (they are what <c>pairs</c> must not
    /// expose), but a serialized template has to reproduce the table's actual contents rather than
    /// its iteration view. Dropping a dead entry changes what <c>next(t, k)</c> returns for a key
    /// the reader wrote as nil -- a live-table-empty answer instead of the entry that follows it --
    /// so the reconstructed template would not be the template.
    /// </para>
    /// </summary>
    internal void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination)
    {
        stringDictionary.CopyAllEntriesTo(destination);
        dictionary?.CopyAllEntriesTo(destination);
    }

    private void GrowArray(int newCapacity)
    {
        var prevLength = array.Length;
        var newLength = newCapacity <= 8 ? 8 : MathEx.NextPowerOfTwo(newCapacity);

        Array.Resize(ref array, newLength);

        // Only the generic part can hold integer keys, so string-only tables skip
        // the migration scan entirely.
        var dict = dictionary;
        if (dict is null || dict.Count == 0)
        {
            return;
        }

        
        using PooledList<(int, LuaValue)> indexList = new(dict.Count);

        // Move some of the elements of the hash part to a newly allocated array
        foreach (var kv in dict)
        {
            if (TryGetInteger(kv.Key, out var index))
            {
                if (index > prevLength && index <= newLength)
                {
                    indexList.Add((index, kv.Value));
                }
            }
        }

        foreach (var (index, value) in PooledList<(int, LuaValue)>.AsSpan(indexList))
        {
            dict.Remove(index);
            array[index - 1] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryGetInteger(in LuaValue value, out int integer)
    {
        if (value.TryReadNumber(out var num) && MathEx.IsInteger(num))
        {
            // TODO: saturate? or return long?
            integer = (int)num;
            return true;
        }

        integer = 0;
        return false;
    }

    static void ThrowIndexIsNil()
    {
        throw new ArgumentException("the table index is nil");
    }

    static void ThrowIndexIsNaN()
    {
        throw new ArgumentException("the table index is NaN");
    }

    public LuaTableEnumerator GetEnumerator()
    {
        return new(this);
    }

    IEnumerator<KeyValuePair<LuaValue, LuaValue>> IEnumerable<
        KeyValuePair<LuaValue, LuaValue>
    >.GetEnumerator()
    {
        return new LuaTableEnumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return new LuaTableEnumerator(this);
    }

    public struct LuaTableEnumerator(LuaTable table) : IEnumerator<KeyValuePair<LuaValue, LuaValue>>
    {
        public KeyValuePair<LuaValue, LuaValue> Current => current;

        // phase 0: array part (index = next array slot to inspect)
        // phase 1: string part (index = string dictionary entry index)
        // phase 2: generic part (index = generic dictionary entry index)
        int phase = 0;
        int index = 0;
        readonly int stringVersion = table.stringDictionary.Version;
        readonly int genericVersion = table.dictionary?.Version ?? 0;
        KeyValuePair<LuaValue, LuaValue> current = default;

        public bool MoveNext()
        {
            if (phase == 0)
            {
                var span = table.array.AsSpan(index);
                for (var i = 0; i < span.Length; i++)
                {
                    if (span[i].Type is not LuaValueType.Nil)
                    {
                        current = new(index + i + 1, span[i]);
                        index += i + 1;
                        return true;
                    }
                }

                phase = 1;
                index = 0;
            }

            if (phase == 1)
            {
                if (LuaStringDictionary.MoveNext(ref table.stringDictionary, stringVersion, ref index, out current))
                {
                    return true;
                }

                phase = 2;
                index = 0;
            }

            var dict = table.dictionary;
            if (dict is null)
            {
                current = default;
                return false;
            }

            while (
                LuaValueDictionary.MoveNext(dict, genericVersion, ref index, out current)
                && current.Value.Type is LuaValueType.Nil
            ) { }

            return current.Value.Type is not LuaValueType.Nil;
        }

        public void Reset() { }

        object IEnumerator.Current => Current;

        public void Dispose() { }
    }
}
