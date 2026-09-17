using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lua.Internal;

namespace Lua;

public sealed class LuaTable : IEnumerable<KeyValuePair<LuaValue, LuaValue>>
{
    public LuaTable()
        : this(0, 0) { }

    public LuaTable(int arrayCapacity, int dictionaryCapacity)
    {
        storage = CreateInitialStorage(arrayCapacity, dictionaryCapacity);
        LuaTableDiagnostics.RecordTableCreated();
    }

    /// <summary>
    /// Used only by <see cref="CloneTemplate"/>: takes an already-built storage directly (a clone of
    /// a template's storage) without going through the public constructor, so a clone does not
    /// double-count against <see cref="LuaTableDiagnostics.RecordTableCreated"/> (the pre-split
    /// version of this class went through the public constructor for clones too, which double-counted
    /// them -- this fixes that as a side effect of the split).
    /// </summary>
    LuaTable(ILuaTableStorage storage)
    {
        this.storage = storage;
    }

    ILuaTableStorage storage;
    LuaTable? metatable;

    /// <summary>Test-only introspection hook for the storage-split promotion graph (see
    /// <c>LuaTableStorageTests</c>). Never used by production code -- everything else dispatches
    /// through <see cref="ILuaTableStorage"/> without inspecting <see cref="LuaTableStorageKind"/>
    /// from the outside.</summary>
    internal LuaTableStorageKind DebugStorageKind => storage.Kind;

    const int MaxArraySize = 1 << 24;
    const int MaxDistance = 1 << 12;

    /// <summary>
    /// Picks the narrowest concrete <see cref="ILuaTableStorage"/> a fresh table's constructor hints
    /// imply. See the storage-split plan's "Table constructors already know their exact shape"
    /// section: the compiler back-patches <c>NewTable</c>'s array/hash counts from a literal's exact
    /// field counts, so routing directly by count here (no compiler changes) gets template literals
    /// straight to their final shape with no promotion.
    /// </summary>
    static ILuaTableStorage CreateInitialStorage(int arrayCapacity, int dictionaryCapacity)
    {
        if (arrayCapacity <= 0 && dictionaryCapacity <= 0)
        {
            return LuaEmptyTableStorage.Instance;
        }

        if (dictionaryCapacity <= 0)
        {
            return arrayCapacity <= LuaSmallArrayTableStorage.Capacity
                ? new LuaSmallArrayTableStorage(arrayCapacity)
                : new LuaArrayTableStorage(arrayCapacity);
        }

        if (arrayCapacity <= 0)
        {
            return new LuaStringTableStorage(dictionaryCapacity);
        }

        return new LuaStringAndArrayTableStorage(arrayCapacity, dictionaryCapacity);
    }

    public LuaValue this[in LuaValue key]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (key.Type is LuaValueType.Nil)
            {
                ThrowIndexIsNil();
            }

            return storage.TryGetValue(key, out var value) ? value : LuaValue.Nil;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if (key.Type is LuaValueType.String)
            {
                storage = PromoteForStringWrite(storage);
                storage.SetValue(key, value);
                return;
            }

            if (key.TryReadNumber(out var d))
            {
                if (double.IsNaN(d))
                {
                    ThrowIndexIsNaN();
                }

                if (MathEx.IsInteger(d) && d <= int.MaxValue)
                {
                    var index = (int)d;
                    var capacity = storage.RawArrayLength;
                    var distance = index - capacity;

                    if (
                        distance <= MaxDistance
                        && 0 < index
                        && index < MaxArraySize
                        && index <= Math.Max(capacity * 2, 8)
                    )
                    {
                        storage = PromoteForArrayCapability(storage, index);
                        // The write grows the array part itself, so this stays one interface
                        // dispatch rather than two.
                        storage.SetArrayValue(index, value);
                        return;
                    }
                }
            }

            storage = PromoteForGenericWrite(storage);
            storage.SetValue(key, value);
        }
    }

    /// <summary>
    /// Promotes <paramref name="storage"/> so it can accept a write of a string key.
    /// Empty/SmallArray/Array all promote to a fast-string-part kind;
    /// String/StringAndArray already have one; Value is left as-is (it is handled specially by the
    /// caller, since it has no fast string part at all).
    /// </summary>
    static ILuaTableStorage PromoteForStringWrite(ILuaTableStorage storage)
    {
        return storage.Kind switch
        {
            LuaTableStorageKind.Empty => new LuaStringTableStorage(0),
            LuaTableStorageKind.SmallArray => LuaStringAndArrayTableStorage.FromSmallArray(
                (LuaSmallArrayTableStorage)storage
            ),
            LuaTableStorageKind.Array => LuaStringAndArrayTableStorage.FromArray((LuaArrayTableStorage)storage),
            _ => storage,
        };
    }

    /// <summary>
    /// Promotes <paramref name="storage"/> so it has an array part able to reach
    /// <paramref name="minCapacity"/>, without growing it there yet: the caller then grows it itself,
    /// via <see cref="ILuaTableStorage.SetArrayValue"/> (the indexer's array branch) or
    /// <see cref="ILuaTableStorage.EnsureArrayCapacityInPlace"/> (<see cref="EnsureArrayCapacity"/>).
    /// Used by both.
    /// </summary>
    static ILuaTableStorage PromoteForArrayCapability(ILuaTableStorage storage, int minCapacity)
    {
        return storage.Kind switch
        {
            LuaTableStorageKind.Empty => minCapacity <= LuaSmallArrayTableStorage.Capacity
                ? new LuaSmallArrayTableStorage()
                : LuaArrayTableStorage.FromEmpty(minCapacity),
            LuaTableStorageKind.SmallArray => minCapacity > LuaSmallArrayTableStorage.Capacity
                ? LuaArrayTableStorage.FromSmallArray((LuaSmallArrayTableStorage)storage, minCapacity)
                : storage,
            LuaTableStorageKind.String => LuaStringAndArrayTableStorage.FromString(
                (LuaStringTableStorage)storage,
                minCapacity
            ),
            _ => storage,
        };
    }

    /// <summary>Promotes <paramref name="storage"/> to the terminal <see cref="LuaValueTableStorage"/>,
    /// migrating whatever it already held. A no-op if it is already that kind.</summary>
    static ILuaTableStorage PromoteForGenericWrite(ILuaTableStorage storage)
    {
        return storage.Kind switch
        {
            LuaTableStorageKind.Empty => new LuaValueTableStorage(0, 0),
            LuaTableStorageKind.SmallArray => LuaValueTableStorage.FromSmallArray((LuaSmallArrayTableStorage)storage),
            LuaTableStorageKind.Array => LuaValueTableStorage.FromArray((LuaArrayTableStorage)storage),
            LuaTableStorageKind.String => LuaValueTableStorage.FromString((LuaStringTableStorage)storage),
            LuaTableStorageKind.StringAndArray => LuaValueTableStorage.FromStringAndArray(
                (LuaStringAndArrayTableStorage)storage
            ),
            _ => storage,
        };
    }

    public int HashMapCount => storage.HashMapLiveCount;

    public int ArrayLength
    {
        get
        {
            var a = storage.ArraySpan;
            var len = a.Length;
            if (len == 0)
            {
                return 0;
            }

            // Fast path: the array part is filled up to its size (last slot non-nil),
            // so its size is a valid border. This is the common case for arrays built
            // by append (`t[#t + 1] = ...`) or dense indexing (`t[i] = ...`), and makes
            // `#` O(1) instead of the previous O(n) linear scan.
            if (a[len - 1].Type is not LuaValueType.Nil)
            {
                return len;
            }

            // No element at key 1 (first slot nil) -> border is 0.
            if (a[0].Type is LuaValueType.Nil)
            {
                return 0;
            }

            // Otherwise binary-search for the largest n in [1..len] with a[n-1] non-nil
            // (the array border). O(log n); for a dense prefix this is exactly the old
            // "first nil" boundary, and for holey arrays it yields a valid Lua border.
            var lo = 1;
            var hi = len;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) >> 1;
                if (a[mid - 1].Type is not LuaValueType.Nil)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }
    }

    public LuaTable? Metatable
    {
        get => metatable;
        set => metatable = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        if (key.Type is LuaValueType.Nil)
        {
            value = default;
            return false;
        }

        return storage.TryGetValue(key, out value) && value.Type is not LuaValueType.Nil;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref LuaValue FindValue(in LuaValue key)
    {
        if (key.Type is LuaValueType.Nil)
        {
            ThrowIndexIsNil();
        }

        return ref storage.FindValue(key);
    }

    public bool ContainsKey(in LuaValue key)
    {
        return TryGetValue(key, out _);
    }

    public LuaValue RemoveAt(int index)
    {
        return storage.RemoveAtArray(index);
    }

    public void Insert(int index, in LuaValue value)
    {
        var capacity = storage.RawArrayLength;
        if (index <= 0 || index > capacity + 1)
        {
            throw new IndexOutOfRangeException();
        }

        storage = PromoteForArrayCapability(storage, index);

        // LuaSmallArrayTableStorage never grows past its fixed 16 slots -- if this insert would need
        // to grow it (either the position itself, or the shift an insert-in-the-middle causes, would
        // spill past slot 16), spill it to a heap array first, mirroring the pre-split
        // GrowArray(len+1) call that used to guard this same insert. This mirrors (without mutating)
        // the same "does this need to grow" check LuaSmallArrayTableStorage.InsertArray itself makes.
        if (storage is LuaSmallArrayTableStorage small)
        {
            var cap = small.RawArrayLength;
            var needsGrow = index > cap || (cap > 0 && small.ArraySpan[^1].Type != LuaValueType.Nil);
            if (needsGrow)
            {
                var target = cap + 1;
                var newCapacity = target <= 8 ? 8 : MathEx.NextPowerOfTwo(target);
                if (newCapacity > LuaSmallArrayTableStorage.Capacity)
                {
                    storage = LuaArrayTableStorage.FromSmallArray(small, newCapacity);
                }
            }
        }

        storage.InsertArray(index, value);
    }

    /// <summary>
    /// Lua `next` semantics. Iteration order is the array part, then whatever hash part(s) the storage
    /// has. The whole walk -- array scan, string part and generic part alike -- belongs to the storage
    /// (see <see cref="ILuaTableStorage.TryGetNext"/>): it is the only thing that knows how its own
    /// array is kept, and this is an interface dispatch either way.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair)
    {
        return storage.TryGetNext(key, out pair, out _);
    }

    /// <summary>
    /// <see cref="TryGetNext(in LuaValue, out KeyValuePair{LuaValue, LuaValue})"/> plus the cursor of
    /// the entry it returned -- whatever <see cref="SlotStillHolds"/> and
    /// <see cref="TryNextFromSlot"/> take, or -1 when the entry has none (an array-part entry, or a
    /// kind with no hash part). This is what lets <c>next</c> resume without re-hashing the key it was
    /// just handed: the VM hands that cursor back on the following step.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        return storage.TryGetNext(key, out pair, out slot);
    }

    /// <summary>
    /// Resumes a traversal at <paramref name="slot"/>: the first non-nil entry after it.
    /// Lets a caller that already knows which slot a key occupies skip hashing it again.
    /// nextSlot is -1 when done.
    /// </summary>
    internal bool TryNextFromSlot(
        int slot,
        out KeyValuePair<LuaValue, LuaValue> pair,
        out int nextSlot
    )
    {
        return storage.TryGetNextFromSlot(slot, out pair, out nextSlot);
    }

    /// <summary>
    /// True while <paramref name="slot"/> still holds exactly <paramref name="key"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SlotStillHolds(int slot, LuaValue key)
    {
        return storage.SlotHasKey(slot, key);
    }

    public void Clear()
    {
        storage.Clear();
    }

    public Memory<LuaValue> GetArrayMemory()
    {
        // A LuaSmallArrayTableStorage's inline buffer cannot back a real Memory<LuaValue> (nothing to
        // pin it against across calls), so promote to a heap array first. GetArraySpan never needs
        // this -- a Span<LuaValue> can point directly at the inline buffer for the call's duration.
        if (storage is LuaSmallArrayTableStorage small)
        {
            storage = LuaArrayTableStorage.FromSmallArray(small, small.RawArrayLength);
        }

        return storage.ArrayMemory;
    }

    public Span<LuaValue> GetArraySpan()
    {
        return storage.ArraySpan;
    }

    internal void EnsureArrayCapacity(int newCapacity)
    {
        if (newCapacity <= storage.RawArrayLength)
        {
            return;
        }

        storage = PromoteForArrayCapability(storage, newCapacity);
        storage.EnsureArrayCapacityInPlace(newCapacity);
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: the independent copy <see cref="Lua.Runtime.OpCode.DupTable"/> stores
    /// into its destination register, of the all-constant template that instruction names. One
    /// <c>Clone()</c> of the storage -- array and hash parts alike -- so the copy is identical to
    /// the template at its exact capacities, which is what makes a fused literal indistinguishable
    /// from the unfused NewTable + per-field writes that built the template.
    ///
    /// No metatable is copied (a template never has one) and no metamethod can fire: the copy is
    /// fresh with no live entries, so <c>__index</c>/<c>__newindex</c> are unreachable and
    /// <see cref="MetamethodCache"/> needs no invalidation -- even a template field literally named
    /// <c>__index</c> is inert on a metatable-less table.
    /// </summary>
    internal LuaTable CloneTemplate()
    {
        return new LuaTable(storage.Clone());
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: the hash part only, in insertion order, for serializing a
    /// template (<c>Dump.cs</c>). The two dictionaries are copied directly rather than walked
    /// through <see cref="GetEnumerator"/> on purpose: that enumerator interleaves the array part,
    /// and deciding "is this key an array slot?" from outside would mean duplicating the indexer's
    /// key routing. Nothing here can be an array-range integer key -- writing one routes it to the
    /// array, and growing the array migrates any the array later grows over -- so a caller can
    /// write this list alongside <see cref="GetArraySpan"/> without the two overlapping.
    /// <para>
    /// This is the <em>whole</em> hash part, dead (nil-valued) entries included, which is why it
    /// cannot use the public enumerator: that one skips them (they are what <c>pairs</c> must not
    /// expose), but a serialized template has to reproduce the table's actual contents rather than
    /// its iteration view. Dropping a dead entry changes what <c>next(t, k)</c> returns for a key
    /// the reader wrote as nil -- a live-table-empty answer instead of the entry that follows it --
    /// so the reconstructed template would not be the template.
    /// </para>
    /// </summary>
    internal void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination)
    {
        storage.CopyHashEntriesTo(destination);
    }

    [DoesNotReturn]
    static void ThrowIndexIsNil()
    {
        throw new ArgumentException("the table index is nil");
    }

    [DoesNotReturn]
    static void ThrowIndexIsNaN()
    {
        throw new ArgumentException("the table index is NaN");
    }

    public LuaTableEnumerator GetEnumerator()
    {
        return new(this);
    }

    IEnumerator<KeyValuePair<LuaValue, LuaValue>> IEnumerable<
        KeyValuePair<LuaValue, LuaValue>
    >.GetEnumerator()
    {
        return new LuaTableEnumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return new LuaTableEnumerator(this);
    }

    public struct LuaTableEnumerator(LuaTable table) : IEnumerator<KeyValuePair<LuaValue, LuaValue>>
    {
        public KeyValuePair<LuaValue, LuaValue> Current => current;

        /// <summary>One cursor covering both phases, owned by the storage: 0-based array slots first
        /// (so the key is <c>index + 1</c>), then hash entries, which the storage offsets past its array
        /// length. See <see cref="ILuaTableStorage.MoveNext"/>.</summary>
        int index = 0;
        readonly int version = table.storage.Version;
        KeyValuePair<LuaValue, LuaValue> current = default;

        public bool MoveNext()
        {
            // Table.storage is re-read every step (never cached): a write during iteration can promote
            // it, and the cursor above keeps its meaning across the swap.
            return table.storage.MoveNext(version, ref index, out current);
        }

        public void Reset()
        {
            index = 0;
        }

        object IEnumerator.Current => Current;

        public void Dispose() { }
    }
}
