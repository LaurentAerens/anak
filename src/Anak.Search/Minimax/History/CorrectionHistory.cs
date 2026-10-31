using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Pawn structure correction history table.
/// Learns positional evaluation offsets on the fly keyed by [SideToMove, PawnKey].
/// Dimensions: short[2 * 16384], indexed by ((int)side << 14) | ((pawnKey ^ (pawnKey >> 16)) & 16383).
/// </summary>
public sealed class CorrectionHistory
{
    public const int TableSizePerColor = 16384;
    public const int TableMask = 16383;
    public const int TotalTableSize = TableSizePerColor * 2;
    public const int MaxCorrection = 128;
    public const int ScaleFactor = 128;

    private readonly short[] _table = new short[TotalTableSize];

    /// <summary>
    /// Computes the dense table index from the side to move and 64-bit pawn hash key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIndex(Color side, ulong pawnKey)
    {
        return ((int)side << 14) | (int)((pawnKey ^ (pawnKey >> 16)) & (ulong)TableMask);
    }

    /// <summary>
    /// Overload for backwards compatibility / default White indexing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIndex(ulong pawnKey)
    {
        return GetIndex(Color.White, pawnKey);
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
    /// Gets the evaluation correction for the given side and pawn key (in centipawns, clamped to [-MaxCorrection, MaxCorrection]).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCorrection(Color side, ulong pawnKey)
    {
        return _table[GetIndex(side, pawnKey)] / ScaleFactor;
    }

    /// <summary>
    /// Gets the evaluation correction for the pawn structure of the board based on side to move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCorrection(in Board board)
    {
        return GetCorrection(board.SideToMove, ComputePawnKey(in board));
    }

    /// <summary>
    /// Overload for backwards compatibility / tests (defaults to White).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCorrection(ulong pawnKey)
    {
        return GetCorrection(Color.White, pawnKey);
    }

    /// <summary>
    /// Gets the adjusted static evaluation offset by pawn correction history.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAdjustedEval(Color side, ulong pawnKey, int staticEval)
    {
        return staticEval + GetCorrection(side, pawnKey);
    }

    /// <summary>
    /// Gets the adjusted static evaluation offset by pawn correction history for the board.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAdjustedEval(in Board board, int staticEval)
    {
        return staticEval + GetCorrection(board.SideToMove, ComputePawnKey(in board));
    }

    /// <summary>
    /// Overload for backwards compatibility / tests (defaults to White).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAdjustedEval(ulong pawnKey, int staticEval)
    {
        return staticEval + GetCorrection(Color.White, pawnKey);
    }

    /// <summary>
    /// Updates the pawn correction history offset using exponential moving average weighted by search depth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Color side, ulong pawnKey, int depth, int searchScore, int staticEval)
    {
        int diff = Math.Clamp(searchScore - staticEval, -MaxCorrection, MaxCorrection);
        int target = diff * ScaleFactor;
        int weight = Math.Clamp(depth * 32, 16, 512);

        int index = GetIndex(side, pawnKey);
        int current = _table[index];
        _table[index] = (short)(current + (target - current) * weight / 2048);
    }

    /// <summary>
    /// Updates the pawn correction history offset using board pawn structure and side to move, weighted by depth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(in Board board, int depth, int searchScore, int staticEval)
    {
        Update(board.SideToMove, ComputePawnKey(in board), depth, searchScore, staticEval);
    }

    /// <summary>
    /// Overload for backward compatibility / tests (defaults to White).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(ulong pawnKey, int depth, int searchScore, int staticEval)
    {
        Update(Color.White, pawnKey, depth, searchScore, staticEval);
    }

    /// <summary>
    /// Overload for backward compatibility / tests (defaults depth to 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Color side, ulong pawnKey, int searchScore, int staticEval)
    {
        Update(side, pawnKey, 1, searchScore, staticEval);
    }

    /// <summary>
    /// Overload for backward compatibility / tests (defaults to White, depth 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(ulong pawnKey, int searchScore, int staticEval)
    {
        Update(Color.White, pawnKey, 1, searchScore, staticEval);
    }

    /// <summary>
    /// Overload for backward compatibility / tests (defaults depth to 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(in Board board, int searchScore, int staticEval)
    {
        Update(in board, 1, searchScore, staticEval);
    }

    /// <summary>
    /// Clears the correction history table.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table);
    }
}
