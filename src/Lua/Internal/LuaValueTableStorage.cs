using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>
/// Heap array + generic <see cref="LuaValueDictionary"/> -- the terminal shape for a table that has
/// ever held a key that is not a string and not (yet) an array-range integer. Per the storage-split
/// plan's confirmed decisions, this kind has <b>no</b> dedicated fast string path: once a table is
/// promoted here, string keys (existing or new) live in the general dictionary wrapped as
/// <see cref="LuaValue"/>, accepting the loss of the 48-byte bare-string fast representation. This is
/// rare in practice.
/// </summary>
sealed class LuaValueTableStorage : ILuaTableStorage
{
    LuaTableArrayPart part;
    LuaValueDictionary dictionary;

    public LuaValueTableStorage(int arrayCapacity, int dictionaryCapacity)
    {
        part = new LuaTableArrayPart(arrayCapacity);
        dictionary = new LuaValueDictionary(dictionaryCapacity);
    }

    LuaValueTableStorage(LuaTableArrayPart part, LuaValueDictionary dictionary)
    {
        this.part = part;
        this.dictionary = dictionary;
    }

    public LuaTableStorageKind Kind => LuaTableStorageKind.Value;
    public int RawArrayLength => part.Length;

    /// <summary>Array part first, then the dictionary. This is the only kind whose two parts can both
    /// hold integer keys -- <see cref="EnsureArrayCapacityInPlace"/> moves one out of the dictionary and
    /// into the array as the array grows over it -- so an integer key the array covers is no longer in
    /// the dictionary, and a dictionary-only lookup would report it missing.</summary>
    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        if (LuaTableArrayPart.TryGetIndex(key, part.Length, out var index))
        {
            value = MemoryMarshalEx.UnsafeElementAt(part.array, index - 1);
            return true;
        }

        return dictionary.TryGetValue(key, out value);
    }

    /// <summary>Array part first, mirroring <see cref="TryGetValue"/>: a caller that got a dictionary ref
    /// for a key the array part holds would write a second copy of it.</summary>
    public ref LuaValue FindValue(in LuaValue key)
    {
        if (LuaTableArrayPart.TryGetIndex(key, part.Length, out var index))
        {
            return ref MemoryMarshalEx.UnsafeElementAt(part.array, index - 1);
        }

        return ref dictionary.FindValue(key, out _);
    }

    public void SetValue(in LuaValue key, in LuaValue value) => dictionary[key] = value;

    /// <summary>Grows for the write itself, through <see cref="EnsureArrayCapacityInPlace"/> rather than
    /// <c>part.EnsureCapacity</c>: growing here is also what migrates integer keys out of the dictionary
    /// once the array reaches over them, and the write has to land on top of that migration (an
    /// update of an existing key would otherwise be overwritten by the migrated old value).</summary>
    public void SetArrayValue(int index, in LuaValue value)
    {
        EnsureArrayCapacityInPlace(index);
        part.Set(index, value);
    }

    public Span<LuaValue> ArraySpan => part.AsSpan();

    public Memory<LuaValue> ArrayMemory => part.AsMemory();

    /// <summary>Grows the array part in place, then -- exactly like the pre-split <c>LuaTable.GrowArray</c>
    /// -- migrates any integer keys from the generic dictionary that now fall within the grown
    /// array's range. Only this kind needs that migration: it is the only one with both an array part
    /// and a dictionary that can hold integer keys.</summary>
    public void EnsureArrayCapacityInPlace(int newCapacity)
    {
        var prevLength = part.Length;
        if (prevLength >= newCapacity)
        {
            return;
        }

        part.EnsureCapacity(newCapacity);
        var newLength = part.Length;

        var d = dictionary;
        if (d.Count == 0)
        {
            return;
        }

        using PooledList<(int, LuaValue)> indexList = new(d.Count);

        foreach (var (key, value) in d)
        {
            if (TryGetInteger(key, out var index) && index > prevLength && index <= newLength)
            {
                indexList.Add((index, value));
            }
        }

        var span = part.AsSpan();
        foreach (var (index, value) in PooledList<(int, LuaValue)>.AsSpan(indexList))
        {
            d.Remove(index);
            span[index - 1] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryGetInteger(in LuaValue value, out int integer)
    {
        if (value.TryReadNumber(out var num) && MathEx.IsInteger(num) && num <= int.MaxValue)
        {
            integer = (int)num;
            return true;
        }

        integer = 0;
        return false;
    }

    public LuaValue RemoveAtArray(int index) => part.RemoveAt(index);

    public void InsertArray(int index, in LuaValue value) => part.Insert(index, value);

    public int HashMapLiveCount => dictionary.LiveCount;

    /// <summary>A hash slot here is a slot in the generic dictionary, where this kind keeps every hash
    /// key -- string keys included (see the class comment).</summary>
    public bool SlotHasKey(int slot, LuaValue key) => dictionary.SlotHasKey(slot, key);

    /// <summary>Array part first, then the dictionary. A string key only ever resumes within the
    /// dictionary -- it cannot live in the array, so it has no position there to resume from. The slot
    /// is -1 only for an entry taken from the array part: this kind is where a walk crossing from the
    /// array part into the dictionary matters most, because the dictionary is the only part of it a
    /// caller cannot cheaply resume in (an integer key out of array range hashes like any other).</summary>
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        if (key.Type is LuaValueType.String)
        {
            return dictionary.TryGetNext(key, out pair, out slot);
        }

        if (!LuaTableArrayPart.TryGetNextStart(key, part.Length, out var start))
        {
            return dictionary.TryGetNext(key, out pair, out slot);
        }

        if (part.TryGetFirstFrom(start, out pair))
        {
            slot = -1;
            return true;
        }

        return dictionary.TryGetFirstFrom(0, out pair, out slot);
    }

    /// <summary><paramref name="slot"/> holds the control key itself (the caller validated it with
    /// <see cref="SlotHasKey"/>), so the walk resumes at the entry after it.</summary>
    public bool TryGetNextFromSlot(int slot, out KeyValuePair<LuaValue, LuaValue> pair, out int nextSlot) =>
        dictionary.TryGetFirstFrom(slot + 1, out pair, out nextSlot);

    public void Clear()
    {
        part.AsSpan().Clear();
        dictionary.Clear();
    }

    public ILuaTableStorage Clone() => new LuaValueTableStorage(part.Clone(), dictionary.Clone());

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) =>
        dictionary.CopyAllEntriesTo(destination);

    public int Version => dictionary.Version;

    /// <summary>
    /// Both phases under one cursor: array slots <c>[0, part.Length)</c>, then dictionary entries with
    /// the array length added to their own cursor. Exhausting the array part leaves the cursor at
    /// exactly <c>part.Length</c>, which is the dictionary's cursor 0.
    /// </summary>
    public bool MoveNext(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        var length = part.Length;
        if (index < length)
        {
            if (part.MoveNext(ref index, out current))
            {
                return true;
            }

            index = length;
        }

        // The dictionary's own MoveNext leaves `index` one past its last entry once it is done, which it
        // would then index out of bounds on a second call -- clamp so a caller that asks again after a
        // false keeps getting false.
        var hashIndex = Math.Min(index - length, dictionary.Count);
        if (LuaValueDictionary.MoveNext(dictionary, expectedVersion, ref hashIndex, out current))
        {
            index = hashIndex + length;
            return true;
        }

        index = hashIndex + length;
        return false;
    }

    internal static LuaValueTableStorage FromArray(LuaArrayTableStorage source) =>
        new(source.TakePart(), new LuaValueDictionary(0));

    internal static LuaValueTableStorage FromSmallArray(LuaSmallArrayTableStorage source) =>
        new(source.SpillToHeap(0), new LuaValueDictionary(0));

    internal static LuaValueTableStorage FromString(LuaStringTableStorage source)
    {
        var dictionary = new LuaValueDictionary(0);
        var entries = new List<KeyValuePair<LuaValue, LuaValue>>();
        source.CopyHashEntriesTo(entries);
        foreach (var (key, value) in entries)
        {
            dictionary[key] = value;
        }

        return new(new LuaTableArrayPart(0), dictionary);
    }

    internal static LuaValueTableStorage FromStringAndArray(LuaStringAndArrayTableStorage source)
    {
        var dictionary = new LuaValueDictionary(0);
        var entries = new List<KeyValuePair<LuaValue, LuaValue>>();
        source.CopyHashEntriesTo(entries);
        foreach (var (key, value) in entries)
        {
            dictionary[key] = value;
        }

        return new(source.TakePart(), dictionary);
    }
}
