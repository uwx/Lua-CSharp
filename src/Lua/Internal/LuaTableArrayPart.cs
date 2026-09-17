namespace Lua.Internal;

/// <summary>
/// The heap-array-backed array part shared by the three <see cref="ILuaTableStorage"/> kinds that
/// own a real <c>LuaValue[]</c> (<see cref="LuaArrayTableStorage"/>, <see cref="LuaStringAndArrayTableStorage"/>,
/// <see cref="LuaValueTableStorage"/>). Ported from the pre-split <c>LuaTable</c>'s own array field
/// plus its grow/insert/removeAt/span/memory logic, so the three storages don't each carry a copy.
/// <see cref="LuaSmallArrayTableStorage"/> does not embed this -- it holds its own fixed
/// <c>InlineArray16&lt;LuaValue&gt;</c> field instead, since it never grows past 16 slots (it promotes
/// out to <see cref="LuaArrayTableStorage"/> instead of growing).
/// </summary>
struct LuaTableArrayPart
{
    internal LuaValue[] array;

    public LuaTableArrayPart(int capacity)
    {
        // Mirrors the pre-split constructor's `arrayCapacity > 1 ? new LuaValue[arrayCapacity] : []`:
        // a capacity of 0 or 1 is not worth a dedicated allocation (the first real growth call sizes
        // to at least 8 anyway), so it starts truly empty instead of a length-1 array. This matters
        // for observable behavior, not just as a micro-optimization -- see
        // CorpusGoldenTests.CompilesToGoldenBytecode: a single-array-field table template relies on
        // this table starting at length 0 so BuildTemplate's EnsureArrayCapacity(1) call actually
        // grows it (laddering up to 8), instead of silently no-op'ing against an already-"sufficient"
        // length-1 array.
        array = capacity > 1 ? new LuaValue[capacity] : [];
    }

    LuaTableArrayPart(LuaValue[] array)
    {
        this.array = array;
    }

    public readonly int Length => array.Length;

    public readonly Span<LuaValue> AsSpan() => array.AsSpan();

    public readonly Memory<LuaValue> AsMemory() => array.AsMemory();

    /// <summary>Grows the backing array in place to at least <paramref name="newCapacity"/> slots,
    /// using the same "8 or next power of two" ladder the pre-split <c>GrowArray</c> used.</summary>
    public void EnsureCapacity(int newCapacity)
    {
        if (array.Length >= newCapacity)
        {
            return;
        }

        var newLength = newCapacity <= 8 ? 8 : MathEx.NextPowerOfTwo(newCapacity);
        Array.Resize(ref array, newLength);
    }

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    public LuaValue RemoveAt(int index)
    {
        var arrayIndex = index - 1;
        var value = array[arrayIndex];

        if (arrayIndex < array.Length - 1)
        {
            array.AsSpan(arrayIndex + 1).CopyTo(array.AsSpan(arrayIndex));
        }

        array[^1] = default;

        return value;
    }

    /// <summary><paramref name="index"/> is a 1-origin Lua array index.</summary>
    public void Insert(int index, in LuaValue value)
    {
        if (index <= 0 || index > array.Length + 1)
        {
            throw new IndexOutOfRangeException();
        }

        var arrayIndex = index - 1;

        if (index > array.Length || array[^1].Type != LuaValueType.Nil)
        {
            EnsureCapacity(array.Length + 1);
        }

        if (arrayIndex != array.Length - 1)
        {
            array
                .AsSpan(arrayIndex, array.Length - arrayIndex - 1)
                .CopyTo(array.AsSpan(arrayIndex + 1));
        }

        array[arrayIndex] = value;
    }

    public readonly LuaTableArrayPart Clone() => new((LuaValue[])array.Clone());
}
