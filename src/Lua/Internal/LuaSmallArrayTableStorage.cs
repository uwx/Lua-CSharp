using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>
/// Up to 16 array elements held inline via <see cref="InlineArray16{T}"/> -- no heap array
/// allocation for small pure-array tables (Sx's <c>children</c> lists are almost always well under
/// 16 entries). Never grows past 16 slots: a write that would need more, or any string/generic key,
/// promotes to <see cref="LuaArrayTableStorage"/>, <see cref="LuaStringAndArrayTableStorage"/> or
/// <see cref="LuaValueTableStorage"/> instead (see the promotion graph in the storage-split plan).
/// A read via <see cref="LuaTable.GetArrayMemory"/> also promotes (to <see cref="LuaArrayTableStorage"/>)
/// since a real <see cref="Memory{T}"/> cannot point into this inline buffer.
/// <para>
/// The <em>reported</em> capacity (<see cref="RawArrayLength"/>) follows the same "8, then next
/// power of two" growth ladder <see cref="LuaTableArrayPart"/> uses, capped at 16, rather than always
/// reporting 16 -- even though the inline buffer physically has all 16 slots from the start. This
/// matters: <see cref="LuaTable"/>'s array-vs-dictionary growth heuristic
/// (<c>index &lt;= Math.Max(capacity * 2, 8)</c>) compares against this reported capacity, and it has
/// to land on the same values a heap array would have at the same occupancy for that heuristic to
/// make the same array/dictionary placement decisions the pre-split implementation did (see
/// <c>LuaTableDictionaryTests.ArrayGrowth_PromotesHashPartIntegers_WithoutDuplicates</c>, which pins
/// this down: keys far enough past a length-8 array must land in the dictionary even though the
/// physical inline buffer could technically hold them).
/// </para>
/// </summary>
sealed class LuaSmallArrayTableStorage : ILuaTableStorage
{
    public const int Capacity = 16;

    InlineArray16<LuaValue> buffer;
    int usedCapacity;

    public LuaSmallArrayTableStorage() { }

    /// <summary>
    /// Used only by <see cref="LuaTable"/>'s constructor: an explicit capacity hint from the caller
    /// (e.g. the compiler's exact array-field count for a table literal) is taken as an exact
    /// reported capacity, matching the pre-split constructor's <c>array = new LuaValue[arrayCapacity]</c>
    /// -- unlike a later dynamic grow-on-write, which always rounds up to the "8, then next power of
    /// two" ladder via <see cref="EnsureArrayCapacityInPlace"/>.
    /// </summary>
    public LuaSmallArrayTableStorage(int exactInitialCapacity)
    {
        usedCapacity = exactInitialCapacity > 1 ? exactInitialCapacity : 0;
    }

    public LuaTableStorageKind Kind => LuaTableStorageKind.SmallArray;
    public int RawArrayLength => usedCapacity;

    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        if (LuaTableArrayPart.TryGetIndex(key, usedCapacity, out var index))
        {
            value = MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1);
            return true;
        }

        value = default;
        return false;
    }

    public ref LuaValue FindValue(in LuaValue key)
    {
        if (LuaTableArrayPart.TryGetIndex(key, usedCapacity, out var index))
        {
            return ref MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1);
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    /// <summary>Only ever reached with a key the caller has already decided fits (the indexer routes
    /// array-range keys to <see cref="SetArrayValue"/> and promotes everything else out of this kind), so
    /// the physical 16-slot bound is the whole check.</summary>
    public void SetValue(in LuaValue key, in LuaValue value)
    {
        if (LuaTableArrayPart.TryGetIndex(key, Capacity, out var index))
        {
            MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1) = value;
        }
    }

    /// <summary>Grows for the write itself. There is no allocation to make -- the inline buffer already
    /// has all 16 slots -- but <see cref="usedCapacity"/> has to advance along the same ladder
    /// <see cref="EnsureArrayCapacityInPlace"/> uses, because it is what
    /// <see cref="RawArrayLength"/> reports to the indexer's array/dictionary placement heuristic.</summary>
    public void SetArrayValue(int index, in LuaValue value)
    {
        EnsureArrayCapacityInPlace(index);
        MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1) = value;
    }

    public Span<LuaValue> ArraySpan => ((Span<LuaValue>)buffer)[..usedCapacity];

    public Memory<LuaValue> ArrayMemory =>
        throw new UnreachableException(
            "LuaTable must promote a LuaSmallArrayTableStorage before requesting a Memory<LuaValue>."
        );

    /// <summary>Grows <see cref="usedCapacity"/> along the same "8, then next power of two" ladder
    /// <see cref="LuaTableArrayPart.EnsureCapacity"/> uses, without touching the (already fully
    /// allocated) inline buffer. Never called with <paramref name="newCapacity"/> &gt; 16 -- the
    /// caller promotes to <see cref="LuaArrayTableStorage"/> first.</summary>
    public void EnsureArrayCapacityInPlace(int newCapacity)
    {
        if (usedCapacity >= newCapacity)
        {
            return;
        }

#if DEBUG
        if (newCapacity > Capacity)
        {
            Unreachable();
        }
#endif

        usedCapacity = newCapacity <= 8 ? 8 : MathEx.NextPowerOfTwo(newCapacity);
        return;

        [DoesNotReturn]
        static void Unreachable()
        {
            throw new UnreachableException(
                "LuaTable must promote a LuaSmallArrayTableStorage before growing past 16 slots."
            );
        }
    }

    public LuaValue RemoveAtArray(int index)
    {
        var arrayIndex = index - 1;
        var span = ArraySpan;
        var value = span[arrayIndex];

        if (arrayIndex < span.Length - 1)
        {
            span[(arrayIndex + 1)..].CopyTo(span[arrayIndex..]);
        }

        span[^1] = default;
        return value;
    }

    public void InsertArray(int index, in LuaValue value)
    {
        if (index <= 0 || index > usedCapacity + 1)
        {
            throw new IndexOutOfRangeException();
        }

        if (index > usedCapacity || (usedCapacity > 0 && buffer[usedCapacity - 1].Type != LuaValueType.Nil))
        {
            EnsureArrayCapacityInPlace(usedCapacity + 1);
        }

        var span = ArraySpan;
        var arrayIndex = index - 1;
        if (arrayIndex != span.Length - 1)
        {
            span[arrayIndex..^1].CopyTo(span[(arrayIndex + 1)..]);
        }

        span[arrayIndex] = value;
    }

    public int HashMapLiveCount => 0;

    public bool SlotHasKey(int slot, LuaValue key) => false;

    /// <summary>Array-only: nil resumes at the first slot, an array-range integer just past it. Nothing
    /// else has a position in this kind, so nothing else has a successor either. The slot is always -1
    /// -- there is no hash part to hold a cursor into, and an array entry needs none (its own integer
    /// key is its position).</summary>
    public bool TryGetNext(in LuaValue key, out KeyValuePair<LuaValue, LuaValue> pair, out int slot)
    {
        slot = -1;
        if (!LuaTableArrayPart.TryGetNextStart(key, usedCapacity, out var start))
        {
            pair = default;
            return false;
        }

        var span = ArraySpan;
        for (var i = start; i < span.Length; i++)
        {
            if (span[i].Type is not LuaValueType.Nil)
            {
                pair = new(i + 1, span[i]);
                return true;
            }
        }

        pair = default;
        return false;
    }

    public bool TryGetNextFromSlot(int slot, out KeyValuePair<LuaValue, LuaValue> pair, out int nextSlot)
    {
        pair = default;
        nextSlot = -1;
        return false;
    }

    public void Clear() => ((Span<LuaValue>)buffer).Clear();

    public ILuaTableStorage Clone()
    {
        var clone = new LuaSmallArrayTableStorage { usedCapacity = usedCapacity };
        ((ReadOnlySpan<LuaValue>)buffer).CopyTo(clone.buffer);
        return clone;
    }

    public void CopyHashEntriesTo(List<KeyValuePair<LuaValue, LuaValue>> destination) { }

    public int Version => 0;

    /// <summary>The whole array part, under the same 0-based-slot cursor
    /// <see cref="LuaTableArrayPart.MoveNext"/> uses (there is no hash part after it to carry on into).</summary>
    public bool MoveNext(int expectedVersion, ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        var span = ArraySpan;
        for (var i = index; i < span.Length; i++)
        {
            if (span[i].Type is not LuaValueType.Nil)
            {
                index = i + 1;
                current = new(i + 1, span[i]);
                return true;
            }
        }

        index = span.Length;
        current = default;
        return false;
    }

    /// <summary>Spills this inline buffer into a fresh heap array sized to continue the same growth
    /// ladder (at least <see cref="usedCapacity"/>, and at least <paramref name="minCapacity"/>), for
    /// the read- and write-triggered promotions to <see cref="LuaArrayTableStorage"/> (and the array
    /// halves of <see cref="LuaStringAndArrayTableStorage"/>/<see cref="LuaValueTableStorage"/>).
    /// Only the live <see cref="usedCapacity"/> prefix is ever non-default, so copying the rest along
    /// is harmless.</summary>
    internal LuaTableArrayPart SpillToHeap(int minCapacity)
    {
        var target = Math.Max(usedCapacity, minCapacity);
        var part = new LuaTableArrayPart(target <= 8 ? 8 : MathEx.NextPowerOfTwo(target));
        ((ReadOnlySpan<LuaValue>)buffer)[..usedCapacity].CopyTo(part.AsSpan());
        return part;
    }
}
