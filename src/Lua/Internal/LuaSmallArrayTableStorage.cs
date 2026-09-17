using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>
/// Up to 8 array elements held inline via <see cref="InlineArray8{T}"/> -- no heap array
/// allocation for small pure-array tables (Sx's <c>children</c> lists are almost always well under
/// 8 entries). Never grows past 8 slots: a write that would need more, or any string/generic key,
/// promotes to <see cref="LuaArrayTableStorage"/>, <see cref="LuaStringAndArrayTableStorage"/> or
/// <see cref="LuaValueTableStorage"/> instead (see the promotion graph in the storage-split plan).
/// A read via <see cref="LuaTable.GetArrayMemory"/> also promotes (to <see cref="LuaArrayTableStorage"/>)
/// since a real <see cref="Memory{T}"/> cannot point into this inline buffer.
/// <para>
/// The <em>reported</em> capacity (<see cref="RawArrayLength"/>) follows the same "8, then next
/// power of two" growth ladder <see cref="LuaTableArrayPart"/> uses, capped at 8, rather than always
/// reporting 8 -- even though the inline buffer physically has all 8 slots from the start. This
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
    public const int Capacity = 8;

    InlineArray8<LuaValue> buffer;

    public LuaTableStorageKind Kind => LuaTableStorageKind.SmallArray;
    public int RawArrayLength => 8;

    public bool TryGetValue(in LuaValue key, out LuaValue value)
    {
        if (LuaTableArrayPart.TryGetIndex(key, Capacity, out var index))
        {
            value = MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1);
            return true;
        }

        value = default;
        return false;
    }

    public ref LuaValue FindValue(in LuaValue key)
    {
        if (LuaTableArrayPart.TryGetIndex(key, Capacity, out var index))
        {
            return ref MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1);
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    /// <summary>Only ever reached with a key the caller has already decided fits (the indexer routes
    /// array-range keys to <see cref="SetArrayValue"/> and promotes everything else out of this kind), so
    /// the physical 8-slot bound is the whole check.</summary>
    public void SetValue(in LuaValue key, in LuaValue value)
    {
        if (LuaTableArrayPart.TryGetIndex(key, Capacity, out var index))
        {
            MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1) = value;
        }
    }

    public void SetArrayValue(int index, in LuaValue value)
    {
        EnsureArrayCapacityInPlace(index);
        MemoryMarshalEx.UnsafeElementAt<LuaValue>(buffer, index - 1) = value;
    }

    public Span<LuaValue> ArraySpan => buffer;

    public Memory<LuaValue> ArrayMemory =>
        throw new UnreachableException(
            "LuaTable must promote a LuaSmallArrayTableStorage before requesting a Memory<LuaValue>."
        );

    public void EnsureArrayCapacityInPlace(int newCapacity)
    {
#if DEBUG
        if (newCapacity > Capacity)
        {
            Unreachable();
        }

        [DoesNotReturn]
        static void Unreachable()
        {
            throw new UnreachableException(
                "LuaTable must promote a LuaSmallArrayTableStorage before growing past 8 slots."
            );
        }
#endif
        return;
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
        EnsureArrayCapacityInPlace(index + 1);

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
        if (!LuaTableArrayPart.TryGetNextStart(key, Capacity, out var start))
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
        var clone = new LuaSmallArrayTableStorage();
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

    internal LuaTableArrayPart SpillToHeap(int minCapacity)
    {
        var target = Math.Max(Capacity, minCapacity);
        var part = new LuaTableArrayPart(target <= 8 ? 8 : MathEx.NextPowerOfTwo(target));
        ((ReadOnlySpan<LuaValue>)buffer).CopyTo(part.AsSpan());
        return part;
    }
}
