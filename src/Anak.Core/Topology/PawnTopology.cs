using System.Numerics;
using System.Runtime.CompilerServices;

namespace Anak.Core;

/// <summary>
/// Specialized NNUE expert models routed by board topology and phase.
/// </summary>
public enum NnueExpertType : byte
{
    Open = 0,
    Closed = 1,
    Endgame = 2,
    Queenless = 3,
    Imbalance = 4,
    Attack = 5
}

/// <summary>
/// High-performance, zero-allocation bitboard analysis of pawn structure and board topology.
/// Evaluates open files, semi-open files, locked pawn chains (rams), pawn tension, phase,
/// material imbalances, and king flank geometry to route positions to specialized NNUE experts.
/// </summary>
public static class PawnTopology
{
    /// <summary>
    /// Computes a continuous openness score between 0.0 (completely locked) and 1.0 (wide open).
    /// </summary>
    /// <param name="board">The chess board state.</param>
    /// <returns>A float between 0.0f and 1.0f.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CalculateOpenness(in Board board)
    {
        ulong whitePawns = board.White & board.Pawns;
        ulong blackPawns = board.Black & board.Pawns;
        ulong allPawns = whitePawns | blackPawns;
        int totalPawns = BitOperations.PopCount(allPawns);

        // 1. Locked pawns (rams): White pawn directly blocked head-to-head by Black pawn
        ulong lockedRams = whitePawns & (blackPawns >> 8);
        int lockedCount = BitOperations.PopCount(lockedRams);

        // 2. Open and Semi-open files
        int openFiles = 0;
        int semiOpenFiles = 0;
        for (int f = 0; f < 8; f++)
        {
            ulong fileMask = Bitboard.FileMasks[f];
            bool hasWhite = (whitePawns & fileMask) != 0;
            bool hasBlack = (blackPawns & fileMask) != 0;

            if (!hasWhite && !hasBlack)
            {
                openFiles++;
            }
            else if (!hasWhite || !hasBlack)
            {
                semiOpenFiles++;
            }
        }

        // 3. Pawn tension (pawns in mutual attack contact)
        ulong whiteAttacks = ((whitePawns << 7) & ~Bitboard.FileH) | ((whitePawns << 9) & ~Bitboard.FileA);
        int tensionCount = BitOperations.PopCount(whiteAttacks & blackPawns);

        // 4. Center pawn liquidation (c, d, e, f files)
        const ulong centerFiles = Bitboard.FileC | Bitboard.FileD | Bitboard.FileE | Bitboard.FileF;
        int centerPawns = BitOperations.PopCount(allPawns & centerFiles);

        // Openness metric calculation:
        // - Starting position: 16 pawns, 0 open files, 0 semi-open files, 8 center pawns, 0 locked rams -> ~0.35 (Closed)
        // - More open files & semi-open files increase openness
        // - Locked rams strongly decrease openness
        // - Center pawn disappearance increases openness
        // - Tension increases volatility and openness
        float openness = 0.35f
            + (openFiles * 0.12f)
            + (semiOpenFiles * 0.05f)
            - (lockedCount * 0.10f)
            + (tensionCount * 0.04f)
            + ((8 - centerPawns) * 0.03f)
            + ((16 - totalPawns) * 0.02f);

        return Math.Clamp(openness, 0.0f, 1.0f);
    }

    /// <summary>
    /// Classifies the position as Open or Closed based on whether openness exceeds the threshold.
    /// </summary>
    /// <param name="board">The chess board state.</param>
    /// <param name="threshold">Classification boundary (default: 0.50f).</param>
    /// <returns>True if the position is open; false if closed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOpen(in Board board, float threshold = 0.50f)
    {
        return CalculateOpenness(in board) >= threshold;
    }

    /// <summary>
    /// Returns true if the position is in the endgame phase (14 or fewer total pieces on board).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEndgame(in Board board)
    {
        return BitOperations.PopCount(board.Occupied) <= 14;
    }

    /// <summary>
    /// Returns true if neither side has a Queen on the board.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsQueenless(in Board board)
    {
        return board.Queens == 0;
    }

    /// <summary>
    /// Returns true if the position has a material imbalance (unequal Queens, Rook vs Minor exchange,
    /// or total material difference >= 2 points).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasMaterialImbalance(in Board board)
    {
        ulong white = board.White;
        ulong black = board.Black;
        ulong minors = board.Knights | board.Bishops;

        int wQ = BitOperations.PopCount(white & board.Queens);
        int bQ = BitOperations.PopCount(black & board.Queens);
        if (wQ != bQ) return true;

        int wR = BitOperations.PopCount(white & board.Rooks);
        int bR = BitOperations.PopCount(black & board.Rooks);
        int wM = BitOperations.PopCount(white & minors);
        int bM = BitOperations.PopCount(black & minors);
        if (wR != bR && wM != bM) return true;

        int wP = BitOperations.PopCount(white & board.Pawns);
        int bP = BitOperations.PopCount(black & board.Pawns);

        int wMat = (wQ * 9) + (wR * 5) + (wM * 3) + wP;
        int bMat = (bQ * 9) + (bR * 5) + (bM * 3) + bP;

        return Math.Abs(wMat - bMat) >= 2;
    }

    /// <summary>
    /// Returns true if Queens are on the board and the Kings are situated on opposite flanks
    /// (Queenside files A-C vs Kingside files F-H).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOppositeSideCastling(in Board board)
    {
        if (board.Queens == 0) return false;

        const ulong queenSide = Bitboard.FileA | Bitboard.FileB | Bitboard.FileC;
        const ulong kingSide = Bitboard.FileF | Bitboard.FileG | Bitboard.FileH;

        ulong wKing = board.White & board.Kings;
        ulong bKing = board.Black & board.Kings;

        bool wQueenSide = (wKing & queenSide) != 0;
        bool wKingSide = (wKing & kingSide) != 0;
        bool bQueenSide = (bKing & queenSide) != 0;
        bool bKingSide = (bKing & kingSide) != 0;

        return (wQueenSide && bKingSide) || (wKingSide && bQueenSide);
    }

    /// <summary>
    /// Classifies the board position into one of the 6 specialized NNUE experts using priority routing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NnueExpertType ClassifyExpert(in Board board)
    {
        if (IsEndgame(in board)) return NnueExpertType.Endgame;
        if (IsQueenless(in board)) return NnueExpertType.Queenless;
        if (HasMaterialImbalance(in board)) return NnueExpertType.Imbalance;
        if (IsOppositeSideCastling(in board)) return NnueExpertType.Attack;
        return IsOpen(in board) ? NnueExpertType.Open : NnueExpertType.Closed;
    }
}
