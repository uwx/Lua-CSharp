using System.Diagnostics;

namespace Lua.Internal;

/// <summary>
/// Stateless shared singleton for a table with no array/dictionary capacity hints. Bootstrap-only:
/// the moment any write happens, <see cref="LuaTable"/> promotes away from this instance to a
/// concrete kind. Never mutated -- there is nothing to mutate.
/// </summary>
sealed class LuaEmptyTableStorage : ILuaTableStorage
{
    public static readonly LuaEmptyTableStorage Instance = new();

    LuaEmptyTableStorage() { }

    public LuaTableStorageKind Kind => LuaTableStorageKind.Empty;
    public int RawArrayLength => 0;

    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        value = default;
        return false;
    }

    public ref LuaValue FindValue(in LuaValue key) => ref System.Runtime.CompilerServices.Unsafe.NullRef<LuaValue>();

    public void SetValue(in LuaValue key, in LuaValue value) => throw new UnreachableException();

    public void SetArrayValue(int index, in LuaValue value) => throw new UnreachableException();

    public Span<LuaValue> ArraySpan => Span<LuaValue>.Empty;

    public Memory<LuaValue> ArrayMemory => Memory<LuaValue>.Empty;

    public void EnsureArrayCapacityInPlace(int newCapacity) => throw new UnreachableException();
    public LuaValue RemoveAtArray(int index) => throw new UnreachableException();
    public void InsertArray(int index, in LuaValue value) => throw new UnreachableException();

    public int HashMapLiveCount => 0;

    public bool SlotHasKey(int slot, LuaValue key) => false;
    
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        pair = default;
        slot = -1;
        return false;
    }

    public bool TryGetNextFromSlot(int slot, out KeyValuePair<LuaValue, LuaValue> pair, out int nextSlot)
    {
        pair = default;
        nextSlot = -1;
        return false;
    }

    public void Clear() { }

    public ILuaTableStorage Clone() => this;

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) { }

    public int Version => 0;

    public bool MoveNext(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        current = default;
        return false;
    }
}
