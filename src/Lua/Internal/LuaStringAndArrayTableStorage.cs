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
    public bool HasFastStringPart => true;
    public int RawArrayLength => part.Length;

    public bool TryGetString(string key, out LuaValue value) => strings.TryGetValue(key, out value);

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

    public bool TryGetGeneric(in LuaValue key, out LuaValue value)
    {
        value = default;
        return false;
    }

    public ref LuaValue FindString(string key) => ref strings.FindValue(key, out _);

    public ref LuaValue FindArray(int index)
    {
        if (index > 0 && index <= part.Length)
        {
            return ref part.AsSpan()[index - 1];
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    public ref LuaValue FindGeneric(in LuaValue key) => ref Unsafe.NullRef<LuaValue>();

    public void SetString(string key, in LuaValue value) => strings.Insert(key, value);

    public void SetArrayGrowing(int index, in LuaValue value)
    {
        Debug.Assert(index > 0 && index <= part.Length);
        part.AsSpan()[index - 1] = value;
    }

    public void SetGeneric(in LuaValue key, in LuaValue value) => throw new UnreachableException();

    public Span<LuaValue> ArraySpan() => part.AsSpan();
    public Memory<LuaValue> ArrayMemory() => part.AsMemory();

    public void EnsureArrayCapacityInPlace(int newCapacity) => part.EnsureCapacity(newCapacity);

    public LuaValue RemoveAtArray(int index) => part.RemoveAt(index);

    public void InsertArray(int index, in LuaValue value) => part.Insert(index, value);

    public int HashMapLiveCount => strings.LiveCount;

    public bool TryGetNextFromStringSlot(string key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        slot = -1;
        ref var valueRef = ref strings.FindValue(key, out var keySlot);
        if (Unsafe.IsNullRef(ref valueRef))
        {
            pair = default;
            return false;
        }

        return strings.TryGetFirstFrom(keySlot + 1, out pair, out slot);
    }

    public bool TryGetFirstFromStringSlot(int index, out KeyValuePair<LuaValue, LuaValue> pair, out int slot) =>
        strings.TryGetFirstFrom(index, out pair, out slot);

    public bool SlotHasKey(int slot, string key) => strings.SlotHasKey(slot, key);

    public bool TryGetNextGeneric(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair)
    {
        pair = default;
        return false;
    }

    public bool TryGetFirstGeneric(out KeyValuePair<LuaValue, LuaValue> pair)
    {
        pair = default;
        return false;
    }

    public void Clear()
    {
        part.AsSpan().Clear();
        strings.Clear();
    }

    public ILuaTableStorage Clone() => new LuaStringAndArrayTableStorage(part.Clone(), strings.Clone());

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) =>
        strings.CopyAllEntriesTo(destination);

    public int StringVersion => strings.Version;
    public int GenericVersion => 0;

    public bool MoveNextString(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current) =>
        LuaStringDictionary.MoveNext(ref strings, expectedVersion, ref index, out current);

    public bool MoveNextGeneric(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        current = default;
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
