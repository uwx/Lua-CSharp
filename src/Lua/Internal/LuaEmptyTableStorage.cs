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
    public bool HasFastStringPart => false;
    public int RawArrayLength => 0;

    public bool TryGetString(string key, out LuaValue value)
    {
        value = default;
        return false;
    }

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

    public ref LuaValue FindString(string key) => ref System.Runtime.CompilerServices.Unsafe.NullRef<LuaValue>();
    public ref LuaValue FindArray(int index) => ref System.Runtime.CompilerServices.Unsafe.NullRef<LuaValue>();
    public ref LuaValue FindGeneric(in LuaValue key) => ref System.Runtime.CompilerServices.Unsafe.NullRef<LuaValue>();

    public void SetString(string key, in LuaValue value) => throw new UnreachableException();
    public void SetArrayGrowing(int index, in LuaValue value) => throw new UnreachableException();
    public void SetGeneric(in LuaValue key, in LuaValue value) => throw new UnreachableException();

    public Span<LuaValue> ArraySpan() => Span<LuaValue>.Empty;
    public Memory<LuaValue> ArrayMemory() => Memory<LuaValue>.Empty;
    public void EnsureArrayCapacityInPlace(int newCapacity) => throw new UnreachableException();
    public LuaValue RemoveAtArray(int index) => throw new UnreachableException();
    public void InsertArray(int index, in LuaValue value) => throw new UnreachableException();

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

    public void Clear() { }

    public ILuaTableStorage Clone() => this;

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
}
