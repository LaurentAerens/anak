using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Capture history table tracking fail-high rates of captures.
/// Flattened 1D array of [16 piece types x 64 squares x 16 victim types] = 16384 ints.
/// Indexed via ((piece << 10) | (to << 4) | victim) for zero-multiplication,
/// single-instruction addressing and eliminated multidimensional bounds checks.
/// Updates use gravity-based damping to bound scores within [-16384, 16384].
/// </summary>
public sealed class CaptureHistory
{
    public const int MaxHistory = 16384;
    private const int TableSize = 16 * 64 * 16;
    private readonly int[] _table = new int[TableSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetIndex(int pieceIndex, Square to, int victimIndex) =>
        (pieceIndex << 10) | ((int)to << 4) | victimIndex;

    /// <summary>
    /// Maps a <see cref="Piece"/> enum to dense index [0..12].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PieceToIndex(Piece piece)
    {
        byte p = (byte)piece;
        if (p == 0) return 0;
        return p < 8 ? p : (p >= 9 && p <= 14 ? p - 2 : 0);
    }

    /// <summary>
    /// Maps a <see cref="PieceType"/> enum to dense index [0..6].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TypeToIndex(PieceType type)
    {
        int t = (int)type;
        return (t >= 0 && t <= 6) ? t : 0;
    }

    /// <summary>
    /// Gets the capture history score for a given attacker piece, destination, and victim piece.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Piece piece, Square to, Piece victim)
    {
        int p = PieceToIndex(piece);
        int v = PieceToIndex(victim);
        return _table[GetIndex(p, to, v)];
    }

    /// <summary>
    /// Gets the capture history score for a given attacker piece, destination, and victim type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Piece piece, Square to, PieceType victimType)
    {
        int p = PieceToIndex(piece);
        int v = TypeToIndex(victimType);
        return _table[GetIndex(p, to, v)];
    }

    /// <summary>
    /// Updates the capture history entry with gravity damping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Piece piece, Square to, Piece victim, int bonus)
    {
        int p = PieceToIndex(piece);
        int v = PieceToIndex(victim);
        ref int val = ref _table[GetIndex(p, to, v)];
        val += bonus - (int)((long)val * Math.Abs(bonus) / MaxHistory);
    }

    /// <summary>
    /// Updates the capture history entry by victim type with gravity damping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Piece piece, Square to, PieceType victimType, int bonus)
    {
        int p = PieceToIndex(piece);
        int v = TypeToIndex(victimType);
        ref int val = ref _table[GetIndex(p, to, v)];
        val += bonus - (int)((long)val * Math.Abs(bonus) / MaxHistory);
    }

    /// <summary>
    /// Clears the capture history table.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table);
    }
}
