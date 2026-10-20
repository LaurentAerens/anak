using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Pawn structure correction history table.
/// Learns positional evaluation offsets on the fly keyed by the position's pawn structure hash.
/// Dimensions: short[16384], indexed by (pawnKey ^ (pawnKey >> 16)) & 16383.
/// </summary>
public sealed class CorrectionHistory
{
    public const int TableSize = 16384;
    public const int TableMask = 16383;
    public const int MaxCorrection = 1024;

    private readonly short[] _table = new short[TableSize];

    /// <summary>
    /// Computes the dense table index from the 64-bit pawn hash key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIndex(ulong pawnKey)
    {
        return (int)((pawnKey ^ (pawnKey >> 16)) & (ulong)TableMask);
    }

    /// <summary>
    /// Computes the 64-bit Zobrist key of the pawn structure on the board.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ComputePawnKey(in Board board)
    {
        ulong key = 0UL;
        ulong wp = board.Pawns & board.White;
        while (wp != 0)
        {
            Square sq = Bitboard.PopLsb(ref wp);
            key ^= Zobrist.PieceSquare[(int)Piece.WhitePawn, (int)sq];
        }

        ulong bp = board.Pawns & board.Black;
        while (bp != 0)
        {
            Square sq = Bitboard.PopLsb(ref bp);
            key ^= Zobrist.PieceSquare[(int)Piece.BlackPawn, (int)sq];
        }

        return key;
    }

    /// <summary>
    /// Gets the evaluation correction for the given pawn key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCorrection(ulong pawnKey)
    {
        return _table[GetIndex(pawnKey)];
    }

    /// <summary>
    /// Gets the evaluation correction for the pawn structure of the board.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCorrection(in Board board)
    {
        return GetCorrection(ComputePawnKey(in board));
    }

    /// <summary>
    /// Gets the adjusted static evaluation offset by pawn correction history.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAdjustedEval(ulong pawnKey, int staticEval)
    {
        return staticEval + GetCorrection(pawnKey);
    }

    /// <summary>
    /// Gets the adjusted static evaluation offset by pawn correction history for the board.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAdjustedEval(in Board board, int staticEval)
    {
        return staticEval + GetCorrection(in board);
    }

    /// <summary>
    /// Updates the pawn correction history offset at the end of search.
    /// Formula: CorrHist[PawnKey] += (SearchScore - StaticEval) / 16.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(ulong pawnKey, int searchScore, int staticEval)
    {
        int diff = searchScore - staticEval;
        int delta = diff / 16;
        if (delta == 0) return;

        int index = GetIndex(pawnKey);
        int current = _table[index];
        _table[index] = (short)Math.Clamp(current + delta, -MaxCorrection, MaxCorrection);
    }

    /// <summary>
    /// Updates the pawn correction history offset using board pawn structure.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(in Board board, int searchScore, int staticEval)
    {
        Update(ComputePawnKey(in board), searchScore, staticEval);
    }

    /// <summary>
    /// Clears the correction history table.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table);
    }
}
