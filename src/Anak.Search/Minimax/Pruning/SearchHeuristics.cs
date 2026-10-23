using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Pre-computed search heuristics and topology-adaptive pruning helpers.
/// Implements:
/// - Logarithmic Late Move Reduction (LMR) table pre-computed via: 0.75 + ln(depth) * ln(moveIndex) / 2.25
/// - Topology-adaptive LMR and NMP adjustments based on PawnTopology openness
/// - Dynamic Null Move Pruning (NMP) reduction with zugzwang verification
/// - Static Null Move / Reverse Futility and Razor pruning margins
/// </summary>
public static class SearchHeuristics
{
    public const int MaxLmrDepth = 128;
    public const int MaxLmrMoves = 128;

    // SPSA Tuned Search Parameters
    public const int TunedLmrBase = 80;             // default: 76  (0.80)
    public const int TunedLmrDivision = 229;        // default: 227 (2.29)
    public const int TunedRfpMargin = 72;          // default: 73
    public const int TunedNmpBase = 3;              // default: 3
    public const int TunedAspirationDelta = 27;      // default: 26
    public const int TunedConthistReward = 2598;    // default: 2500
    public const int TunedConthistPenalty = 3839;   // default: 4000
    public const int TunedHistoryThreshold = 2101;  // default: 2000
    public const int TunedRfpImprovingMultiplier = 115; // default: 130 (1.15x)
    public const int TunedSingularMargin = 6;           // default: 2
    public const int TunedQsDeltaMargin = 188;          // default: 200

    /// <summary>
    /// Pre-computed LMR reduction table: [depth, moveIndex].
    /// </summary>
    public static readonly int[,] LmrTable = new int[MaxLmrDepth, MaxLmrMoves];

    static SearchHeuristics()
    {
        for (int depth = 1; depth < MaxLmrDepth; depth++)
        {
            for (int moves = 1; moves < MaxLmrMoves; moves++)
            {
                double reduction = (TunedLmrBase / 100.0) + (Math.Log(depth) * Math.Log(moves) / (TunedLmrDivision / 100.0));
                LmrTable[depth, moves] = (int)reduction;
            }
        }
    }

    /// <summary>
    /// Gets the base LMR reduction for a given depth and move index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBaseLmr(int depth, int moveIndex)
    {
        int d = Math.Clamp(depth, 0, MaxLmrDepth - 1);
        int m = Math.Clamp(moveIndex, 0, MaxLmrMoves - 1);
        return LmrTable[d, m];
    }

    /// <summary>
    /// Gets the base LMR reduction for a given depth and move count (alias for GetBaseLmr).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLmrReduction(int depth, int moveCount) => GetBaseLmr(depth, moveCount);

    /// <summary>
    /// Computes topology-adaptive LMR reduction adjusted for openness, PV status, and tactical context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AdjustLmr(int baseReduction, float openness, bool isPv = false, bool isTactical = false, bool inCheck = false)
    {
        if (inCheck || baseReduction <= 0) return 0;

        int r = baseReduction;

        // Reduce LMR for PV nodes
        if (isPv)
        {
            r = Math.Max(0, r - 1);
        }

        // Reduce LMR for tactical or capture moves
        if (isTactical)
        {
            r = Math.Max(0, r - 1);
        }

        // Open Structures (>= 0.55): tactics are sharper, reduce quiet move reductions
        if (openness >= 0.55f)
        {
            r = Math.Max(0, r - 1);
        }
        // Closed Structures (< 0.40): quiet maneuvers dominate, safely increase reduction
        else if (openness < 0.40f)
        {
            r += 1;
        }

        return Math.Max(0, r);
    }

    /// <summary>
    /// Alias for AdjustLmr.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AdjustLmrForTopology(int baseReduction, float openness, bool isPv = false, bool isTactical = false, bool inCheck = false)
        => AdjustLmr(baseReduction, openness, isPv, isTactical, inCheck);

    /// <summary>
    /// Computes dynamic Null-Move Pruning (NMP) reduction:
    /// R = nmpBase + depth / 6 + (eval - beta) / 200, adjusted for board openness.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetNmpReduction(int depth, int eval, int beta, float openness, int nmpBase = TunedNmpBase)
    {
        int baseR = nmpBase + (depth / 6);
        int evalMargin = Math.Clamp((eval - beta) / 200, 0, 3);
        int r = baseR + evalMargin;

        // In open positions with high tactical volatility, be more cautious with NMP
        if (openness >= 0.55f)
        {
            r = Math.Max(2, r - 1);
        }
        // In locked/closed structures, maneuvering is slow, aggressive NMP is safe
        else if (openness < 0.40f && depth >= 8)
        {
            r += 1;
        }

        return Math.Clamp(r, 1, depth);
    }

    /// <summary>
    /// Checks whether the side to move has non-pawn material (Knights, Bishops, Rooks, Queens).
    /// Used to prevent catastrophic zugzwang blunders in Null-Move Pruning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasNonPawnMaterial(in Board board, Color side)
    {
        ulong sidePieces = side == Color.White ? board.White : board.Black;
        ulong nonPawnNonKing = sidePieces & ~board.Pawns & ~board.Kings;
        return nonPawnNonKing != 0;
    }

    /// <summary>
    /// Reverse Futility Pruning (Static Null Move Pruning) margin:
    /// Prunes when staticEval - margin >= beta.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetReverseFutilityMargin(int depth, bool improving, float openness)
    {
        int baseMargin = improving ? 80 * depth : 120 * depth;
        if (openness < 0.40f)
        {
            baseMargin = (baseMargin * 3) / 4; // Tighter margin in closed structures
        }
        return baseMargin;
    }

    /// <summary>
    /// Futility Pruning margin for shallow depths:
    /// Prunes quiet moves when staticEval + margin <= alpha.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetFutilityMargin(int depth, bool improving)
    {
        return improving ? 100 * depth : 150 * depth;
    }

    /// <summary>
    /// Razor Pruning margin for very shallow plies (depth 1 to 3).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRazorMargin(int depth)
    {
        return depth switch
        {
            1 => 300,
            2 => 500,
            3 => 750,
            _ => 1000
        };
    }
}
