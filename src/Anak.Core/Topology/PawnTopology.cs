using System.Numerics;
using System.Runtime.CompilerServices;

namespace Anak.Core;

/// <summary>
/// High-performance, zero-allocation bitboard analysis of pawn structure topology.
/// Evaluates open files, semi-open files, locked pawn chains (rams), and pawn tension to
/// classify positions as Open vs Closed for root-pinned NNUE expert selection.
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
}
