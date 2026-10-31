using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Butterfly history table tracking move success frequencies across all depths.
/// Flattened 1D array indexed by ((side << 12) | (from << 6) | to) for zero-multiplication,
/// single-instruction addressing and eliminated multidimensional bounds checks.
/// Updates use gravity-based damping to bound scores within [-16384, 16384].
/// </summary>
public sealed class HistoryTable
{
    public const int MaxHistory = 16384;
    private const int TableSize = 2 * 64 * 64;
    private readonly int[] _table = new int[TableSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetIndex(Color side, Square from, Square to) =>
        ((int)side << 12) | ((int)from << 6) | (int)to;

    /// <summary>
    /// Gets the butterfly history score for a given side, from-square, and to-square.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Color side, Square from, Square to)
    {
        return _table[GetIndex(side, from, to)];
    }

    /// <summary>
    /// Gets the butterfly history score for a move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Color side, Move move)
    {
        return _table[GetIndex(side, move.From, move.To)];
    }

    /// <summary>
    /// Updates the history score with gravity damping:
    /// val += bonus - (val * |bonus| / 16384)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Color side, Square from, Square to, int bonus)
    {
        ref int val = ref _table[GetIndex(side, from, to)];
        val += bonus - (int)((long)val * Math.Abs(bonus) / MaxHistory);
    }

    /// <summary>
    /// Updates the history score for a move with gravity damping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Color side, Move move, int bonus)
    {
        Update(side, move.From, move.To, bonus);
    }

    /// <summary>
    /// Computes history bonus based on search depth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateBonus(int depth)
    {
        return Math.Min(depth * depth, 2000);
    }

    /// <summary>
    /// Clears the history table.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table);
    }
}
