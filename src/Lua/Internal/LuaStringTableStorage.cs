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
    public int RawArrayLength => 0;

    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        if (key.TryRead<string>(out var keyString))
        {
            if (strings.TryGetValue(keyString, out var result))
            {
                value = result;
                return true;
            }
        }

        value = default;
        return false;
    }

    public ref LuaValue FindValue(in LuaValue key)
    {
        if (key.TryRead<string>(out var keyString))
        {
            return ref strings.FindValue(keyString, out _);
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    public void SetValue(in LuaValue key, in LuaValue value)
    {
        Debug.Assert(key.Type == LuaValueType.String);
        
        strings.Insert(key.ReadAsString(), value);
    }

    public void SetArrayValue(int index, in LuaValue value) => throw new UnreachableException();

    public Span<LuaValue> ArraySpan => Span<LuaValue>.Empty;

    public Memory<LuaValue> ArrayMemory => Memory<LuaValue>.Empty;

    public void EnsureArrayCapacityInPlace(int newCapacity) => throw new UnreachableException();

    public LuaValue RemoveAtArray(int index) => throw new UnreachableException();

    public void InsertArray(int index, in LuaValue value) => throw new UnreachableException();

    public int HashMapLiveCount => strings.LiveCount;

    public bool SlotHasKey(int slot, LuaValue key) => key.Type == LuaValueType.String && strings.SlotHasKey(slot, key.ReadAsString());

    /// <summary>String part only, and no array part to walk first: nil starts at the first entry, a
    /// string key resumes just past itself, and anything else is not a key this kind can hold. Every
    /// entry this can return lives in a hash slot, so <paramref name="slot"/> always names it.</summary>
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        if (key.Type is LuaValueType.Nil)
        {
            return strings.TryGetFirstFrom(0, out pair, out slot);
        }

        if (key.Type != LuaValueType.String)
        {
            pair = default;
            slot = -1;
            return false;
        }

        ref var valueRef = ref strings.FindValue(key.ReadAsString(), out var keySlot);
        if (Unsafe.IsNullRef(ref valueRef))
        {
            pair = default;
            slot = -1;
            return false;
        }

        return strings.TryGetFirstFrom(keySlot + 1, out pair, out slot);
    }

    /// <summary><paramref name="slot"/> holds the control key itself (the caller validated it with
    /// <see cref="SlotHasKey"/>), so the walk resumes at the entry after it.</summary>
    public bool TryGetNextFromSlot(int slot, out KeyValuePair<LuaValue, LuaValue> pair, out int nextSlot)
        => strings.TryGetFirstFrom(slot + 1, out pair, out nextSlot);

    public void Clear() => strings.Clear();

    public ILuaTableStorage Clone() => new LuaStringTableStorage(strings.Clone());

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) =>
        strings.CopyAllEntriesTo(destination);

    public int Version => strings.Version;

    /// <summary>No array part, so <paramref name="index"/> is already the string part's own cursor.</summary>
    public bool MoveNext(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current) =>
        LuaStringDictionary.MoveNext(ref strings, expectedVersion, ref index, out current);

    /// <summary>Hands over this instance's string dictionary by value (a reference-array move, no
    /// copy) for a promotion that discards this instance.</summary>
    internal LuaStringDictionary TakeStrings() => strings;
}
