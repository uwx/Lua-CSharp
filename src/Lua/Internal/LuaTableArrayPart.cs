using System.Diagnostics.CodeAnalysis;

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
    
    void GrowForInsertion(int indexToInsert, int insertionCount = 1)
    {
        var requiredCapacity = checked(array.Length + insertionCount);
        var newLength = requiredCapacity <= 8 ? 8 : MathEx.NextPowerOfTwo(requiredCapacity);

        var newItems = new LuaValue[newLength];
        if (indexToInsert != 0)
        {
            Array.Copy(array, newItems, length: indexToInsert);
        }

        if (array.Length != indexToInsert)
        {
            Array.Copy(array, indexToInsert, newItems, indexToInsert + insertionCount, array.Length - indexToInsert);
        }

        array = newItems;
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
            ThrowIndexOutOfRangeException();
        }

        var arrayIndex = index - 1;

        if (arrayIndex >= array.Length || array[^1].Type != LuaValueType.Nil)
        {
            GrowForInsertion(array.Length);
        }
        else if (arrayIndex != array.Length - 1)
        {
            array
                .AsSpan(arrayIndex, array.Length - arrayIndex - 1)
                .CopyTo(array.AsSpan(arrayIndex + 1));
        }

        array[arrayIndex] = value;
        return;
        
        [DoesNotReturn]
        static void ThrowIndexOutOfRangeException()
        {
            throw new IndexOutOfRangeException();
        }
    }

    public readonly LuaTableArrayPart Clone() => new((LuaValue[])array.Clone());

    /// <summary>
    /// Writes 1-origin slot <paramref name="index"/>, which the caller has already grown the array to
    /// hold (the indexer's array branch calls <see cref="EnsureCapacity"/> first). The key was unwrapped
    /// on the way in, so nothing here has to re-derive it from a <see cref="LuaValue"/>.
    /// </summary>
    internal void Set(int index, in LuaValue value) =>
        MemoryMarshalEx.UnsafeElementAt(array, index - 1) = value;

    /// <summary>
    /// The 1-origin array slot <paramref name="key"/> names, if it names one: an integral number in
    /// <c>[1, arrayLength]</c>, in either numeric representation (<see cref="LuaValue.TryReadNumber"/>
    /// covers <c>Integer</c> and <c>Number</c> alike). This is the very guard the indexer's write path
    /// applies before routing a key into the array part, and every array read and write has to apply
    /// exactly it. A storage that instead switched on the key's <c>Type</c> would disagree with the write
    /// that put the value there -- most visibly for a <c>Number</c>, whose union holds a double, so
    /// reading it as an integer reinterprets those bits instead of converting the number.
    /// </summary>
    internal static bool TryGetIndex(in LuaValue key, int arrayLength, out int index)
    {
        if (
            key.TryReadNumber(out var number)
            && MathEx.IsInteger(number)
            && number > 0
            && number <= arrayLength
        )
        {
            index = (int)number;
            return true;
        }

        index = 0;
        return false;
    }

    /// <summary>
    /// Where a <c>next</c> control key resumes an array-part scan, as a 0-based slot: nil starts at the
    /// first slot, an integer key inside the array resumes at the slot just past it, and any other key
    /// -- a string, a non-integral or out-of-range number, anything that is not a number at all -- has
    /// no position in the array part. Mirrors the pre-split <c>LuaTable.TryGetInteger</c> guard for the
    /// integer case.
    /// </summary>
    internal static bool TryGetNextStart(in LuaValue key, int arrayLength, out int startSlot)
    {
        if (key.Type is LuaValueType.Nil)
        {
            startSlot = 0;
            return true;
        }

        // The control key's own 1-origin index doubles as the 0-based slot just past it, which is where a
        // walk resumed from that key has to start.
        if (TryGetIndex(key, arrayLength, out var index))
        {
            startSlot = index;
            return true;
        }

        startSlot = 0;
        return false;
    }

    /// <summary>
    /// First non-nil entry at or after 0-based slot <paramref name="index"/>, as a 1-origin Lua
    /// key/value pair. The array part's counterpart of the two dictionaries'
    /// <c>TryGetFirstFrom</c>; <see cref="LuaSmallArrayTableStorage"/> has its own copy over its inline
    /// buffer rather than sharing this struct.
    /// </summary>
    internal readonly bool TryGetFirstFrom(int index, out KeyValuePair<LuaValue, LuaValue> pair)
    {
        for (var i = index; i < array.Length; i++)
        {
            var value = array[i];
            if (value.Type is not LuaValueType.Nil)
            {
                pair = new(i + 1, value);
                return true;
            }
        }

        pair = default;
        return false;
    }

    /// <summary>
    /// Enumeration primitive for the array part: the next non-nil entry at or after 0-based slot
    /// <paramref name="index"/>, advancing <paramref name="index"/> to just past the slot it returned --
    /// so the same cursor carries on into the hash part once it reaches <see cref="Length"/>. The array
    /// part has no version to check (its slots cannot be swapped around by a removal the way a hash
    /// entry's can).
    /// </summary>
    internal readonly bool MoveNext(ref int index, out KeyValuePair<LuaValue, LuaValue> current)
    {
        for (var i = index; i < array.Length; i++)
        {
            var value = array[i];
            if (value.Type is not LuaValueType.Nil)
            {
                index = i + 1;
                current = new(i + 1, value);
                return true;
            }
        }

        index = array.Length;
        current = default;
        return false;
    }
}
