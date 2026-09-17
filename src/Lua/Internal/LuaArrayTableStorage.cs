using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>Heap array part only, for arrays that grow past 16 elements (or start there via an
/// explicit capacity hint). See the storage-split plan for the full promotion graph.</summary>
sealed class LuaArrayTableStorage : ILuaTableStorage
{
    LuaTableArrayPart part;

    public LuaArrayTableStorage(int capacity)
    {
        part = new LuaTableArrayPart(capacity);
    }

    LuaArrayTableStorage(LuaTableArrayPart part)
    {
        this.part = part;
    }

    public LuaTableStorageKind Kind => LuaTableStorageKind.Array;
    public bool HasFastStringPart => false;
    public int RawArrayLength => part.Length;

    public bool TryGetString(string key, out LuaValue value)
    {
        value = default;
        return false;
    }

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

    public ref LuaValue FindString(string key) => ref Unsafe.NullRef<LuaValue>();

    public ref LuaValue FindArray(int index)
    {
        if (index > 0 && index <= part.Length)
        {
            return ref part.AsSpan()[index - 1];
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    public ref LuaValue FindGeneric(in LuaValue key) => ref Unsafe.NullRef<LuaValue>();

    public void SetString(string key, in LuaValue value) => throw new UnreachableException();

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

    public int HashMapLiveCount => 0;

    public bool TryGetNextFromStringSlot(string key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        pair = default;
        slot = -1;
        return false;
    }

    public bool TryGetFirstFromStringSlot(int index, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        pair = default;
        slot = -1;
        return false;
    }

    public bool SlotHasKey(int slot, string key) => false;

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

    public void Clear() => part.AsSpan().Clear();

    public ILuaTableStorage Clone() => new LuaArrayTableStorage(part.Clone());

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) { }

    public int StringVersion => 0;
    public int GenericVersion => 0;

    public bool MoveNextString(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        current = default;
        return false;
    }

    public bool MoveNextGeneric(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        current = default;
        return false;
    }

    /// <summary>Promotion from an empty table receiving an array-range integer key whose required
    /// capacity is already past 16 (so there is no point routing through <see cref="LuaSmallArrayTableStorage"/> first).</summary>
    internal static LuaArrayTableStorage FromEmpty(int minCapacity) => new(minCapacity);

    internal static LuaArrayTableStorage FromSmallArray(LuaSmallArrayTableStorage source, int minCapacity)
    {
        return new(source.SpillToHeap(minCapacity));
    }

    /// <summary>Hands over this instance's array part by value (a reference-array move, no copy) for
    /// a promotion that discards this instance. Only safe because the caller immediately replaces
    /// whatever held this storage.</summary>
    internal LuaTableArrayPart TakePart() => part;
}
