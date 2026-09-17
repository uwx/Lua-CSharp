using System.Diagnostics;
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
    public bool HasFastStringPart => false;
    public int RawArrayLength => part.Length;

    public bool TryGetString(string key, out LuaValue value) => dictionary.TryGetValue(new LuaValue(key), out value);

    public bool TryGetArray(int index, out LuaValue value)
    {
        if (index > 0 && index <= part.Length)
        {
            value = part.AsSpan()[index - 1];
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetGeneric(in LuaValue key, out LuaValue value) => dictionary.TryGetValue(key, out value);

    /// <summary>
    /// Never actually called: <see cref="LuaTable.FindValue"/> checks <see cref="HasFastStringPart"/>
    /// first and calls <see cref="FindGeneric"/> directly with the original (already-wrapped)
    /// <see cref="LuaValue"/> key instead, since constructing a fresh wrapper here just to hand it to
    /// a ref-returning lookup would not be ref-safe to return from this method.
    /// </summary>
    public ref LuaValue FindString(string key) => throw new UnreachableException();

    public ref LuaValue FindArray(int index)
    {
        if (index > 0 && index <= part.Length)
        {
            return ref part.AsSpan()[index - 1];
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    public ref LuaValue FindGeneric(in LuaValue key) => ref dictionary.FindValue(key, out _);

    /// <summary>Never called by <see cref="LuaTable"/> -- this kind has no fast string part, so
    /// string writes route through <see cref="SetGeneric"/> instead.</summary>
    public void SetString(string key, in LuaValue value) => dictionary[new LuaValue(key)] = value;

    public void SetArrayGrowing(int index, in LuaValue value)
    {
        part.AsSpan()[index - 1] = value;
    }

    public void SetGeneric(in LuaValue key, in LuaValue value) => dictionary[key] = value;

    public Span<LuaValue> ArraySpan() => part.AsSpan();
    public Memory<LuaValue> ArrayMemory() => part.AsMemory();

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

        if (dictionary.Count == 0)
        {
            return;
        }

        using PooledList<(int, LuaValue)> indexList = new(dictionary.Count);

        foreach (var kv in dictionary)
        {
            if (TryGetInteger(kv.Key, out var index) && index > prevLength && index <= newLength)
            {
                indexList.Add((index, kv.Value));
            }
        }

        foreach (var (index, value) in PooledList<(int, LuaValue)>.AsSpan(indexList))
        {
            dictionary.Remove(index);
            part.AsSpan()[index - 1] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryGetInteger(in LuaValue value, out int integer)
    {
        if (value.TryReadNumber(out var num) && MathEx.IsInteger(num))
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

    public bool TryGetNextFromStringSlot(string key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        slot = -1;
        return dictionary.TryGetNext(new LuaValue(key), out pair);
    }

    public bool TryGetFirstFromStringSlot(int index, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        slot = -1;
        pair = default;
        return false;
    }

    public bool SlotHasKey(int slot, string key) => false;

    public bool TryGetNextGeneric(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair) =>
        dictionary.TryGetNext(key, out pair);

    public bool TryGetFirstGeneric(out KeyValuePair<LuaValue, LuaValue> pair)
    {
        var i = 0;
        return LuaValueDictionary.MoveNext(dictionary, dictionary.Version, ref i, out pair);
    }

    public void Clear()
    {
        part.AsSpan().Clear();
        dictionary.Clear();
    }

    public ILuaTableStorage Clone() => new LuaValueTableStorage(part.Clone(), dictionary.Clone());

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) =>
        dictionary.CopyAllEntriesTo(destination);

    public int StringVersion => 0;
    public int GenericVersion => dictionary.Version;

    public bool MoveNextString(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        current = default;
        return false;
    }

    public bool MoveNextGeneric(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current) =>
        LuaValueDictionary.MoveNext(dictionary, expectedVersion, ref index, out current);

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
