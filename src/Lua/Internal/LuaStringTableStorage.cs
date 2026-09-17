using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>A table with only a string-keyed hash part -- no array, no generic dictionary. The
/// pure prop-bag shape (e.g. Sx style tables).</summary>
sealed class LuaStringTableStorage : ILuaTableStorage
{
    internal LuaStringDictionary strings;

    public LuaStringTableStorage(int capacity)
    {
        strings = new LuaStringDictionary(capacity);
    }

    LuaStringTableStorage(LuaStringDictionary strings)
    {
        this.strings = strings;
    }

    public LuaTableStorageKind Kind => LuaTableStorageKind.String;
    public bool HasFastStringPart => true;
    public int RawArrayLength => 0;

    public bool TryGetString(string key, out LuaValue value) => strings.TryGetValue(key, out value);

    public bool TryGetArray(int index, out LuaValue value)
    {
        value = default;
        return false;
    }

    public bool TryGetGeneric(in LuaValue key, out LuaValue value)
    {
        value = default;
        return false;
    }

    public ref LuaValue FindString(string key) => ref strings.FindValue(key, out _);

    public ref LuaValue FindArray(int index) => ref Unsafe.NullRef<LuaValue>();

    public ref LuaValue FindGeneric(in LuaValue key) => ref Unsafe.NullRef<LuaValue>();

    public void SetString(string key, in LuaValue value) => strings.Insert(key, value);

    public void SetArrayGrowing(int index, in LuaValue value) => throw new UnreachableException();

    public void SetGeneric(in LuaValue key, in LuaValue value) => throw new UnreachableException();

    public Span<LuaValue> ArraySpan() => Span<LuaValue>.Empty;

    public Memory<LuaValue> ArrayMemory() => Memory<LuaValue>.Empty;

    public void EnsureArrayCapacityInPlace(int newCapacity) => throw new UnreachableException();

    public LuaValue RemoveAtArray(int index) => throw new UnreachableException();

    public void InsertArray(int index, in LuaValue value) => throw new UnreachableException();

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

    public void Clear() => strings.Clear();

    public ILuaTableStorage Clone() => new LuaStringTableStorage(strings.Clone());

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

    /// <summary>Hands over this instance's string dictionary by value (a reference-array move, no
    /// copy) for a promotion that discards this instance.</summary>
    internal LuaStringDictionary TakeStrings() => strings;
}
