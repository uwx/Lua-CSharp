using System.Diagnostics;
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
    public bool HasFastStringPart => false;
    public int RawArrayLength => usedCapacity;

    public bool TryGetString(string key, out LuaValue value)
    {
        value = default;
        return false;
    }

    public bool TryGetArray(int index, out LuaValue value)
    {
        if (index > 0 && index <= usedCapacity)
        {
            value = buffer[index - 1];
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
        if (index > 0 && index <= usedCapacity)
        {
            return ref buffer[index - 1];
        }

        return ref Unsafe.NullRef<LuaValue>();
    }

    public ref LuaValue FindGeneric(in LuaValue key) => ref Unsafe.NullRef<LuaValue>();

    public void SetString(string key, in LuaValue value) => throw new UnreachableException();

    public void SetArrayGrowing(int index, in LuaValue value)
    {
        Debug.Assert(index > 0 && index <= usedCapacity);
        buffer[index - 1] = value;
    }

    public void SetGeneric(in LuaValue key, in LuaValue value) => throw new UnreachableException();

    public Span<LuaValue> ArraySpan() => ((Span<LuaValue>)buffer)[..usedCapacity];

    public Memory<LuaValue> ArrayMemory() => throw new UnreachableException(
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

        if (newCapacity > Capacity)
        {
            throw new UnreachableException(
                "LuaTable must promote a LuaSmallArrayTableStorage before growing past 16 slots."
            );
        }

        usedCapacity = newCapacity <= 8 ? 8 : MathEx.NextPowerOfTwo(newCapacity);
    }

    public LuaValue RemoveAtArray(int index)
    {
        var arrayIndex = index - 1;
        var span = ArraySpan();
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

        var span = ArraySpan();
        var arrayIndex = index - 1;
        if (arrayIndex != span.Length - 1)
        {
            span[arrayIndex..^1].CopyTo(span[(arrayIndex + 1)..]);
        }

        span[arrayIndex] = value;
    }

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

    public void Clear() => ((Span<LuaValue>)buffer).Clear();

    public ILuaTableStorage Clone()
    {
        var clone = new LuaSmallArrayTableStorage { usedCapacity = usedCapacity };
        ((ReadOnlySpan<LuaValue>)buffer).CopyTo(clone.buffer);
        return clone;
    }

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
