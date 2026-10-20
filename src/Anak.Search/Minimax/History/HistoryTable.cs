using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Butterfly history table tracking move success frequencies across all depths.
/// Dimensions: [2 colors, 64 from-squares, 64 to-squares].
/// Updates use gravity-based damping to bound scores within [-16384, 16384].
/// </summary>
public sealed class HistoryTable
{
    public const int MaxHistory = 16384;
    private readonly int[,,] _table = new int[2, 64, 64];

    /// <summary>
    /// Gets the butterfly history score for a given side, from-square, and to-square.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Color side, Square from, Square to)
    {
        return _table[(byte)side, (byte)from, (byte)to];
    }

    /// <summary>
    /// Gets the butterfly history score for a move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Color side, Move move)
    {
        return _table[(byte)side, (byte)move.From, (byte)move.To];
    }

    /// <summary>
    /// Updates the history score with gravity damping:
    /// val += bonus - (val * |bonus| / 16384)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Color side, Square from, Square to, int bonus)
    {
        ref int val = ref _table[(byte)side, (byte)from, (byte)to];
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
