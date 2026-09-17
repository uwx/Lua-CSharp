using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lua.Internal;

static class MemoryMarshalEx
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T UnsafeElementAt<T>(T[] array, int index)
    {
        // Debug: force an index out of bounds error instead of returning invalid memory
#if DEBUG
        return ref array[index];
#elif NET6_0_OR_GREATER
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index);
#else
        ref var reference = ref MemoryMarshal.GetReference(array.AsSpan());
        return ref Unsafe.Add(ref reference, index);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T UnsafeElementAt<T>(Span<T> array, int index)
    {
        // Debug: force an index out of bounds error instead of returning invalid memory
#if DEBUG
        return ref array[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetReference(array), index);
#endif
    }

    /// <summary>
    /// Reference to the first element of <paramref name="array"/>, held as the base of an indexed walk
    /// (the VM's constant table is the caller) rather than as an access to element 0. Deliberately not
    /// <see cref="UnsafeElementAt{T}(System.ReadOnlySpan{T},int)"/> with index 0: a prototype with no
    /// constants has an empty constant span, and the reference to element 0 of an empty span is
    /// one-past-the-end -- fine to hold and to offset from, since no instruction of a prototype can name
    /// a constant that prototype does not have, but not fine to dereference. The debug bounds check in
    /// <see cref="UnsafeElementAt{T}(System.ReadOnlySpan{T},int)"/> is exactly that dereference, so it
    /// would reject the frame entry rather than an access.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T UnsafeHeadRef<T>(ReadOnlySpan<T> array) => ref MemoryMarshal.GetReference(array);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T UnsafeElementAt<T>(ReadOnlySpan<T> array, int index)
    {
        // Debug: force an index out of bounds error instead of returning invalid memory.
        // AsRef because a ReadOnlySpan's indexer hands back a readonly reference -- the bounds
        // check still happens (and still throws), it is only the reference that is opened up.
#if DEBUG
        return ref Unsafe.AsRef(in array[index]);
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetReference(array), index);
#endif
    }
}