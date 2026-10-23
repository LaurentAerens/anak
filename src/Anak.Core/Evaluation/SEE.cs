using System.Numerics;
using System.Runtime.CompilerServices;

namespace Anak.Core;

/// <summary>
/// High-performance bitboard Static Exchange Evaluation (SEE).
/// Determines the static material outcome (gain/loss in centipawns) of a sequence of captures on a target square.
/// Used to heavily penalize losing captures in MCTS policy priors and accurately resolve leaf captures.
/// </summary>
public static class StaticExchangeEvaluator
{
    private static readonly int[] _pieceValues =
    [
        0,     // None
        100,   // Pawn
        320,   // Knight
        330,   // Bishop
        500,   // Rook
        900,   // Queen
        20000  // King
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPieceValue(PieceType pt) => pt switch
    {
        PieceType.Pawn => 100,
        PieceType.Knight => 320,
        PieceType.Bishop => 330,
        PieceType.Rook => 500,
        PieceType.Queen => 900,
        PieceType.King => 20000,
        _ => 0
    };

    /// <summary>
    /// Returns true if the static exchange evaluation of <paramref name="move"/> is greater than or equal to <paramref name="threshold"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasPositiveOrEqualSee(in Board board, Move move, int threshold = 0)
    {
        return Evaluate(in board, move) >= threshold;
    }

    /// <summary>
    /// Computes the Static Exchange Evaluation (SEE) score in centipawns for <paramref name="move"/>.
    /// Positive scores represent winning trades, 0 equal trades, and negative scores losing trades.
    /// </summary>
    public static int Evaluate(in Board board, Move move)
    {
        Square from = move.From;
        Square to = move.To;
        Piece movedPiece = board.GetPieceAt(from);
        if (movedPiece == Piece.None) return 0;

        PieceType attackerType = movedPiece.Type();
        Color us = board.SideToMove;
        Color them = us.Opponent();

        Piece capturedPiece = move.Type == MoveType.EnPassant
            ? PieceExtensions.Create(them, PieceType.Pawn)
            : board.GetPieceAt(to);

        int victimVal = capturedPiece != Piece.None ? GetPieceValue(capturedPiece.Type()) : 0;
        if (move.IsPromotion)
        {
            victimVal += GetPieceValue(move.PromotionPieceType) - 100;
        }

        // Fast path for quiet moves: if target square is undefended by opponent, SEE is >= 0
        if (capturedPiece == Piece.None && !move.IsPromotion)
        {
            if (!board.IsSquareAttacked(to, them))
            {
                return 0;
            }
        }

        Span<int> gain = stackalloc int[32];
        gain[0] = victimVal;

        ulong occupied = board.Occupied ^ (1UL << (byte)from);
        if (move.Type == MoveType.EnPassant)
        {
            Square epCapSq = SquareExtensions.Create(to.File(), from.Rank());
            occupied ^= 1UL << (byte)epCapSq;
        }

        ulong attackers = GetAllAttackers(in board, to, occupied);
        Color side = them;
        int d = 0;
        PieceType currentAttacker = attackerType;

        while (true)
        {
            d++;
            gain[d] = GetPieceValue(currentAttacker) - gain[d - 1];

            ulong sidePieces = side == Color.White ? board.White : board.Black;
            ulong sideAttackers = attackers & sidePieces & occupied;
            if (sideAttackers == 0)
            {
                break;
            }

            Square attackerSq = GetLeastValuableAttacker(in board, sideAttackers, side, out currentAttacker);
            if (currentAttacker == PieceType.None)
            {
                break;
            }

            occupied ^= 1UL << (byte)attackerSq;

            // X-rays uncovered?
            if (currentAttacker is PieceType.Pawn or PieceType.Bishop or PieceType.Queen)
            {
                attackers |= MagicBitboards.GetBishopAttacks(to, occupied) & (board.Bishops | board.Queens);
            }
            if (currentAttacker is PieceType.Rook or PieceType.Queen)
            {
                attackers |= MagicBitboards.GetRookAttacks(to, occupied) & (board.Rooks | board.Queens);
            }

            attackers &= occupied;
            side = side.Opponent();
        }

        // Negamax backpropagation
        while (--d > 0)
        {
            gain[d - 1] = -Math.Max(-gain[d - 1], gain[d]);
        }

        return gain[0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetAllAttackers(in Board board, Square to, ulong occupied)
    {
        int s = (byte)to;
        ulong attackers = 0UL;

        // Pawns
        attackers |= Bitboard.BlackPawnAttacks[s] & board.Pawns & board.White;
        attackers |= Bitboard.WhitePawnAttacks[s] & board.Pawns & board.Black;

        // Knights
        attackers |= Bitboard.KnightAttacks[s] & board.Knights;

        // Kings
        attackers |= Bitboard.KingAttacks[s] & board.Kings;

        // Bishops / Queens
        attackers |= MagicBitboards.GetBishopAttacks(to, occupied) & (board.Bishops | board.Queens);

        // Rooks / Queens
        attackers |= MagicBitboards.GetRookAttacks(to, occupied) & (board.Rooks | board.Queens);

        return attackers;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Square GetLeastValuableAttacker(in Board board, ulong sideAttackers, Color side, out PieceType type)
    {
        ulong subset;

        // 1. Pawn
        if ((subset = sideAttackers & board.Pawns) != 0)
        {
            type = PieceType.Pawn;
            return Bitboard.Lsb(subset);
        }

        // 2. Knight
        if ((subset = sideAttackers & board.Knights) != 0)
        {
            type = PieceType.Knight;
            return Bitboard.Lsb(subset);
        }

        // 3. Bishop
        if ((subset = sideAttackers & board.Bishops) != 0)
        {
            type = PieceType.Bishop;
            return Bitboard.Lsb(subset);
        }

        // 4. Rook
        if ((subset = sideAttackers & board.Rooks) != 0)
        {
            type = PieceType.Rook;
            return Bitboard.Lsb(subset);
        }

        // 5. Queen
        if ((subset = sideAttackers & board.Queens) != 0)
        {
            type = PieceType.Queen;
            return Bitboard.Lsb(subset);
        }

        // 6. King
        if ((subset = sideAttackers & board.Kings) != 0)
        {
            type = PieceType.King;
            return Bitboard.Lsb(subset);
        }

        type = PieceType.None;
        return Square.A1;
    }
}
