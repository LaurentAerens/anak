using System.Runtime.CompilerServices;
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

    private readonly int[,,,] _table1Ply = new int[13, 64, 13, 64];
    private readonly int[,,,] _table2Ply = new int[13, 64, 13, 64];

    /// <summary>
    /// Maps a <see cref="Piece"/> enum (0, 1..6, 9..14) to dense index [0..12].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PieceToIndex(Piece piece)
    {
        byte p = (byte)piece;
        if (p == 0) return 0;
        return p < 8 ? p : (p >= 9 && p <= 14 ? p - 2 : 0);
    }

    /// <summary>
    /// Gets the 1-ply continuation history score (response to opponent's previous move).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get1PlyScore(Piece prevPiece, Square prevTo, Piece currPiece, Square currTo)
    {
        if (prevPiece == Piece.None || prevTo == Square.None || currPiece == Piece.None || currTo == Square.None)
            return 0;

        int p1 = PieceToIndex(prevPiece);
        int p2 = PieceToIndex(currPiece);
        return _table1Ply[p1, (byte)prevTo, p2, (byte)currTo];
    }

    /// <summary>
    /// Gets the 2-ply continuation history score (response to our own previous move).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get2PlyScore(Piece prevPiece, Square prevTo, Piece currPiece, Square currTo)
    {
        if (prevPiece == Piece.None || prevTo == Square.None || currPiece == Piece.None || currTo == Square.None)
            return 0;

        int p1 = PieceToIndex(prevPiece);
        int p2 = PieceToIndex(currPiece);
        return _table2Ply[p1, (byte)prevTo, p2, (byte)currTo];
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

        int p1 = PieceToIndex(prevPiece);
        int p2 = PieceToIndex(currPiece);
        ref int val = ref _table1Ply[p1, (byte)prevTo, p2, (byte)currTo];
        val += bonus - (int)((long)val * Math.Abs(bonus) / MaxHistory);
    }

    /// <summary>
    /// Updates the 2-ply continuation history entry with gravity damping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update2Ply(Piece prevPiece, Square prevTo, Piece currPiece, Square currTo, int bonus)
    {
        if (prevPiece == Piece.None || prevTo == Square.None || currPiece == Piece.None || currTo == Square.None)
            return;

        int p1 = PieceToIndex(prevPiece);
        int p2 = PieceToIndex(currPiece);
        ref int val = ref _table2Ply[p1, (byte)prevTo, p2, (byte)currTo];
        val += bonus - (int)((long)val * Math.Abs(bonus) / MaxHistory);
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
