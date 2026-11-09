using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Continuation history table tracking move response correlations across 1-ply and 2-ply intervals.
/// Dimensions: [13 piece types, 64 to-squares, 13 piece types, 64 to-squares].
/// Updates use gravity-based damping to bound scores within [-16384, 16384].
/// </summary>
public sealed class ContinuationHistory
{
    public const int MaxHistory = 16384;

    public const int TableSize = 16 * 64 * 16 * 64;

    private readonly short[] _table1Ply = new short[TableSize];
    private readonly short[] _table2Ply = new short[TableSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBaseOffset(Piece prevPiece, Square prevTo)
    {
        if (prevPiece == Piece.None || prevTo == Square.None) return -1;
        return ((int)prevPiece << 16) | ((int)prevTo << 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get1PlyAt(int baseOffset, Piece currPiece, Square currTo)
    {
        if (baseOffset < 0 || currPiece == Piece.None || currTo == Square.None) return 0;
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table1Ply), baseOffset | ((int)currPiece << 6) | (int)currTo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get2PlyAt(int baseOffset, Piece currPiece, Square currTo)
    {
        if (baseOffset < 0 || currPiece == Piece.None || currTo == Square.None) return 0;
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table2Ply), baseOffset | ((int)currPiece << 6) | (int)currTo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetIndex(Piece p1, Square to1, Piece p2, Square to2)
    {
        return ((int)p1 << 16) | ((int)to1 << 10) | ((int)p2 << 6) | (int)to2;
    }

    /// <summary>
    /// Maps a <see cref="Piece"/> enum (0, 1..6, 9..14) to dense index [0..12].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PieceToIndex(Piece piece)
    {
        int p = (int)piece;
        return ((uint)((p & 7) - 1) < 6u && (uint)p <= 14u) ? p - ((p >> 3) << 1) : 0;
    }

    /// <summary>
    /// Gets the 1-ply continuation history score (response to opponent's previous move).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get1PlyScore(Piece prevPiece, Square prevTo, Piece currPiece, Square currTo)
    {
        if (prevPiece == Piece.None || prevTo == Square.None || currPiece == Piece.None || currTo == Square.None)
            return 0;

        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table1Ply), GetIndex(prevPiece, prevTo, currPiece, currTo));
    }

    /// <summary>
    /// Gets the 2-ply continuation history score (response to our own previous move).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get2PlyScore(Piece prevPiece, Square prevTo, Piece currPiece, Square currTo)
    {
        if (prevPiece == Piece.None || prevTo == Square.None || currPiece == Piece.None || currTo == Square.None)
            return 0;

        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table2Ply), GetIndex(prevPiece, prevTo, currPiece, currTo));
    }

    /// <summary>
    /// Gets the combined 1-ply and 2-ply continuation history score for candidate move ordering.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(Piece prev1Piece, Square prev1To, Piece prev2Piece, Square prev2To, Piece currPiece, Square currTo)
    {
        return Get1PlyScore(prev1Piece, prev1To, currPiece, currTo) +
               Get2PlyScore(prev2Piece, prev2To, currPiece, currTo);
    }

    /// <summary>
    /// Updates the 1-ply continuation history entry with gravity damping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update1Ply(Piece prevPiece, Square prevTo, Piece currPiece, Square currTo, int bonus)
    {
        if (prevPiece == Piece.None || prevTo == Square.None || currPiece == Piece.None || currTo == Square.None)
            return;

        int idx = GetIndex(prevPiece, prevTo, currPiece, currTo);
        ref short val = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table1Ply), idx);
        val = (short)(val + bonus - ((val * Math.Abs(bonus)) / MaxHistory));
    }

    /// <summary>
    /// Updates the 2-ply continuation history entry with gravity damping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update2Ply(Piece prevPiece, Square prevTo, Piece currPiece, Square currTo, int bonus)
    {
        if (prevPiece == Piece.None || prevTo == Square.None || currPiece == Piece.None || currTo == Square.None)
            return;

        int idx = GetIndex(prevPiece, prevTo, currPiece, currTo);
        ref short val = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_table2Ply), idx);
        val = (short)(val + bonus - ((val * Math.Abs(bonus)) / MaxHistory));
    }

    /// <summary>
    /// Clears both 1-ply and 2-ply continuation tables.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table1Ply);
        Array.Clear(_table2Ply);
    }
}
