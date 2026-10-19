using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Anak.Core;

[InlineArray(256)]
public struct MoveBuffer
{
    private Move _element0;
}

public ref struct MoveList
{
    public const int MaxMoves = 256;
    private MoveBuffer _buffer;
    private int _count;

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    public Move this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer[index];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _buffer[index] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(Move move)
    {
        _buffer[_count++] = move;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        _count = 0;
    }

    [UnscopedRef]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<Move> AsSpan() => ((Span<Move>)_buffer)[.._count];

    [UnscopedRef]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Move> AsReadOnlySpan() => ((ReadOnlySpan<Move>)_buffer)[.._count];

    [UnscopedRef]
    public Span<Move>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();
}
