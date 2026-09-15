using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>
/// The string-keyed hash part of a <see cref="LuaTable"/>, laid out with the
/// signature-bucket scheme popularized by Faster.Map's BlitzMap: a bucket array kept
/// separate from the entries it indexes, where each bucket packs an entry's
/// <em>signature</em> (the bits of the hash above the home mask) together with that
/// entry's slot into one 32-bit word, and links to the next bucket of the same home
/// index in the other 32 bits.
///
/// Specialized for <c>string</c> keys: an entry stores a bare string reference instead
/// of a full <see cref="LuaValue"/>, which keeps per-entry size at 48 bytes. Since
/// nearly every table in real Lua code is string-keyed (object/record-shaped), this is
/// the representation the overwhelming majority of hash entries live in; non-string,
/// non-array keys fall back to a lazily-created <see cref="LuaValueDictionary"/>.
///
/// This is a mutable struct embedded directly in LuaTable so a string-only table
/// costs no separate dictionary object. It must only ever be accessed through
/// that field (never copied); an uninitialized instance has <c>_buckets == null</c>
/// and allocates its arrays on first insert.
/// </summary>
/// <remarks>
/// Invariants, all of which the XOR signature filter in <see cref="FindValue"/> needs:
/// <list type="bullet">
/// <item>Every bucket in one chain shares the same home index, so a lookup only ever
/// walks the chain for <c>hash &amp; mask</c>.</item>
/// <item>A bucket word is <c>next &lt;&lt; 32 | (signature | slot)</c>. The signature is
/// <c>hash &amp; ~mask</c>, i.e. a multiple of the power-of-two length, so its low bits
/// are zero and a slot fits in them.</item>
/// <item>The length is at least 2, so a signature is always even and can never be
/// <see cref="Inactive"/>. A live bucket therefore never looks empty, which is what
/// makes the all-ones word a safe marker for an empty bucket.</item>
/// <item>At most 4/5 of the bucket array can hold entries, so
/// <see cref="FindEmptyBucket"/> is guaranteed to find a free bucket.</item>
/// </list>
/// </remarks>
struct LuaStringDictionary
{
    /// <summary>Bits 0..31 hold <c>signature | slot</c>; bits 32..63 hold the next bucket.</summary>
    ulong[]? _buckets;

    /// <summary>
    /// Entries in slot order: slot <c>i</c> is pointed at by whichever bucket carries
    /// <c>signature | i</c>. Always compact -- <c>[0, _count)</c> is in use and removal
    /// swaps the last slot into the hole, so there is no free list to walk.
    /// </summary>
    Entry[]? _entries;

    int _count;
    int _version;

    /// <summary>Bucket-array length: always a power of two and at least 2.</summary>
    int _length;

    /// <summary>
    /// How many entries fit before the table grows (<c>_length * 4 / 5</c>). Always
    /// equal to <c>_entries.Length</c>.
    /// </summary>
    int _maxCount;

    /// <summary>Cursor for <see cref="FindEmptyBucket"/>'s scan fallback.</summary>
    uint _last;

    const uint Inactive = 0xFFFF_FFFF;
    const ulong EmptyBucket = 0xFFFF_FFFF_FFFF_FFFF;
    const ulong LowWordMask = 0x0000_0000_FFFF_FFFF;
    const ulong HighWordMask = 0xFFFF_FFFF_0000_0000;

    /// <summary>Probes tried near the home index before falling back to a scan.</summary>
    const uint QuadraticProbeLength = 6;

    /// <summary>Entries may fill 4/5 of the bucket array (the load factor).</summary>
    const int LoadFactorNumerator = 4;
    const int LoadFactorDenominator = 5;

    public LuaStringDictionary(int capacity)
    {
        _buckets = null;
        _entries = null;
        _count = 0;
        _version = 0;
        _length = 0;
        _maxCount = 0;
        _last = 0;

        if (capacity > 0)
        {
            Initialize(capacity);
        }
    }

    public readonly int Count => _count;

    public readonly int Version => _version;

    public readonly bool IsAllocated => _buckets != null;

    /// <summary>
    /// Number of entries whose value is not nil. Assigning nil keeps the entry (so a
    /// <c>next</c> call handed that key can still find it) and only marks it dead, so
    /// liveness cannot be read off <see cref="Count"/>.
    /// </summary>
    public readonly int LiveCount
    {
        get
        {
            var entries = _entries;
            if (entries is null)
            {
                return 0;
            }

            var live = 0;
            for (var i = 0; i < _count; i++)
            {
                if (entries[i].value.Type is not LuaValueType.Nil)
                {
                    live++;
                }
            }

            return live;
        }
    }

    public void Clear()
    {
        var count = _count;
        if (count == 0)
        {
            return;
        }

        _count = 0;
        _last = 0;
        Array.Clear(_entries!, 0, count);
        _buckets!.AsSpan().Fill(EmptyBucket);
    }

    public readonly bool ContainsValue(LuaValue value)
    {
        var entries = _entries;
        if (entries is null)
        {
            return false;
        }

        for (var i = 0; i < _count; i++)
        {
            if (entries[i].value.Equals(value))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint ComputeHash(string key)
    {
        return (uint)key.GetHashCode();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool KeyEquals(string stored, string key)
    {
        // string == does a reference check first, so interned keys short-circuit.
        return stored == key;
    }

    /// <summary>
    /// Finds <paramref name="key"/>. On a hit returns a reference to the stored value and
    /// reports its slot through <paramref name="index"/>; on a miss returns
    /// <see cref="Unsafe.NullRef{T}"/> and sets <paramref name="index"/> to -1.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal ref LuaValue FindValue(string key, out int index)
    {
        index = -1;

        var buckets = _buckets;
        if (buckets is null)
        {
            return ref Unsafe.NullRef<LuaValue>();
        }

        Debug.Assert(_entries != null, "expected entries to be != null");

        var mask = (uint)(_length - 1);
        var hash = ComputeHash(key);
        var signature = hash & ~mask;
        var entries = _entries;

        var head = buckets[hash & mask];
        if ((uint)head == Inactive)
        {
            return ref Unsafe.NullRef<LuaValue>();
        }

        // XOR-ing the bucket's packed signature|slot with the wanted signature yields the
        // slot when the signatures match, and something strictly above the mask when they
        // do not -- so a signature mismatch costs no entries[] access at all. Because both
        // signatures are mask-aligned, the filter is exact rather than probabilistic.
        var slot = (uint)head ^ signature;
        if (slot <= mask && KeyEquals(entries[slot].key, key))
        {
            index = (int)slot;
            return ref entries[slot].value;
        }

        // Software pipelining: pull the next link out before testing this node so the CPU
        // can start the next load while this comparison is still in flight.
        var next = (uint)(head >> 32);
        while (next != Inactive)
        {
            var node = buckets[next];
            next = (uint)(node >> 32);

            slot = (uint)node ^ signature;
            if (slot <= mask && KeyEquals(entries[slot].key, key))
            {
                index = (int)slot;
                return ref entries[slot].value;
            }
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    void Initialize(int capacity)
    {
        // Bucket indexing uses `hash & (length - 1)`, so the length stays a power of two, and
        // the load factor has to stay at or below 4/5 so FindEmptyBucket always has slack.
        // That makes the bucket array the smallest power of two holding 5/4 of the requested
        // capacity. The entries array is sized to exactly `capacity` instead: over-sizing it
        // to the buckets' full load-factor capacity would allocate for entries the caller
        // never asked for, which costs far more than the bucket slack it saves. Floor the
        // bucket array at 2 -- a single bucket would give every signature the same home
        // index.
        var length = 2;
        while ((long)length * LoadFactorNumerator < (long)capacity * LoadFactorDenominator)
        {
            length *= 2;
        }

        _length = length;
        _maxCount = Math.Max(capacity, 1);
        _last = 0;
        _buckets = new ulong[length];
        _buckets.AsSpan().Fill(EmptyBucket);
        _entries = new Entry[_maxCount];
    }

    /// <summary>
    /// Inserts <paramref name="key"/> or overwrites the value already stored for it. Lua
    /// tables keep nil-valued keys rather than removing them, so nil is stored like any
    /// other value and merely makes the entry invisible to lookups and iteration.
    /// </summary>
    public void Insert(string key, LuaValue value)
    {
        var __diagStart = Stopwatch.GetTimestamp();
        try
        {
            InsertCore(key, value);
        }
        finally
        {
            LuaTableDiagnostics.RecordStringInsert(Stopwatch.GetTimestamp() - __diagStart);
        }
    }

    void InsertCore(string key, LuaValue value)
    {
        if (MetamethodCache.IsMetamethodKey(key))
        {
            MetamethodCache.Invalidate();
        }

        if (_buckets is null)
        {
            Initialize(0);
        }

        // Growth is deferred until the key is known to be NEW (see the two Resize calls
        // below): resizing is only legal once the probe has established that this is not an
        // overwrite, otherwise a table sitting exactly at its capacity would double -- and
        // re-hash every entry -- merely because an existing key was assigned again.
        // The retry is guaranteed to terminate: Resize always leaves `_count < _maxCount`.
        Retry:
        var mask = (uint)(_length - 1);
        var hash = ComputeHash(key);
        var main = hash & mask;
        var signature = hash & ~mask;

        var buckets = _buckets;
        var entries = _entries;
        Debug.Assert(buckets != null && entries != null, "expected arrays to be non-null");

        var head = buckets[main];
        var packed = (uint)head;

        if (packed != Inactive)
        {
            var occupant = (int)(packed & mask);

            // Test for an existing entry first. An update is the common case, and a hit here
            // means the occupant's home index is necessarily `main`, so the owner check below
            // -- which has to re-hash the stored key -- can be skipped entirely.
            if ((packed ^ signature) <= mask && KeyEquals(entries[occupant].key, key))
            {
                entries[occupant].value = value;
                return;
            }

            // The home bucket is taken by a different key. If that occupant does not belong
            // here, evict it to a bucket still reachable from its own home chain and take
            // this one -- that is what keeps "one chain, one home index" true, which the
            // signature filter in FindValue relies on.
            var owner = ComputeHash(entries[occupant].key) & mask;
            if (owner != main)
            {
                KickoutBucket(buckets, owner, main);
                packed = Inactive;
            }
        }

        if (packed == Inactive)
        {
            // The home bucket is free and the key was not found in it, so this is a new
            // key: only now can the table need to grow.
            if (_count == _maxCount)
            {
                Resize();
                goto Retry;
            }

            var slot = (uint)_count++;
            ref var entry = ref entries[slot];
            entry.key = key;
            entry.value = value;
            buckets[main] = ((ulong)Inactive << 32) | signature | slot;
            _version++;
            return;
        }

        // Walk the chain looking for the key, remembering the tail so a new entry can be
        // appended to it.
        var tail = main;
        var next = (uint)(head >> 32);
        while (next != Inactive)
        {
            var node = buckets[next];
            var slot = (uint)node ^ signature;
            if (slot <= mask && KeyEquals(entries[slot].key, key))
            {
                entries[slot].value = value;
                return;
            }

            tail = next;
            next = (uint)(node >> 32);
        }

        if (_count == _maxCount)
        {
            Resize();
            goto Retry;
        }

        var newBucket = FindEmptyBucket(buckets, main, 1);
        buckets[tail] = (buckets[tail] & LowWordMask) | ((ulong)newBucket << 32);

        var newSlot = (uint)_count++;
        ref var newEntry = ref entries[newSlot];
        newEntry.key = key;
        newEntry.value = value;
        buckets[newBucket] = ((ulong)Inactive << 32) | signature | newSlot;
        _version++;
    }

    void Resize()
    {
        var __diagStart = Stopwatch.GetTimestamp();
        try
        {
            ResizeCore();
        }
        finally
        {
            LuaTableDiagnostics.RecordStringResize(Stopwatch.GetTimestamp() - __diagStart);
        }
    }

    void ResizeCore()
    {
        var newLength = _length * 2;
        // The bucket array has to double anyway (its slots are what the probe sequence walks),
        // so sizing the entries at half the buckets makes the capacity ladder land on exact
        // powers of two -- the same ladder the array part uses. Filling 4/5 of the buckets
        // instead would only buy unused entry slots while overshooting the capacity a caller
        // actually needs by up to 60%.
        var newMaxCount = newLength / 2;

        var newBuckets = new ulong[newLength];
        newBuckets.AsSpan().Fill(EmptyBucket);

        var oldEntries = _entries!;
        var newEntries = new Entry[newMaxCount];
        Array.Copy(oldEntries, newEntries, _count);

        _length = newLength;
        _maxCount = newMaxCount;
        _buckets = newBuckets;
        _entries = newEntries;
        _last = 0;

        // Slots are preserved, so the iteration order is preserved too.
        var mask = (uint)(newLength - 1);
        for (var i = 0; i < _count; i++)
        {
            PlaceExistingEntry(newBuckets, newEntries, mask, (uint)i);
        }
    }

    public bool Remove(string key)
    {
        var buckets = _buckets;
        if (buckets is null)
        {
            return false;
        }

        Debug.Assert(_entries != null, "expected entries to be non-null");

        var mask = (uint)(_length - 1);
        var hash = ComputeHash(key);
        var main = hash & mask;
        var signature = hash & ~mask;
        var entries = _entries;

        var head = buckets[main];
        if ((uint)head == Inactive)
        {
            return false;
        }

        var slot = (uint)head ^ signature;
        if (slot <= mask && KeyEquals(entries[slot].key, key))
        {
            EraseBucket(buckets, main, main);
            EraseSlot(slot);
            return true;
        }

        var next = (uint)(head >> 32);
        while (next != Inactive)
        {
            var node = buckets[next];
            var nodeSlot = (uint)node ^ signature;
            if (nodeSlot <= mask && KeyEquals(entries[nodeSlot].key, key))
            {
                EraseBucket(buckets, next, main);
                EraseSlot(nodeSlot);
                return true;
            }

            next = (uint)(node >> 32);
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(string key, out LuaValue value)
    {
        ref var valRef = ref FindValue(key, out _);
        if (!Unsafe.IsNullRef(ref valRef))
        {
            value = valRef;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Finds the first non-nil entry after <paramref name="key"/>. <paramref name="found"/>
    /// reports whether <paramref name="key"/> itself was present, so a caller can tell
    /// "key was the last entry" (found, no result) apart from "key isn't in this table".
    /// </summary>
    public bool TryGetNext(string key, out KeyValuePair<LuaValue, LuaValue> pair, out bool found)
    {
        found = false;

        ref var valRef = ref FindValue(key, out var index);
        if (Unsafe.IsNullRef(ref valRef))
        {
            pair = default;
            return false;
        }

        found = true;
        return TryGetFirstFrom(index + 1, out pair);
    }

    /// <summary>First non-nil entry at or after <paramref name="index"/>.</summary>
    public bool TryGetFirstFrom(int index, out KeyValuePair<LuaValue, LuaValue> pair)
    {
        return TryGetFirstFrom(index, out pair, out _);
    }

    /// <summary>
    /// First non-nil entry at or after <paramref name="index"/>, also reporting the slot it
    /// lives in so a caller can resume iteration from it later without re-hashing the key.
    /// <paramref name="slot"/> is -1 when there is no such entry.
    /// </summary>
    public bool TryGetFirstFrom(
        int index,
        out KeyValuePair<LuaValue, LuaValue> pair,
        out int slot
    )
    {
        var entries = _entries;
        while ((uint)index < (uint)_count)
        {
            ref var entry = ref entries![index];
            if (entry.value.Type is not LuaValueType.Nil)
            {
                pair = new(entry.key, entry.value);
                slot = index;
                return true;
            }

            index++;
        }

        pair = default;
        slot = -1;
        return false;
    }

    /// <summary>
    /// True while <paramref name="slot"/> still holds exactly <paramref name="key"/> (by
    /// reference). Slots are stable under insert/overwrite/resize -- only a swap-erase
    /// removal or a clear can move or drop one -- so this lets a caller validate a cached
    /// cursor cheaply instead of re-hashing the key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool SlotHasKey(int slot, string key)
    {
        var entries = _entries;
        return entries != null
            && (uint)slot < (uint)_count
            && ReferenceEquals(entries[slot].key, key);
    }

    /// <summary>
    /// Appends <paramref name="slot"/> to the chain rooted at its key's home index,
    /// evicting an occupant that does not belong there. Only used by <see cref="Resize"/>,
    /// which rebuilds every bucket from scratch while keeping the slots -- and therefore
    /// the iteration order -- unchanged. Slots are already unique, so unlike
    /// <see cref="Insert"/> there is nothing to look up.
    /// </summary>
    void PlaceExistingEntry(ulong[] buckets, Entry[] entries, uint mask, uint slot)
    {
        var hash = ComputeHash(entries[slot].key);
        var main = hash & mask;
        var signature = hash & ~mask;

        var packed = (uint)buckets[main];
        if (packed != Inactive)
        {
            var occupant = packed & mask;
            var owner = ComputeHash(entries[occupant].key) & mask;
            if (owner != main)
            {
                KickoutBucket(buckets, owner, main);
                packed = Inactive;
            }
        }

        if (packed == Inactive)
        {
            buckets[main] = ((ulong)Inactive << 32) | signature | slot;
            return;
        }

        var tail = main;
        var next = (uint)(buckets[main] >> 32);
        while (next != Inactive)
        {
            tail = next;
            next = (uint)(buckets[next] >> 32);
        }

        var newBucket = FindEmptyBucket(buckets, main, 1);
        buckets[tail] = (buckets[tail] & LowWordMask) | ((ulong)newBucket << 32);
        buckets[newBucket] = ((ulong)Inactive << 32) | signature | slot;
    }

    /// <summary>
    /// Finds the bucket whose packed slot is <paramref name="targetSlot"/>, so
    /// <see cref="EraseSlot"/> can repoint it after a swap-erase.
    /// </summary>
    uint FindBucketForSlot(ulong[] buckets, uint mask, string key, uint targetSlot)
    {
        var main = ComputeHash(key) & mask;

        var packed = (uint)buckets[main];
        // An empty bucket's packed word is Inactive, whose low bits happen to equal the
        // mask -- hence the explicit emptiness test rather than comparing slots alone.
        if (packed != Inactive && (packed & mask) == targetSlot)
        {
            return main;
        }

        var next = (uint)(buckets[main] >> 32);
        while (next != Inactive)
        {
            packed = (uint)buckets[next];
            if (packed != Inactive && (packed & mask) == targetSlot)
            {
                return next;
            }

            next = (uint)(buckets[next] >> 32);
        }

        ThrowHelper.ThrowInvalidOperationException_MapStateCorrupted();
        return 0;
    }

    /// <summary>
    /// Unlinks the bucket node at <paramref name="bucket"/> from the chain rooted at
    /// <paramref name="main"/>. When the node is the chain's root and has a successor, the
    /// successor is promoted into the root rather than leaving a hole in the chain.
    /// </summary>
    static void EraseBucket(ulong[] buckets, uint bucket, uint main)
    {
        var next = (uint)(buckets[bucket] >> 32);

        if (bucket == main)
        {
            if (next == Inactive)
            {
                buckets[bucket] = EmptyBucket;
                return;
            }

            buckets[bucket] = buckets[next];
            buckets[next] = EmptyBucket;
            return;
        }

        var prev = FindPrevBucket(buckets, main, bucket);
        buckets[prev] = (buckets[prev] & LowWordMask) | ((ulong)next << 32);
        buckets[bucket] = EmptyBucket;
    }

    /// <summary>
    /// Removes <paramref name="slot"/> by swapping the last slot into it. The moved
    /// entry's bucket still points at the old slot, so it has to be repointed -- which is
    /// why the caller must unlink the bucket node first.
    /// </summary>
    void EraseSlot(uint slot)
    {
        var entries = _entries!;
        var buckets = _buckets!;
        var mask = (uint)(_length - 1);
        var lastSlot = (uint)--_count;

        if (slot == lastSlot)
        {
            entries[slot] = default;
            return;
        }

        var moved = entries[lastSlot];
        entries[slot] = moved;
        entries[lastSlot] = default;

        var movedBucket = FindBucketForSlot(buckets, mask, moved.key, lastSlot);
        var node = buckets[movedBucket];
        buckets[movedBucket] = (node & HighWordMask) | ((uint)node & ~mask) | slot;
    }

    /// <summary>
    /// Moves <paramref name="bucket"/> out of <paramref name="owner"/>'s chain into a free
    /// bucket that is linked back into it, preserving the moved node's payload and its own
    /// chain. That frees <paramref name="bucket"/> for a new entry whose home index really
    /// is <paramref name="bucket"/>.
    /// </summary>
    void KickoutBucket(ulong[] buckets, uint owner, uint bucket)
    {
        var victim = buckets[bucket];
        var next = (uint)(victim >> 32);

        // Search near the victim's own continuation so the relocated node stays close to
        // the chain it is about to join.
        var newBucket = FindEmptyBucket(buckets, next == Inactive ? bucket : next, 2);
        var prev = FindPrevBucket(buckets, owner, bucket);

        buckets[newBucket] = victim;
        buckets[prev] = (buckets[prev] & LowWordMask) | ((ulong)newBucket << 32);
        buckets[bucket] = EmptyBucket;
    }

    static uint FindPrevBucket(ulong[] buckets, uint main, uint target)
    {
        var current = main;
        while (true)
        {
            var next = (uint)(buckets[current] >> 32);
            if (next == Inactive)
            {
                ThrowHelper.ThrowInvalidOperationException_MapStateCorrupted();
                return 0;
            }

            if (next == target)
            {
                return current;
            }

            current = next;
        }
    }

    /// <summary>
    /// Finds a free bucket for a chain rooted at <paramref name="index"/>: two adjacent
    /// probes near the home index, then a short quadratic run, then a linear scan from
    /// <see cref="_last"/>. Always terminates, because the load factor keeps at least 1/5
    /// of the bucket array free.
    /// </summary>
    uint FindEmptyBucket(ulong[] buckets, uint index, uint cint)
    {
        var mask = (uint)(_length - 1);
        var baseIndex = index & mask;

        var bucket = (baseIndex + 1) & mask;
        if (buckets[bucket] == EmptyBucket)
        {
            return bucket;
        }

        var next = (bucket + 1) & mask;
        if (buckets[next] == EmptyBucket)
        {
            return next;
        }

        var n = 1u;
        var t = 1u; // running sum of the quadratic offsets
        while (n < QuadraticProbeLength)
        {
            bucket = (baseIndex + t + cint) & mask;
            if (buckets[bucket] == EmptyBucket)
            {
                return bucket;
            }

            next = (bucket + 1) & mask;
            if (buckets[next] == EmptyBucket)
            {
                return next;
            }

            n++;
            t += n;
        }

        var last = _last;
        var stride = (uint)(_length >> 1);
        while (true)
        {
            last = (last + 1) & mask;
            if (buckets[last] == EmptyBucket)
            {
                _last = last;
                return last;
            }

            last = (last + 1) & mask;
            if (buckets[last] == EmptyBucket)
            {
                _last = last;
                return last;
            }

            var medium = (last + stride) & mask;
            if (buckets[medium] == EmptyBucket)
            {
                _last = medium;
                return medium;
            }
        }
    }

    struct Entry
    {
        public string key;
        public LuaValue value;
    }

    internal static bool MoveNext(
        ref LuaStringDictionary dictionary,
        int version,
        ref int index,
        out KeyValuePair<LuaValue, LuaValue> current
    )
    {
        if (version != dictionary._version)
        {
            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
        }

        var entries = dictionary._entries;
        while ((uint)index < (uint)dictionary._count)
        {
            ref var entry = ref entries![index++];
            if (entry.value.Type is not LuaValueType.Nil)
            {
                current = new(entry.key, entry.value);
                return true;
            }
        }

        index = dictionary._count + 1;
        current = default;
        return false;
    }

    static class ThrowHelper
    {
        public static void ThrowInvalidOperationException_ConcurrentOperationsNotSupported()
        {
            throw new InvalidOperationException("Concurrent operations are not supported");
        }

        public static void ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion()
        {
            throw new InvalidOperationException(
                "Collection was modified after the enumerator was instantiated."
            );
        }

        public static void ThrowInvalidOperationException_MapStateCorrupted()
        {
            throw new InvalidOperationException("Hash map state is corrupted");
        }
    }
}
