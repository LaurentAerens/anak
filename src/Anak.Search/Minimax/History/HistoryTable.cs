using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table), GetIndex(side, from, to));
    }

    /// <summary>
    /// Gets the butterfly history score for a move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Color side, Move move)
    {
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table), GetIndex(side, move.From, move.To));
    }

    /// <summary>
    /// Updates the history score with gravity damping:
    /// val += bonus - (val * |bonus| / 16384)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Color side, Square from, Square to, int bonus)
    {
        ref int val = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table), GetIndex(side, from, to));
        val += bonus - ((val * Math.Abs(bonus)) / MaxHistory);
    }

    /// <summary>
    /// Updates the history score for a move with gravity damping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Color side, Move move, int bonus)
    {
        Update(side, move.From, move.To, bonus);
    }

    public static int BonusScale { get; set; } = 16;
    public static int MaxBonus { get; set; } = 2000;
    public static int MaxMalus { get; set; } = 2000;

    /// <summary>
    /// Computes history bonus based on search depth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateBonus(int depth)
    {
        return Math.Min((depth * depth * BonusScale) / 16, MaxBonus);
    }

    /// <summary>
    /// Computes capped history malus for failed quiet moves.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateMalus(int depth)
    {
        return Math.Min(CalculateBonus(depth), MaxMalus);
    }

    /// <summary>
    /// Clears the history table.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table);
    }
}
