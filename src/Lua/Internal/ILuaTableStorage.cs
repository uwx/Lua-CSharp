using System.Diagnostics;

namespace Lua.Internal;

/// <summary>Which concrete <see cref="ILuaTableStorage"/> a <see cref="LuaTable"/> currently holds.</summary>
enum LuaTableStorageKind
{
    Empty,
    SmallArray,
    Array,
    String,
    StringAndArray,
    Value,
}

/// <summary>
/// A <see cref="LuaTable"/>'s backing storage. See
/// <c>Lua-CSharp/src/Lua/LuaTable.cs</c> and the storage-split plan for the full promotion graph.
/// <para>
/// Reads (<c>TryGet*</c>, <c>Find*</c>) are always safe to call on any kind: they simply report "not
/// found" when the kind structurally cannot hold that sort of key. Writes (<c>SetString</c>,
/// <c>SetArrayGrowing</c>, <c>SetGeneric</c>) are the opposite -- <see cref="LuaTable"/> promotes the
/// storage to a kind that can hold the write <em>before</em> calling them, so a concrete
/// implementation that structurally cannot support the call throws
/// <see cref="UnreachableException"/> rather than silently discarding the write.
/// </para>
/// </summary>
interface ILuaTableStorage
{
    LuaTableStorageKind Kind { get; }

    /// <summary>True for <see cref="LuaStringTableStorage"/> and <see cref="LuaStringAndArrayTableStorage"/>
    /// -- the two kinds with a dedicated string-keyed hash part. False for everything else, including
    /// <see cref="LuaValueTableStorage"/>, which stores string keys in its generic dictionary instead
    /// (see the plan's "no dedicated fast string path" confirmed decision).</summary>
    bool HasFastStringPart { get; }

    /// <summary>Current array-part capacity, used by <see cref="LuaTable"/> for the array-vs-dictionary
    /// growth heuristic (today's <c>MaxDistance</c>/<c>MaxArraySize</c> math). Always 16 for
    /// <see cref="LuaSmallArrayTableStorage"/>; 0 for kinds with no array part at all.</summary>
    int RawArrayLength { get; }

    bool TryGetString(string key, out LuaValue value);

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    bool TryGetArray(int index, out LuaValue value);

    bool TryGetGeneric(in LuaValue key, out LuaValue value);

    ref LuaValue FindString(string key);

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    ref LuaValue FindArray(int index);

    ref LuaValue FindGeneric(in LuaValue key);

    /// <summary>Inserts/overwrites a string key. Caller (<see cref="LuaTable"/>) has already promoted
    /// to a kind with a fast string part, or -- for <see cref="LuaValueTableStorage"/> -- routes string
    /// writes through <see cref="SetGeneric"/> instead of this method.</summary>
    void SetString(string key, in LuaValue value);

    /// <summary>Writes an array-range integer key. <paramref name="index"/> (1-origin) must already fit
    /// within the storage's current array capacity -- the caller calls
    /// <see cref="EnsureArrayCapacityInPlace"/> (after promoting if needed) first.</summary>
    void SetArrayGrowing(int index, in LuaValue value);

    void SetGeneric(in LuaValue key, in LuaValue value);

    /// <summary>Always safe: an empty span for a kind with no array part.</summary>
    Span<LuaValue> ArraySpan();

    /// <summary>Only safe for the three heap-array-owning kinds. <see cref="LuaSmallArrayTableStorage"/>
    /// cannot support a real <see cref="Memory{T}"/> over its inline buffer, so
    /// <see cref="LuaTable.GetArrayMemory"/> promotes it to <see cref="LuaArrayTableStorage"/> before
    /// calling this.</summary>
    Memory<LuaValue> ArrayMemory();

    /// <summary>Grows the array part in place to at least <paramref name="newCapacity"/> slots. A no-op
    /// on <see cref="LuaSmallArrayTableStorage"/> when <paramref name="newCapacity"/> &lt;= 16 (it never
    /// grows past that -- the caller promotes it away first).</summary>
    void EnsureArrayCapacityInPlace(int newCapacity);

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    LuaValue RemoveAtArray(int index);

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    void InsertArray(int index, in LuaValue value);

    /// <summary>Live (non-nil) entry count across whichever hash part(s) this kind has.</summary>
    int HashMapLiveCount { get; }

    /// <summary>Mirrors <see cref="LuaStringDictionary.TryGetNext"/>: the first non-nil string entry
    /// after <paramref name="key"/>, also reporting whether <paramref name="key"/> itself was present.
    /// Only meaningful when <see cref="HasFastStringPart"/> is true.</summary>
    bool TryGetNextFromStringSlot(string key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot);

    /// <summary>Mirrors <see cref="LuaStringDictionary.TryGetFirstFrom(int, out KeyValuePair{LuaValue, LuaValue}, out int)"/>.</summary>
    bool TryGetFirstFromStringSlot(int index, out KeyValuePair<LuaValue, LuaValue> pair, out int slot);

    bool SlotHasKey(int slot, string key);

    bool TryGetNextGeneric(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair);

    bool TryGetFirstGeneric(out KeyValuePair<LuaValue, LuaValue> pair);

    void Clear();

    ILuaTableStorage Clone();

    /// <summary>The whole hash part (dead entries included) in insertion order, for template
    /// serialization. See the pre-split <c>LuaTable.CopyHashEntriesTo</c> for why dead entries matter.</summary>
    void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination);

    /// <summary>Version counter for the string part (0 / unchanging for a kind without one), for
    /// <see cref="LuaTable.LuaTableEnumerator"/>'s modification check.</summary>
    int StringVersion { get; }

    /// <summary>Version counter for the generic part (0 / unchanging for a kind without one).</summary>
    int GenericVersion { get; }

    /// <summary>Enumeration primitive for the string phase. A no-op (always returns false) on a kind
    /// with no string part.</summary>
    bool MoveNextString(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current);

    /// <summary>Enumeration primitive for the generic phase. A no-op (always returns false) on a kind
    /// with no generic part.</summary>
    bool MoveNextGeneric(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current);
}
