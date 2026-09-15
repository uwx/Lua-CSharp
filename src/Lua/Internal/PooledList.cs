using System.Buffers;
using System.Runtime.CompilerServices;

namespace Lua.Internal;

struct PooledList<T> : IDisposable
{
    InlineArray16<T> inlineBuffer;
    
    T[]? buffer;
    int tail;

    private readonly int _sizeHint = 32;

    public PooledList(int sizeHint)
    {
        _sizeHint = sizeHint;
    }

    public bool IsDisposed => tail == -1;

    public readonly int Count => tail;

    public int Length => tail;

    public void Add(in T item)
    {
        ThrowIfDisposed();

        if (buffer == null)
        {
            if (tail < 16)
            {
                inlineBuffer[tail] = item;
                tail++;
                return;
            }
            
            buffer = ArrayPool<T>.Shared.Rent(_sizeHint);
            ((Span<T>)inlineBuffer).CopyTo(buffer);
        }
        else if (buffer.Length == tail)
        {
            var newArray = ArrayPool<T>.Shared.Rent(tail * 2);
            buffer.AsSpan().CopyTo(newArray);
            ArrayPool<T>.Shared.Return(buffer);
            buffer = newArray;
        }

        buffer[tail] = item;
        tail++;
    }

    public void AddRange(scoped ReadOnlySpan<T> items)
    {
        ThrowIfDisposed();

        if (buffer == null)
        {
            if (tail + items.Length <= 16)
            {
                items.CopyTo(((Span<T>)inlineBuffer)[tail..]);
                tail += items.Length;
                return;
            }

            buffer = ArrayPool<T>.Shared.Rent(Math.Max(_sizeHint, tail + items.Length));
            ((Span<T>)inlineBuffer).CopyTo(buffer);
        }
        else if (buffer.Length < tail + items.Length)
        {
            var newSize = buffer.Length * 2;
            while (newSize < tail + items.Length)
            {
                newSize *= 2;
            }

            var newArray = ArrayPool<T>.Shared.Rent(newSize);
            buffer.AsSpan().CopyTo(newArray);
            ArrayPool<T>.Shared.Return(buffer);
            buffer = newArray;
        }

        items.CopyTo(buffer.AsSpan()[tail..]);
        tail += items.Length;
    }

    public void PopUntil(int count)
    {
        ThrowIfDisposed();

        if (count > tail)
        {
            ThrowArgumentOutOfRangeException();
        }

        tail = count;
    }

    public void Pop(int count)
    {
        ThrowIfDisposed();

        if (count > tail)
        {
            ThrowArgumentOutOfRangeException();
        }

        tail -= count;
    }

    private static void ThrowArgumentOutOfRangeException()
    {
        // ReSharper disable once NotResolvedInText
        throw new ArgumentOutOfRangeException("count");
    }

    public void Clear()
    {
        ThrowIfDisposed();

        if (buffer != null)
        {
            new Span<T>(buffer, 0, tail).Clear();
        }
        else
        {
            ((Span<T>)inlineBuffer)[..tail].Clear();
        }

        tail = 0;
    }

    public void Dispose()
    {
        ThrowIfDisposed();

        if (buffer != null)
        {
            ArrayPool<T>.Shared.Return(buffer);
            buffer = null;
        }

        tail = -1;
    }

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AsSpan(this)[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<T> AsSpan(in PooledList<T> list)
    {
        if (list.buffer == null)
        {
            return ((ReadOnlySpan<T>)list.inlineBuffer)[..list.tail];
        }

        return new(list.buffer, 0, list.tail);
    }

    void ThrowIfDisposed()
    {
        if (tail == -1)
        {
            ThrowDisposedException();
        }
    }

    void ThrowDisposedException()
    {
        throw new ObjectDisposedException(nameof(PooledList<T>));
    }
}
