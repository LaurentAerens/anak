using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Represents a node in the Monte Carlo Tree Search.
/// Compact struct layout designed for zero-GC allocation in the NodeArena.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MCTSNode
{
    /// <summary>
    /// The edge move that led to this node from its parent.
    /// </summary>
    public Move Move;

    /// <summary>
    /// Number of legal child moves available from this position.
    /// </summary>
    public ushort NumChildren;

    /// <summary>
    /// Total number of visits to this node (N).
    /// </summary>
    public uint Visits;

    /// <summary>
    /// Cumulative value / reward accumulated through this node (W).
    /// </summary>
    public float TotalValue;

    /// <summary>
    /// Prior policy probability assigned to this move (P).
    /// </summary>
    public float Prior;

    /// <summary>
    /// Index into the flat NodeArena where children are located (-1 if unexpanded leaf).
    /// </summary>
    public int FirstChildIndex;

    /// <summary>
    /// Offset [0 .. NumChildren - 1] to the child with highest visit count.
    /// </summary>
    public ushort BestChildOffset;

    /// <summary>
    /// Reserved padding for memory alignment.
    /// </summary>
    private ushort _padding;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MCTSNode(Move move, float prior)
    {
        Move = move;
        NumChildren = 0;
        Visits = 0;
        TotalValue = 0.0f;
        Prior = prior;
        FirstChildIndex = -1;
        BestChildOffset = 0;
        _padding = 0;
    }

    /// <summary>
    /// Mean action value Q = W / N (or 0.0f if unvisited).
    /// </summary>
    public readonly float Q
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Visits > 0 ? TotalValue / Visits : 0.0f;
    }

    /// <summary>
    /// Returns true if this node has been expanded into children in the arena.
    /// </summary>
    public readonly bool IsExpanded
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => FirstChildIndex >= 0;
    }

    /// <summary>
    /// Updates visit count and total accumulated value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddVisit(float value)
    {
        Visits++;
        TotalValue += value;
    }

    /// <summary>
    /// Resets the node state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Move = Move.Null;
        NumChildren = 0;
        Visits = 0;
        TotalValue = 0.0f;
        Prior = 0.0f;
        FirstChildIndex = -1;
        BestChildOffset = 0;
        _padding = 0;
    }
}
