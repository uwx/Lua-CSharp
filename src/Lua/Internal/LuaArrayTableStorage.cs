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
    public int RawArrayLength => part.Length;

    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
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
        if (LuaTableArrayPart.TryGetIndex(key, part.Length, out var index))
        {
            return ref MemoryMarshalEx.UnsafeElementAt(part.array, index - 1);
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    public void SetValue(in LuaValue key, in LuaValue value)
    {
        if (LuaTableArrayPart.TryGetIndex(key, part.Length, out var index))
        {
            MemoryMarshalEx.UnsafeElementAt(part.array, index - 1) = value;
        }
    }

    /// <summary>Grows for the write itself (<see cref="ILuaTableStorage.SetArrayValue"/>) -- the
    /// indexer's array branch is the one place a table grows on nearly every call, and a separate
    /// growth dispatch there would double the interface traffic of the write.</summary>
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

    public int HashMapLiveCount => 0;

    public bool SlotHasKey(int slot, LuaValue key) => false;

    /// <summary>Array-only: nil resumes at the first slot, an array-range integer just past it. Nothing
    /// else has a position in this kind, so nothing else has a successor either. The slot is always -1
    /// -- there is no hash part to hold a cursor into, and an array entry needs none (its own integer
    /// key is its position).</summary>
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        slot = -1;
        if (!LuaTableArrayPart.TryGetNextStart(key, part.Length, out var start))
        {
            pair = default;
            return false;
        }

        return part.TryGetFirstFrom(start, out pair);
    }

    public bool TryGetNextFromSlot(int slot, out KeyValuePair<LuaValue, LuaValue> pair, out int nextSlot)
    {
        pair = default;
        nextSlot = -1;
        return false;
    }

    public void Clear() => part.AsSpan().Clear();

    public ILuaTableStorage Clone() => new LuaArrayTableStorage(part.Clone());

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) { }

    public int Version => 0;

    /// <summary>The whole array part, under the same 0-based-slot cursor
    /// <see cref="LuaTableArrayPart.MoveNext"/> uses (there is no hash part after it to carry on into).</summary>
    public bool MoveNext(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current) =>
        part.MoveNext(ref index, out current);

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
