using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// Packed step entry along a linear probe spine.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZapTrajectoryStep
{
    public BoardState State;
    public ulong ZobristKey;
    public Move Move;
    public Color SideToMove;
    public short StaticEval;
    public short EvalDelta;

    public ZapTrajectoryStep(BoardState state, ulong zobristKey, Move move, Color sideToMove, short staticEval, short evalDelta)
    {
        State = state;
        ZobristKey = zobristKey;
        Move = move;
        SideToMove = sideToMove;
        StaticEval = staticEval;
        EvalDelta = evalDelta;
    }
}

[InlineArray(128)]
public struct ZapTrajectoryBuffer
{
    private ZapTrajectoryStep _element0;
}

/// <summary>
/// Stack-allocated, zero-allocation trajectory recording the sequence of plies along a greedy probe spine.
/// </summary>
public ref struct ZapTrajectory
{
    public const int MaxCapacity = 128;
    private ZapTrajectoryBuffer _buffer;
    private int _count;

    public readonly int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    [UnscopedRef]
    public ref ZapTrajectoryStep this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _buffer[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(in ZapTrajectoryStep step)
    {
        if (_count < MaxCapacity)
        {
            _buffer[_count++] = step;
        }
    }

    [UnscopedRef]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ZapTrajectoryStep> AsSpan() => ((ReadOnlySpan<ZapTrajectoryStep>)_buffer)[.._count];
}
