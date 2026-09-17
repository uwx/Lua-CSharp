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
/// found" when the kind structurally cannot hold that sort of key. Writes (<see cref="SetValue"/>,
/// <see cref="SetArrayValue"/>) are the opposite -- <see cref="LuaTable"/> promotes the storage to a
/// kind that can hold the write <em>before</em> calling them, so a concrete implementation that
/// structurally cannot support the call throws <see cref="UnreachableException"/> rather than silently
/// discarding the write.
/// </para>
/// <para>
/// The three walk primitives -- <see cref="TryGetNext"/> (Lua <c>next</c>),
/// <see cref="TryGetNextFromSlot"/> (Lua <c>next</c> resumed from a known slot) and
/// <see cref="MoveNext"/> (enumeration) -- each own their <em>whole</em> walk, array part included,
/// rather than leaving the caller to splice an array scan onto a hash walk. A storage knows how its own
/// array is kept (a heap array, an inline buffer, or absent entirely), so that is the only place the
/// walk can be written without hardcoding one of those layouts.
/// </para>
/// </summary>
interface ILuaTableStorage
{
    LuaTableStorageKind Kind { get; }

    /// <summary>Current array-part capacity, used by <see cref="LuaTable"/> for the array-vs-dictionary
    /// growth heuristic (today's <c>MaxDistance</c>/<c>MaxArraySize</c> math). Always 16 for
    /// <see cref="LuaSmallArrayTableStorage"/>; 0 for kinds with no array part at all.</summary>
    int RawArrayLength { get; }

    bool TryGetValue(in LuaValue key, out LuaValue value);

    ref LuaValue FindValue(in LuaValue key);

    void SetValue(in LuaValue key, in LuaValue value);

    /// <summary>
    /// Writes 1-origin array slot <paramref name="index"/>, growing the array part to hold it if
    /// needed. Separate from <see cref="SetValue"/> because the indexer has already unwrapped the key
    /// to an <see cref="int"/> by the time it gets here: routing that back through a
    /// <see cref="LuaValue"/> only to unwrap it again would be pure overhead, and on
    /// <see cref="LuaValueTableStorage"/> it would be wrong rather than merely slow
    /// (<see cref="SetValue"/> writes its dictionary there).
    /// <para>
    /// The growth is part of this call on purpose. The caller has already promoted the storage to a
    /// kind with an array part, so the only thing left to do is make room, and a separate
    /// <see cref="EnsureArrayCapacityInPlace"/> call would be a second interface dispatch on the
    /// hottest write path in the interpreter. On <see cref="LuaValueTableStorage"/> the growth is also
    /// where integer keys migrate out of the dictionary, so it has to happen here rather than in a
    /// caller that cannot see the dictionary.
    /// </para>
    /// </summary>
    void SetArrayValue(int index, in LuaValue value);

    /// <summary>Always safe: an empty span for a kind with no array part.</summary>
    Span<LuaValue> ArraySpan { get; }

    /// <summary>Only safe for the three heap-array-owning kinds. <see cref="LuaSmallArrayTableStorage"/>
    /// cannot support a real <see cref="Memory{T}"/> over its inline buffer, so
    /// <see cref="LuaTable.GetArrayMemory"/> promotes it to <see cref="LuaArrayTableStorage"/> before
    /// calling this.</summary>
    Memory<LuaValue> ArrayMemory { get; }

    /// <summary>Grows the array part in place to at least <paramref name="newCapacity"/> slots. A no-op
    /// on <see cref="LuaSmallArrayTableStorage"/> when <paramref name="newCapacity"/> &lt;= 16 (it never
    /// grows past that -- the caller promotes it away first).
    /// <para>
    /// For callers that need the room <em>without</em> writing then -- <c>SetList</c>, the compiler's
    /// template construction, <c>Dump</c>. A write goes through <see cref="SetArrayValue"/>, which
    /// grows for itself.</para></summary>
    void EnsureArrayCapacityInPlace(int newCapacity);

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    LuaValue RemoveAtArray(int index);

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    void InsertArray(int index, in LuaValue value);

    /// <summary>Live (non-nil) entry count across whichever hash part(s) this kind has.</summary>
    int HashMapLiveCount { get; }

    /// <summary>
    /// Lua <c>next(t, key)</c>, complete: the first live entry after <paramref name="key"/> in this
    /// kind's iteration order (array part first, then whichever hash part(s) it has). A nil
    /// <paramref name="key"/> starts the walk; an array-range integer resumes it in the array part; a
    /// hash key resumes it in the hash part. Anything the kind cannot hold at all has nothing after it,
    /// so it reports false.
    /// <para>
    /// <paramref name="slot"/> names the entry returned, in the same cursor space
    /// <see cref="SlotHasKey"/> and <see cref="TryGetNextFromSlot"/> take: it is what lets a caller
    /// that got an entry back resume from it without re-hashing its key. -1 for an array-part entry
    /// (there is no hash slot to resume from), for a kind with no hash part at all, and whenever this
    /// reports false.
    /// </para>
    /// </summary>
    bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot);

    /// <summary>True while <paramref name="slot"/> still holds <paramref name="key"/> -- the guard for
    /// a cursor cached by <see cref="TryGetNextFromSlot"/>. False for a kind with no hash part.</summary>
    bool SlotHasKey(int slot, LuaValue key);

    /// <summary>
    /// Resumes a hash-part traversal at <paramref name="slot"/>, which must be a slot
    /// <see cref="SlotHasKey"/> has just confirmed holds the control key: the first live entry
    /// <em>after</em> it. <paramref name="nextSlot"/> is -1 when there is none.
    /// </summary>
    bool TryGetNextFromSlot(int slot, out KeyValuePair<LuaValue, LuaValue> pair, out int nextSlot);

    void Clear();

    ILuaTableStorage Clone();

    /// <summary>The whole hash part (dead entries included) in insertion order, for template
    /// serialization. See the pre-split <c>LuaTable.CopyHashEntriesTo</c> for why dead entries matter.</summary>
    void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination);

    /// <summary>Version counter for <see cref="LuaTable.LuaTableEnumerator"/>'s modification check
    /// (0, and never changing, for a kind with no hash part).</summary>
    int Version { get; }

    /// <summary>
    /// Enumeration primitive for both phases, under one cursor: 0-based array slots in
    /// <c>[0, <see cref="RawArrayLength"/>)</c> first, then hash entries with the array length added to
    /// their own cursor. Advances <paramref name="index"/> past whatever it returns, skips dead (nil)
    /// entries itself, and reports false -- leaving <paramref name="current"/> default -- once the whole
    /// table is walked. Callable again after it has reported false.
    /// </summary>
    bool MoveNext(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current);
}
