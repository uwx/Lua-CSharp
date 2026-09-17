using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>Heap array + string-keyed hash part. The Sx call-site-literal shape: UI descriptors mix
/// positional children (array part) with named props (string part) in the same table literal.</summary>
sealed class LuaStringAndArrayTableStorage : ILuaTableStorage
{
    LuaTableArrayPart part;
    internal LuaStringDictionary strings;

    public LuaStringAndArrayTableStorage(int arrayCapacity, int dictionaryCapacity)
    {
        part = new LuaTableArrayPart(arrayCapacity);
        strings = new LuaStringDictionary(dictionaryCapacity);
    }

    LuaStringAndArrayTableStorage(LuaTableArrayPart part, LuaStringDictionary strings)
    {
        this.part = part;
        this.strings = strings;
    }

    public LuaTableStorageKind Kind => LuaTableStorageKind.StringAndArray;
    public int RawArrayLength => part.Length;

    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        if (key.Type is LuaValueType.String)
        {
            return strings.TryGetValue(key.ReadAsString(), out value);
        }

        if (LuaTableArrayPart.TryGetIndex(key, part.Length, out var index))
        {
            value = MemoryMarshalEx.UnsafeElementAt(part.array, index - 1);
            return true;
        }

        value = default;
        return false;
    }

    public ref LuaValue FindValue(in LuaValue key)
    {
        if (key.Type is LuaValueType.String)
        {
            return ref strings.FindValue(key.ReadAsString(), out _);
        }

        if (LuaTableArrayPart.TryGetIndex(key, part.Length, out var index))
        {
            return ref MemoryMarshalEx.UnsafeElementAt(part.array, index - 1);
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    public void SetValue(in LuaValue key, in LuaValue value)
    {
        if (key.Type is LuaValueType.String)
        {
            strings.Insert(key.ReadAsString(), value);
            return;
        }

        if (LuaTableArrayPart.TryGetIndex(key, part.Length, out var index))
        {
            MemoryMarshalEx.UnsafeElementAt(part.array, index - 1) = value;
        }
    }

    /// <summary>Grows for the write itself (<see cref="ILuaTableStorage.SetArrayValue"/>).</summary>
    public void SetArrayValue(int index, in LuaValue value)
    {
        part.EnsureCapacity(index);
        part.Set(index, value);
    }

    public Span<LuaValue> ArraySpan => part.AsSpan();

    public Memory<LuaValue> ArrayMemory => part.AsMemory();

    public void EnsureArrayCapacityInPlace(int newCapacity) => part.EnsureCapacity(newCapacity);

    public LuaValue RemoveAtArray(int index) => part.RemoveAt(index);

    public void InsertArray(int index, in LuaValue value) => part.Insert(index, value);

    public int HashMapLiveCount => strings.LiveCount;

    public bool SlotHasKey(int slot, LuaValue key) => key.Type == LuaValueType.String && strings.SlotHasKey(slot, key.ReadAsString());

    /// <summary>Array part first, then the string part. A string key only ever resumes within the
    /// string part -- it cannot live in the array, so it has no position there to resume from. The slot
    /// is -1 only for an entry taken from the array part: the walk crossing from there into the string
    /// part reports the string slot it landed in, which is what lets a caller resume in the hash part
    /// without re-hashing the control key.</summary>
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        if (key.Type is LuaValueType.String)
        {
            ref var valueRef = ref strings.FindValue(key.ReadAsString(), out var keySlot);
            if (Unsafe.IsNullRef(ref valueRef))
            {
                pair = default;
                slot = -1;
                return false;
            }

            return strings.TryGetFirstFrom(keySlot + 1, out pair, out slot);
        }

        if (!LuaTableArrayPart.TryGetNextStart(key, part.Length, out var start))
        {
            pair = default;
            slot = -1;
            return false;
        }

        if (part.TryGetFirstFrom(start, out pair))
        {
            slot = -1;
            return true;
        }

        return strings.TryGetFirstFrom(0, out pair, out slot);
    }

    /// <summary><paramref name="slot"/> holds the control key itself (the caller validated it with
    /// <see cref="SlotHasKey"/>), so the walk resumes at the entry after it.</summary>
    public bool TryGetNextFromSlot(int slot, out KeyValuePair<LuaValue, LuaValue> pair, out int nextSlot)
        => strings.TryGetFirstFrom(slot + 1, out pair, out nextSlot);

    public void Clear()
    {
        part.AsSpan().Clear();
        strings.Clear();
    }

    public ILuaTableStorage Clone() => new LuaStringAndArrayTableStorage(part.Clone(), strings.Clone());

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination)
        => strings.CopyAllEntriesTo(destination);

    public int Version => strings.Version;

    /// <summary>
    /// Both phases under one cursor: array slots <c>[0, part.Length)</c>, then string entries with the
    /// array length added to their own cursor. Exhausting the array part leaves the cursor at exactly
    /// <c>part.Length</c>, which is the string part's cursor 0.
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

        // The string dictionary's own MoveNext leaves `index` one past its last entry once it is done,
        // which it would then index out of bounds on a second call -- clamp so a caller that asks again
        // after a false keeps getting false.
        var hashIndex = Math.Min(index - length, strings.Count);
        if (LuaStringDictionary.MoveNext(ref strings, expectedVersion, ref hashIndex, out current))
        {
            index = hashIndex + length;
            return true;
        }

        index = hashIndex + length;
        return false;
    }

    /// <summary>Promotes an array-only storage that just received its first string key. The array
    /// part is taken over as-is (no copy needed -- <paramref name="source"/> is discarded by the
    /// caller); the string part starts empty for the caller to insert into.</summary>
    internal static LuaStringAndArrayTableStorage FromArray(LuaArrayTableStorage source) =>
        new(source.TakePart(), new LuaStringDictionary(0));

    /// <summary>Promotes a small (inline, &lt;=16) array-only storage that just received its first
    /// string key, spilling the inline buffer to a heap array.</summary>
    internal static LuaStringAndArrayTableStorage FromSmallArray(LuaSmallArrayTableStorage source) =>
        new(source.SpillToHeap(0), new LuaStringDictionary(0));

    /// <summary>Promotes a string-only storage that just received its first array-range integer key.
    /// The string part is taken over as-is; the array part starts at <paramref name="arrayCapacity"/>
    /// for the caller to write into (the caller calls <see cref="EnsureArrayCapacityInPlace"/> to grow
    /// it further if needed).</summary>
    internal static LuaStringAndArrayTableStorage FromString(LuaStringTableStorage source, int arrayCapacity) =>
        new(new LuaTableArrayPart(arrayCapacity), source.TakeStrings());

    /// <summary>Hands over this instance's array part by value for a promotion to
    /// <see cref="LuaValueTableStorage"/> that discards this instance.</summary>
    internal LuaTableArrayPart TakePart() => part;
}
