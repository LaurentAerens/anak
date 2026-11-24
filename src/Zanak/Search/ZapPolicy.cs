using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// Stateless, zero-allocation greedy policy move selector.
/// Evaluates a strict 25-step bitboard priority spine without allocating or running general move generation
/// except in check evasion (Step 1) and final legal fallback (Step 25).
/// Equal capture trades are intentionally omitted from Steps 1–24 of this optimistic first spine.
/// </summary>
public sealed class ZapPolicy
{
    private readonly ZapParams _params;

    private enum LvaCaptureMode : byte
    {
        NonNegativeSee = 0,
        WinningForEvasion = 1,
        FreeCapture = 2
    }

    /// <summary>
    /// Initializes a new <see cref="ZapPolicy"/> with the given search parameters.
    /// </summary>
    public ZapPolicy(ZapParams? parameters = null)
    {
        _params = parameters ?? ZapParams.Default;
    }

    /// <summary>
    /// Selects a single greedy move using the 25-step stateless bitboard spine without a prior move hint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetQuickMove(in Board board, int ply, out Move move)
    {
        return TryGetQuickMove(in board, ply, Move.Null, out move);
    }

    /// <summary>
    /// Selects a single greedy move using the 25-step stateless bitboard spine:
    ///  1) Check evasion
    ///  2) Free queen capture
    ///  3) Queen evasion
    ///  4) Safe queen promotion
    ///  5) PxQ (not free)
    ///  6) NxQ (not free)
    ///  7) BxQ (not free)
    ///  8) Free rook capture
    ///  9) Rook evasion
    /// 10) RxQ (not free)
    /// 11) PxR (not free)
    /// 12) Free bishop capture
    /// 13) Bishop evasion
    /// 14) Free knight capture
    /// 15) Knight evasion
    /// 16) PxB (not free)
    /// 17) PxN (not free)
    /// 18) NxR (not free)
    /// 19) BxR (not free)
    /// 20) Free pawn capture
    /// 21) Pawn evasion
    /// 22) Safe check
    /// 23) NxB (not free)
    /// 24) Safe pawn push (closest to promotion rank first)
    /// 25) Safe legal fallback (else legal[0])
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGetQuickMove(in Board board, int ply, Move lastMove, out Move move)
    {
        _ = ply;

        // 1) Check evasion
        if (board.IsInCheck())
        {
            return TryGetCheckEvasion(in board, lastMove, out move);
        }

        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong theirPieces = them == Color.White ? board.White : board.Black;

        ulong enemyQueens = board.Queens & theirPieces;
        ulong enemyRooks = board.Rooks & theirPieces;
        ulong enemyBishops = board.Bishops & theirPieces;
        ulong enemyKnights = board.Knights & theirPieces;
        ulong enemyPawns = board.Pawns & theirPieces;

        // 2) Take free queen
        if (TryGetFreeCapture(in board, enemyQueens, 900, lastMove, out move)) return true;

        // 3) Queen evasion
        if (TryEvadeThreatenedPieces(in board, board.Queens & ourPieces, PieceType.Queen, 900, out move)) return true;

        // 4) Safe queen promotion
        if (TryGetSafeQueenPromotion(in board, out move)) return true;

        // Immediate LVA recapture when the opponent just captured on lastMove.To (SEE >= 0)
        if (lastMove.IsCapture && TryGetLvaRecapture(in board, lastMove, out move)) return true;

        // 5) Take queen (not free) with pawn
        if (TryGetWinningCaptureByAttacker(in board, enemyQueens, PieceType.Pawn, lastMove, out move)) return true;

        // 6) Take queen (not free) with knight
        if (TryGetWinningCaptureByAttacker(in board, enemyQueens, PieceType.Knight, lastMove, out move)) return true;

        // 7) Take queen (not free) with bishop
        if (TryGetWinningCaptureByAttacker(in board, enemyQueens, PieceType.Bishop, lastMove, out move)) return true;

        // 8) Take free rook
        if (TryGetFreeCapture(in board, enemyRooks, 500, lastMove, out move)) return true;

        // 9) Rook evasion
        if (TryEvadeThreatenedPieces(in board, board.Rooks & ourPieces, PieceType.Rook, 500, out move)) return true;

        // 10) Take queen (not free) with rook
        if (TryGetWinningCaptureByAttacker(in board, enemyQueens, PieceType.Rook, lastMove, out move)) return true;

        // 11) Take rook (not free) with pawn
        if (TryGetWinningCaptureByAttacker(in board, enemyRooks, PieceType.Pawn, lastMove, out move)) return true;

        // 12) Take free bishop
        if (TryGetFreeCapture(in board, enemyBishops, 330, lastMove, out move)) return true;

        // 13) Bishop evasion
        if (TryEvadeThreatenedPieces(in board, board.Bishops & ourPieces, PieceType.Bishop, 330, out move)) return true;

        // 14) Take free knight
        if (TryGetFreeCapture(in board, enemyKnights, 320, lastMove, out move)) return true;

        // 15) Knight evasion
        if (TryEvadeThreatenedPieces(in board, board.Knights & ourPieces, PieceType.Knight, 320, out move)) return true;

        // 16) Take bishop (not free) with pawn
        if (TryGetWinningCaptureByAttacker(in board, enemyBishops, PieceType.Pawn, lastMove, out move)) return true;

        // 17) Take knight (not free) with pawn
        if (TryGetWinningCaptureByAttacker(in board, enemyKnights, PieceType.Pawn, lastMove, out move)) return true;

        // 18) Take rook (not free) with knight
        if (TryGetWinningCaptureByAttacker(in board, enemyRooks, PieceType.Knight, lastMove, out move)) return true;

        // 19) Take rook (not free) with bishop
        if (TryGetWinningCaptureByAttacker(in board, enemyRooks, PieceType.Bishop, lastMove, out move)) return true;

        // 20) Take free pawn
        if (TryGetFreePawnCapture(in board, enemyPawns, lastMove, out move)) return true;

        // 21) Pawn evasion
        if (TryEvadeThreatenedPawns(in board, board.Pawns & ourPieces, out move)) return true;

        // 22) Safe check
        if (TryGetSafeCheck(in board, out move)) return true;

        // 23) Take bishop (not free) with knight
        if (TryGetWinningCaptureByAttacker(in board, enemyBishops, PieceType.Knight, lastMove, out move)) return true;

        // 23.5) Simplifying equal trade (Queen/Rook/Minor) when materially ahead (>= +200 cp)
        if (TryGetSimplifyingTrade(in board, out move)) return true;

        // 24) Safe pawn push (ranks 6-7 first, then safe passed pawns)
        bool isEndgame = board.Queens == 0;
        if (TryGetSafePawnPush(in board, isEndgame, out move)) return true;

        // 24.5) Phase-aware bitboard quiet rules (zero move-gen)
        if (!isEndgame)
        {
            if (TryGetSafeCastling(in board, out move)) return true;
            if (TryGetSafeMinorDevelopment(in board, out move)) return true;
            if (TryGetSafeCentralPawnPush(in board, out move)) return true;
        }
        else
        {
            if (TryGetSafeEndgameKingMove(in board, out move)) return true;
        }

        // 25) Fallback
        return TryGetFallbackMove(in board, out move);
    }

    #region 25-Step Spine Helpers

    /// <summary>
    /// Step 1: Resolves check, preferring winning/free captures, then non-losing quiet evasions
    /// (avoiding suicidal blunders and equal-trade captures whenever a sound evasion exists).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryGetCheckEvasion(in Board board, Move lastMove, out Move move)
    {
        MoveList evasions = default;
        MoveGenerator.GenerateLegalMoves(in board, ref evasions);
        if (evasions.Count == 0)
        {
            move = Move.Null;
            return false;
        }
        if (evasions.Count == 1)
        {
            move = evasions[0];
            return true;
        }

        Move bestMove = evasions[0];
        int bestTier = int.MaxValue;
        int bestScore = int.MinValue;

        for (int i = 0; i < evasions.Count; i++)
        {
            Move ev = evasions[i];
            bool safeDest = HasNonLosingDestination(in board, ev);
            bool allowsMate = AllowsMateIn1(in board, ev);

            int tier;
            int score;

            if (ev.IsCapture)
            {
                int see = StaticExchangeEvaluator.Evaluate(in board, ev);
                int attackerVal = StaticExchangeEvaluator.GetPieceValue(board.GetPieceAt(ev.From).Type());
                int recaptureBonus = !lastMove.IsNull && ev.To == lastMove.To ? 50 : 0;

                if (see > 0)
                {
                    bool exposesHigher = IntroducesHigherPieceLoss(in board, ev, see);
                    tier = exposesHigher ? 2 : 0;
                    score = (see * 20) - attackerVal + recaptureBonus;
                }
                else if (see == 0)
                {
                    tier = 3;
                    score = -attackerVal + recaptureBonus;
                }
                else
                {
                    tier = 4;
                    score = (see * 20) - attackerVal;
                }
            }
            else if (ev.IsPromotion && safeDest)
            {
                bool exposesHigher = IntroducesHigherPieceLoss(in board, ev, 800);
                tier = exposesHigher ? 2 : 0;
                score = 16000 + StaticExchangeEvaluator.GetPieceValue(ev.PromotionPieceType);
            }
            else if (safeDest)
            {
                bool exposesHigher = IntroducesHigherPieceLoss(in board, ev, 0);
                bool nonTradingSquare = IsDestinationSafe(in board, ev, maxAllowedEnemySee: -1);
                bool isKing = board.GetPieceAt(ev.From).Type() == PieceType.King;

                tier = exposesHigher ? 2 : 1;
                score = (nonTradingSquare ? 100 : 0) + (isKing ? 10 : 20);
            }
            else
            {
                int pieceVal = StaticExchangeEvaluator.GetPieceValue(board.GetPieceAt(ev.From).Type());
                tier = 4;
                score = -5000 - pieceVal;
            }

            if (allowsMate)
            {
                tier += 10;
            }

            if (tier < bestTier || (tier == bestTier && score > bestScore))
            {
                bestTier = tier;
                bestScore = score;
                bestMove = ev;
            }
        }

        move = bestMove;
        return true;
    }

    /// <summary>
    /// Steps 2, 8, 12, 14: Captures an effectively undefended enemy piece of the given type
    /// using the least valuable friendly attacker (LVA), avoiding mate-in-1 and higher-piece exposure.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetFreeCapture(in Board board, ulong targets, int victimValue, Move lastMove, out Move move)
    {
        if (targets == 0)
        {
            move = Move.Null;
            return false;
        }

        if (!lastMove.IsNull)
        {
            ulong lastBit = 1UL << (byte)lastMove.To;
            if ((targets & lastBit) != 0)
            {
                if (TryGetLvaCaptureWithMode(in board, lastMove.To, LvaCaptureMode.FreeCapture, victimValue, out move))
                {
                    return true;
                }
                targets &= ~lastBit;
            }
        }

        while (targets != 0)
        {
            Square targetSq = Bitboard.PopLsb(ref targets);
            if (TryGetLvaCaptureWithMode(in board, targetSq, LvaCaptureMode.FreeCapture, victimValue, out move))
            {
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Step 20: Captures a free enemy pawn (including en passant when free) using LVA order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetFreePawnCapture(in Board board, ulong enemyPawns, Move lastMove, out Move move)
    {
        if (TryGetFreeCapture(in board, enemyPawns, 100, lastMove, out move))
        {
            return true;
        }

        if (board.EpSquare != Square.None)
        {
            Color us = board.SideToMove;
            ulong ourPieces = us == Color.White ? board.White : board.Black;
            ulong epAttackers = (us == Color.White
                ? Bitboard.BlackPawnAttacks[(byte)board.EpSquare]
                : Bitboard.WhitePawnAttacks[(byte)board.EpSquare]) & board.Pawns & ourPieces;

            while (epAttackers != 0)
            {
                Square from = Bitboard.PopLsb(ref epAttackers);
                Move epMove = new Move(from, board.EpSquare, MoveType.EnPassant);
                if (IsLvaCandidateValid(in board, epMove, LvaCaptureMode.FreeCapture, 100))
                {
                    move = epMove;
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Unified LVA capture scan across Pawn -> Knight -> Bishop -> Rook -> Queen -> King
    /// parameterized by <see cref="LvaCaptureMode"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetLvaCaptureWithMode(
        in Board board,
        Square targetSq,
        LvaCaptureMode mode,
        int victimValue,
        out Move move)
    {
        byte toByte = (byte)targetSq;
        Color us = board.SideToMove;
        ulong friendly = us == Color.White ? board.White : board.Black;
        ulong occ = board.Occupied;
        int rank = (int)targetSq / 8;

        // 1. Pawn
        ulong pawnAttackers = (us == Color.White
            ? Bitboard.BlackPawnAttacks[toByte]
            : Bitboard.WhitePawnAttacks[toByte]) & board.Pawns & friendly;
        while (pawnAttackers != 0)
        {
            Square from = Bitboard.PopLsb(ref pawnAttackers);
            Move m = new Move(from, targetSq, (rank == 7 || rank == 0) ? MoveType.QueenPromotionCapture : MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, mode, victimValue)) { move = m; return true; }
        }

        // 2. Knight
        ulong knightAttackers = Bitboard.KnightAttacks[toByte] & board.Knights & friendly;
        while (knightAttackers != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref knightAttackers), targetSq, MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, mode, victimValue)) { move = m; return true; }
        }

        // 3. Bishop
        ulong bishopAttackers = MagicBitboards.GetBishopAttacks(targetSq, occ) & board.Bishops & friendly;
        while (bishopAttackers != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref bishopAttackers), targetSq, MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, mode, victimValue)) { move = m; return true; }
        }

        // 4. Rook
        ulong rookAttackers = MagicBitboards.GetRookAttacks(targetSq, occ) & board.Rooks & friendly;
        while (rookAttackers != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref rookAttackers), targetSq, MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, mode, victimValue)) { move = m; return true; }
        }

        // 5. Queen
        ulong queenAttackers = (MagicBitboards.GetBishopAttacks(targetSq, occ) | MagicBitboards.GetRookAttacks(targetSq, occ))
            & board.Queens & friendly;
        while (queenAttackers != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref queenAttackers), targetSq, MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, mode, victimValue)) { move = m; return true; }
        }

        // 6. King
        ulong kingAttackers = Bitboard.KingAttacks[toByte] & board.Kings & friendly;
        if (kingAttackers != 0)
        {
            Move m = new Move(Bitboard.Lsb(kingAttackers), targetSq, MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, mode, victimValue)) { move = m; return true; }
        }

        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLvaCandidateValid(in Board board, Move m, LvaCaptureMode mode, int victimValue)
    {
        if (!board.IsLegal(in m)) return false;

        int see = StaticExchangeEvaluator.Evaluate(in board, m);
        switch (mode)
        {
            case LvaCaptureMode.NonNegativeSee:
                return see >= 0;

            case LvaCaptureMode.WinningForEvasion:
                return see > 0 &&
                    HasNonLosingDestination(in board, m) &&
                    !IntroducesHigherPieceLoss(in board, m, see);

            case LvaCaptureMode.FreeCapture:
                return see >= victimValue &&
                    IsDestinationSafe(in board, m, maxAllowedEnemySee: 0) &&
                    !AllowsMateIn1(in board, m) &&
                    !IntroducesHigherPieceLoss(in board, m, victimValue);

            default:
                return false;
        }
    }

    /// <summary>
    /// Steps 5, 6, 7, 10, 11, 16, 17, 18, 19, 23: Captures a higher-value enemy piece using
    /// a specific friendly attacker type where SEE > 0 (net winning exchange, never equal trades).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetWinningCaptureByAttacker(
        in Board board,
        ulong targets,
        PieceType attackerType,
        Move lastMove,
        out Move move)
    {
        if (targets == 0)
        {
            move = Move.Null;
            return false;
        }

        Color us = board.SideToMove;
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong ourAttackersOfType = attackerType switch
        {
            PieceType.Pawn => board.Pawns & ourPieces,
            PieceType.Knight => board.Knights & ourPieces,
            PieceType.Bishop => board.Bishops & ourPieces,
            PieceType.Rook => board.Rooks & ourPieces,
            _ => 0UL
        };

        if (ourAttackersOfType == 0)
        {
            move = Move.Null;
            return false;
        }

        if (!lastMove.IsNull)
        {
            ulong lastBit = 1UL << (byte)lastMove.To;
            if ((targets & lastBit) != 0)
            {
                if (TryFindAttackerCaptureOnSquare(in board, lastMove.To, attackerType, ourAttackersOfType, out move))
                {
                    return true;
                }
                targets &= ~lastBit;
            }
        }

        while (targets != 0)
        {
            Square targetSq = Bitboard.PopLsb(ref targets);
            if (TryFindAttackerCaptureOnSquare(in board, targetSq, attackerType, ourAttackersOfType, out move))
            {
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFindAttackerCaptureOnSquare(
        in Board board,
        Square targetSq,
        PieceType attackerType,
        ulong ourAttackersOfType,
        out Move move)
    {
        byte toByte = (byte)targetSq;
        Color us = board.SideToMove;

        ulong candidates = attackerType switch
        {
            PieceType.Pawn => (us == Color.White
                ? Bitboard.BlackPawnAttacks[toByte]
                : Bitboard.WhitePawnAttacks[toByte]) & ourAttackersOfType,
            PieceType.Knight => Bitboard.KnightAttacks[toByte] & ourAttackersOfType,
            PieceType.Bishop => MagicBitboards.GetBishopAttacks(targetSq, board.Occupied) & ourAttackersOfType,
            PieceType.Rook => MagicBitboards.GetRookAttacks(targetSq, board.Occupied) & ourAttackersOfType,
            _ => 0UL
        };

        int rank = (int)targetSq / 8;
        while (candidates != 0)
        {
            Square from = Bitboard.PopLsb(ref candidates);
            MoveType mt = (attackerType == PieceType.Pawn && (rank == 0 || rank == 7))
                ? MoveType.QueenPromotionCapture
                : MoveType.Capture;
            Move m = new Move(from, targetSq, mt);

            if (!board.IsLegal(in m)) continue;

            int see = StaticExchangeEvaluator.Evaluate(in board, m);
            if (see <= 0) continue;
            if (AllowsMateIn1(in board, m)) continue;
            if (IntroducesHigherPieceLoss(in board, m, see)) continue;

            move = m;
            return true;
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Steps 3, 9, 13, 15: Evades friendly Queen, Rook, Bishop, or Knight when undefended under attack
    /// or attacked by a strictly cheaper piece.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryEvadeThreatenedPieces(
        in Board board,
        ulong piecesBb,
        PieceType pt,
        int pieceValue,
        out Move move)
    {
        if (piecesBb == 0)
        {
            move = Move.Null;
            return false;
        }

        Color us = board.SideToMove;
        Color them = us.Opponent();

        while (piecesBb != 0)
        {
            Square sq = Bitboard.PopLsb(ref piecesBb);
            if (IsThreatenedByCheaper(in board, sq, pieceValue, them, us))
            {
                if (TryEvadePiece(in board, sq, pt, pieceValue, out move))
                {
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Evades a threatened piece by:
    /// 1) Capturing an attacker safely with a winning/free LVA capture (resolving the threat without equal trades),
    /// 2) Capturing a free/winning target with the threatened piece itself,
    /// 3) Fleeing to a safe quiet square (preferring completely unattacked post-move squares), or
    /// 4) Safely interposing or pawn-defending when no capture or flight square exists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryEvadePiece(in Board board, Square fromSq, PieceType pt, int pieceValue, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong theirPieces = them == Color.White ? board.White : board.Black;
        ulong occ = board.Occupied;

        // 1. Remove an attacker with a strictly winning/free LVA capture (excluding equal trades)
        ulong attackers = StaticExchangeEvaluator.GetAllAttackers(in board, fromSq, occ) & theirPieces;
        int attackerCount = BitOperations.PopCount(attackers);
        ulong attackersCopy = attackers;
        while (attackersCopy != 0)
        {
            Square attackerSq = Bitboard.PopLsb(ref attackersCopy);
            if (TryGetLvaCaptureWithMode(in board, attackerSq, LvaCaptureMode.WinningForEvasion, 0, out Move capture))
            {
                Board after = board;
                if (after.MakeMove(capture, out _) &&
                    (capture.From == fromSq || !IsThreatenedByCheaper(in after, fromSq, pieceValue, them, us)) &&
                    !AllowsMateIn1(in board, capture))
                {
                    move = capture;
                    return true;
                }
            }
        }

        // 2. Threatened piece captures a winning/free target
        ulong rawAttacks = pt switch
        {
            PieceType.Queen => MagicBitboards.GetBishopAttacks(fromSq, occ) | MagicBitboards.GetRookAttacks(fromSq, occ),
            PieceType.Rook => MagicBitboards.GetRookAttacks(fromSq, occ),
            PieceType.Bishop => MagicBitboards.GetBishopAttacks(fromSq, occ),
            PieceType.Knight => Bitboard.KnightAttacks[(byte)fromSq],
            _ => 0UL
        };

        ulong captureTargets = rawAttacks & theirPieces;
        while (captureTargets != 0)
        {
            Square to = Bitboard.PopLsb(ref captureTargets);
            Move m = new Move(fromSq, to, MoveType.Capture);
            if (!board.IsLegal(in m)) continue;
            int see = StaticExchangeEvaluator.Evaluate(in board, m);
            if (see > 0 &&
                HasNonLosingDestination(in board, m) &&
                !AllowsMateIn1(in board, m) &&
                !IntroducesHigherPieceLoss(in board, m, Math.Max(see, pieceValue)))
            {
                move = m;
                return true;
            }
        }

        // 3a. Quiet flight squares completely unattacked on post-move board (respecting uncovered rays)
        ulong quietDests = rawAttacks & ~occ;
        ulong quietCopy = quietDests;
        while (quietCopy != 0)
        {
            Square to = Bitboard.PopLsb(ref quietCopy);
            Move m = new Move(fromSq, to, MoveType.Quiet);
            if (!board.IsLegal(in m)) continue;

            Board after = board;
            if (after.MakeMove(m, out _) &&
                !after.IsSquareAttacked(to, them) &&
                !AllowsMateIn1(in board, m) &&
                !IntroducesHigherPieceLoss(in board, m, pieceValue))
            {
                move = m;
                return true;
            }
        }

        // 3b. Quiet flight squares passing HasNonLosingDestination (preferring non-equal-trade invitations first)
        Move defendedQuietFallback = Move.Null;
        quietCopy = quietDests;
        while (quietCopy != 0)
        {
            Square to = Bitboard.PopLsb(ref quietCopy);
            Move m = new Move(fromSq, to, MoveType.Quiet);
            if (!board.IsLegal(in m)) continue;
            if (!HasNonLosingDestination(in board, m) || AllowsMateIn1(in board, m)) continue;
            if (IntroducesHigherPieceLoss(in board, m, pieceValue)) continue;

            if (IsDestinationSafe(in board, m, maxAllowedEnemySee: -1))
            {
                move = m;
                return true;
            }
            if (defendedQuietFallback.IsNull)
            {
                defendedQuietFallback = m;
            }
        }

        if (!defendedQuietFallback.IsNull)
        {
            move = defendedQuietFallback;
            return true;
        }

        // 4. Safe interposition between a single sliding attacker and the threatened piece
        if (attackerCount == 1)
        {
            Square attackerSq = Bitboard.Lsb(attackers);
            PieceType attType = board.GetPieceAt(attackerSq).Type();
            if (attType is PieceType.Bishop or PieceType.Rook or PieceType.Queen)
            {
                ulong between = Bitboard.Between[(int)fromSq, (int)attackerSq];
                while (between != 0)
                {
                    Square blockSq = Bitboard.PopLsb(ref between);
                    if (TryGetSafeQuietMoveToSquare(in board, blockSq, fromSq, pieceValue, out move))
                    {
                        return true;
                    }
                }
            }
        }

        // 5. If undefended against an equal/higher piece, try defending safely with a pawn push or piece move
        if (!board.IsSquareAttacked(fromSq, us))
        {
            bool attackedByCheaper = false;
            ulong attScan = attackers;
            while (attScan != 0)
            {
                Square aSq = Bitboard.PopLsb(ref attScan);
                if (pieceValue - StaticExchangeEvaluator.GetPieceValue(board.GetPieceAt(aSq).Type()) >= 100)
                {
                    attackedByCheaper = true;
                    break;
                }
            }

            if (!attackedByCheaper)
            {
                ulong pawnDefendSquares = us == Color.White
                    ? Bitboard.BlackPawnAttacks[(byte)fromSq]
                    : Bitboard.WhitePawnAttacks[(byte)fromSq];
                while (pawnDefendSquares != 0)
                {
                    Square defSq = Bitboard.PopLsb(ref pawnDefendSquares);
                    if ((occ & (1UL << (byte)defSq)) == 0 &&
                        TryGetSafePawnMoveToSquare(in board, defSq, fromSq, pieceValue, out move))
                    {
                        return true;
                    }
                }

                if (TryGetSafePieceDefense(in board, fromSq, pieceValue, out move))
                {
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Attempts to defend an undefended threatened piece at <paramref name="protectedSq"/> by moving a
    /// friendly Knight, Bishop, Rook, or King to an empty square from which it guards <paramref name="protectedSq"/>.
    /// Pure bitboard lookup; zero move-list generation.
    /// </summary>
    private static bool TryGetSafePieceDefense(
        in Board board,
        Square protectedSq,
        int protectedVal,
        out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong friendly = us == Color.White ? board.White : board.Black;
        ulong occ = board.Occupied;
        ulong empty = ~occ;
        ulong excludeProtected = ~(1UL << (byte)protectedSq);

        // 1. Knight defense
        ulong ourKnights = board.Knights & friendly & excludeProtected;
        if (ourKnights != 0)
        {
            ulong knightDefSquares = Bitboard.KnightAttacks[(byte)protectedSq] & empty;
            while (knightDefSquares != 0)
            {
                Square defSq = Bitboard.PopLsb(ref knightDefSquares);
                ulong candidates = Bitboard.KnightAttacks[(byte)defSq] & ourKnights;
                while (candidates != 0)
                {
                    Move m = new Move(Bitboard.PopLsb(ref candidates), defSq, MoveType.Quiet);
                    if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
                    {
                        move = m;
                        return true;
                    }
                }
            }
        }

        // 2. Bishop defense
        ulong ourBishops = board.Bishops & friendly & excludeProtected;
        if (ourBishops != 0)
        {
            ulong bishopDefSquares = MagicBitboards.GetBishopAttacks(protectedSq, occ) & empty;
            while (bishopDefSquares != 0)
            {
                Square defSq = Bitboard.PopLsb(ref bishopDefSquares);
                ulong candidates = MagicBitboards.GetBishopAttacks(defSq, occ) & ourBishops;
                while (candidates != 0)
                {
                    Move m = new Move(Bitboard.PopLsb(ref candidates), defSq, MoveType.Quiet);
                    if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
                    {
                        move = m;
                        return true;
                    }
                }
            }
        }

        // 3. Rook defense
        ulong ourRooks = board.Rooks & friendly & excludeProtected;
        if (ourRooks != 0)
        {
            ulong rookDefSquares = MagicBitboards.GetRookAttacks(protectedSq, occ) & empty;
            while (rookDefSquares != 0)
            {
                Square defSq = Bitboard.PopLsb(ref rookDefSquares);
                ulong candidates = MagicBitboards.GetRookAttacks(defSq, occ) & ourRooks;
                while (candidates != 0)
                {
                    Move m = new Move(Bitboard.PopLsb(ref candidates), defSq, MoveType.Quiet);
                    if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
                    {
                        move = m;
                        return true;
                    }
                }
            }
        }

        // 4. King defense (especially valuable in endgames)
        ulong ourKingBb = board.Kings & friendly;
        if (ourKingBb != 0)
        {
            Square kingSq = Bitboard.Lsb(ourKingBb);
            ulong kingDefSquares = Bitboard.KingAttacks[(byte)protectedSq] & Bitboard.KingAttacks[(byte)kingSq] & empty;
            while (kingDefSquares != 0)
            {
                Square defSq = Bitboard.PopLsb(ref kingDefSquares);
                Move m = new Move(kingSq, defSq, MoveType.Quiet);
                if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
                {
                    move = m;
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    private static bool TryGetSafeQuietMoveToSquare(
        in Board board,
        Square targetSq,
        Square protectedSq,
        int protectedVal,
        out Move move)
    {
        if (TryGetSafePawnMoveToSquare(in board, targetSq, protectedSq, protectedVal, out move))
        {
            return true;
        }

        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong friendly = us == Color.White ? board.White : board.Black;
        ulong occ = board.Occupied;
        ulong excludeProtected = ~(1UL << (byte)protectedSq);

        // Knight
        ulong knights = Bitboard.KnightAttacks[(byte)targetSq] & board.Knights & friendly & excludeProtected;
        while (knights != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref knights), targetSq, MoveType.Quiet);
            if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
            {
                move = m;
                return true;
            }
        }

        // Bishop
        ulong bishops = MagicBitboards.GetBishopAttacks(targetSq, occ) & board.Bishops & friendly & excludeProtected;
        while (bishops != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref bishops), targetSq, MoveType.Quiet);
            if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
            {
                move = m;
                return true;
            }
        }

        // Rook
        ulong rooks = MagicBitboards.GetRookAttacks(targetSq, occ) & board.Rooks & friendly & excludeProtected;
        while (rooks != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref rooks), targetSq, MoveType.Quiet);
            if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
            {
                move = m;
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    private static bool TryGetSafePawnMoveToSquare(
        in Board board,
        Square targetSq,
        Square protectedSq,
        int protectedVal,
        out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong friendly = us == Color.White ? board.White : board.Black;
        ulong ourPawns = board.Pawns & friendly;
        int pushDelta = us == Color.White ? 8 : -8;
        int toIdx = (int)targetSq;
        int toRank = toIdx / 8;

        int singleFromIdx = toIdx - pushDelta;
        if ((uint)singleFromIdx < 64 && (ourPawns & (1UL << singleFromIdx)) != 0)
        {
            MoveType mt = (toRank == 7 || toRank == 0) ? MoveType.QueenPromotion : MoveType.Quiet;
            Move m = new Move((Square)singleFromIdx, targetSq, mt);
            if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
            {
                move = m;
                return true;
            }
        }

        bool isDoubleRank = (us == Color.White && toRank == 3) || (us == Color.Black && toRank == 4);
        if (isDoubleRank && (board.Occupied & (1UL << singleFromIdx)) == 0)
        {
            int doubleFromIdx = toIdx - (pushDelta * 2);
            if ((uint)doubleFromIdx < 64 && (ourPawns & (1UL << doubleFromIdx)) != 0)
            {
                Move m = new Move((Square)doubleFromIdx, targetSq, MoveType.DoublePawnPush);
                if (IsSafeDefenseOrInterposition(in board, m, protectedSq, protectedVal, them, us))
                {
                    move = m;
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSafeDefenseOrInterposition(
        in Board board,
        Move m,
        Square protectedSq,
        int protectedVal,
        Color them,
        Color us)
    {
        if (!board.IsLegal(in m)) return false;
        if (!HasNonLosingDestination(in board, m)) return false;

        Board after = board;
        return after.MakeMove(m, out _) && !IsThreatenedByCheaper(in after, protectedSq, protectedVal, them, us);
    }

    /// <summary>
    /// Step 4: Safe Queen promotion (captures first, then quiet pushes) passing HasNonLosingDestination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetSafeQueenPromotion(in Board board, out Move move)
    {
        Color us = board.SideToMove;
        ulong friendly = us == Color.White ? board.White : board.Black;
        ulong enemies = us == Color.White ? board.Black : board.White;
        ulong promoRank = ZapTables.GetRelativeRankMask(us, 6);
        ulong pawns = board.Pawns & friendly & promoRank;

        while (pawns != 0)
        {
            Square from = Bitboard.PopLsb(ref pawns);
            ulong captures = (us == Color.White ? Bitboard.WhitePawnAttacks[(byte)from] : Bitboard.BlackPawnAttacks[(byte)from])
                & enemies & ~board.Kings;
            while (captures != 0)
            {
                Move capture = new(from, Bitboard.PopLsb(ref captures), MoveType.QueenPromotionCapture);
                if (board.IsLegal(in capture) &&
                    HasNonLosingDestination(in board, capture) &&
                    !AllowsMateIn1(in board, capture))
                {
                    move = capture;
                    return true;
                }
            }

            Square to = (Square)((int)from + (us == Color.White ? 8 : -8));
            Move push = new(from, to, MoveType.QueenPromotion);
            if ((board.Occupied & (1UL << (byte)to)) == 0 &&
                board.IsLegal(in push) &&
                HasNonLosingDestination(in board, push) &&
                !AllowsMateIn1(in board, push))
            {
                move = push;
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Step 21: Evades, captures with, or safely defends a threatened undefended friendly pawn.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryEvadeThreatenedPawns(in Board board, ulong ourPawns, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();

        while (ourPawns != 0)
        {
            Square myPawnSq = Bitboard.PopLsb(ref ourPawns);
            if (board.IsSquareAttacked(myPawnSq, them) && !board.IsSquareAttacked(myPawnSq, us))
            {
                if (TryEvadePawn(in board, myPawnSq, out move))
                {
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryEvadePawn(in Board board, Square pawnSq, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong occ = board.Occupied;
        ulong theirPieces = them == Color.White ? board.White : board.Black;
        int from = (int)pawnSq;
        int file = from & 7;
        int pushDelta = us == Color.White ? 8 : -8;

        // Capture left / right (respecting promotion rank and post-move destination safety)
        if (file > 0 && TrySafePawnCapture(in board, pawnSq, (Square)(from + pushDelta - 1), theirPieces, out move))
        {
            return true;
        }
        if (file < 7 && TrySafePawnCapture(in board, pawnSq, (Square)(from + pushDelta + 1), theirPieces, out move))
        {
            return true;
        }

        // En passant escape
        if (board.EpSquare != Square.None)
        {
            ulong pawnAtt = us == Color.White
                ? Bitboard.WhitePawnAttacks[from]
                : Bitboard.BlackPawnAttacks[from];
            if ((pawnAtt & (1UL << (byte)board.EpSquare)) != 0)
            {
                Move ep = new Move(pawnSq, board.EpSquare, MoveType.EnPassant);
                if (IsSafePawnEvasionMove(in board, ep, 100))
                {
                    move = ep;
                    return true;
                }
            }
        }

        // Single push escape
        Square toPush = (Square)(from + pushDelta);
        if ((occ & (1UL << (byte)toPush)) == 0)
        {
            int toRank = (int)toPush / 8;
            Move m = new Move(pawnSq, toPush, (toRank == 7 || toRank == 0) ? MoveType.QueenPromotion : MoveType.Quiet);
            if (IsSafePawnEvasionMove(in board, m, 100))
            {
                move = m;
                return true;
            }

            // Double push escape from home rank
            int fromRank = from / 8;
            bool onHomeRank = (us == Color.White && fromRank == 1) || (us == Color.Black && fromRank == 6);
            if (onHomeRank)
            {
                Square toDouble = (Square)(from + (pushDelta * 2));
                if ((occ & (1UL << (byte)toDouble)) == 0)
                {
                    Move dbl = new Move(pawnSq, toDouble, MoveType.DoublePawnPush);
                    if (IsSafePawnEvasionMove(in board, dbl, 100))
                    {
                        move = dbl;
                        return true;
                    }
                }
            }
        }

        // Safe pawn defense from behind
        ulong defendSquares = us == Color.White
            ? Bitboard.BlackPawnAttacks[from]
            : Bitboard.WhitePawnAttacks[from];
        while (defendSquares != 0)
        {
            Square defSq = Bitboard.PopLsb(ref defendSquares);
            if ((occ & (1UL << (byte)defSq)) == 0 &&
                TryGetSafePawnMoveToSquare(in board, defSq, pawnSq, 100, out move))
            {
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TrySafePawnCapture(in Board board, Square fromSq, Square toSq, ulong theirPieces, out Move move)
    {
        if ((theirPieces & (1UL << (byte)toSq)) != 0)
        {
            int toRank = (int)toSq / 8;
            Move m = new Move(fromSq, toSq, (toRank == 7 || toRank == 0) ? MoveType.QueenPromotionCapture : MoveType.Capture);
            if (IsSafePawnEvasionMove(in board, m, 100))
            {
                move = m;
                return true;
            }
        }
        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSafePawnEvasionMove(in Board board, Move m, int gainThreshold)
    {
        return board.IsLegal(in m) &&
            HasNonLosingDestination(in board, m) &&
            !AllowsMateIn1(in board, m) &&
            !IntroducesHigherPieceLoss(in board, m, gainThreshold);
    }

    /// <summary>
    /// Step 22: Safe checking moves in LVA order (Pawn -> Knight -> Bishop -> Rook -> Queen),
    /// excluding suicidal checks and equal capture/destination trades.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryGetSafeCheck(in Board board, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong theirPieces = them == Color.White ? board.White : board.Black;
        ulong enemyKingBb = board.Kings & theirPieces;
        if (enemyKingBb == 0)
        {
            move = Move.Null;
            return false;
        }

        Square kSq = Bitboard.Lsb(enemyKingBb);
        ulong occ = board.Occupied;
        int pushDelta = us == Color.White ? 8 : -8;

        // 1. Pawn checks (single push, double push, and winning capture checks)
        ulong pawnCheckSqs = (us == Color.White
            ? Bitboard.BlackPawnAttacks[(byte)kSq]
            : Bitboard.WhitePawnAttacks[(byte)kSq]) & ~ourPieces;
        while (pawnCheckSqs != 0)
        {
            Square to = Bitboard.PopLsb(ref pawnCheckSqs);
            int toRank = (int)to / 8;

            if ((theirPieces & (1UL << (byte)to)) != 0)
            {
                ulong pawnCaps = (us == Color.White
                    ? Bitboard.BlackPawnAttacks[(byte)to]
                    : Bitboard.WhitePawnAttacks[(byte)to]) & board.Pawns & ourPieces;
                while (pawnCaps != 0)
                {
                    Square from = Bitboard.PopLsb(ref pawnCaps);
                    Move m = new Move(from, to, (toRank == 7 || toRank == 0) ? MoveType.QueenPromotionCapture : MoveType.Capture);
                    if (IsSafeCheckMove(in board, m, isCap: true))
                    {
                        move = m;
                        return true;
                    }
                }
            }
            else if ((occ & (1UL << (byte)to)) == 0)
            {
                int fromSingle = (int)to - pushDelta;
                if ((uint)fromSingle < 64 && ((board.Pawns & ourPieces) & (1UL << fromSingle)) != 0)
                {
                    Move m = new Move((Square)fromSingle, to, (toRank == 7 || toRank == 0) ? MoveType.QueenPromotion : MoveType.Quiet);
                    if (IsSafeCheckMove(in board, m, isCap: false))
                    {
                        move = m;
                        return true;
                    }
                }

                bool isDoubleTargetRank = (us == Color.White && toRank == 3) || (us == Color.Black && toRank == 4);
                if (isDoubleTargetRank && (uint)fromSingle < 64 && (occ & (1UL << fromSingle)) == 0)
                {
                    int fromDouble = (int)to - (pushDelta * 2);
                    if ((uint)fromDouble < 64 && ((board.Pawns & ourPieces) & (1UL << fromDouble)) != 0)
                    {
                        Move m = new Move((Square)fromDouble, to, MoveType.DoublePawnPush);
                        if (IsSafeCheckMove(in board, m, isCap: false))
                        {
                            move = m;
                            return true;
                        }
                    }
                }
            }
        }

        // 2. Knight checks
        ulong knightCheckSqs = Bitboard.KnightAttacks[(byte)kSq] & ~ourPieces;
        if (TryFindCheckMoveFromMask(in board, knightCheckSqs, board.Knights & ourPieces, theirPieces, occ, PieceType.Knight, out move))
        {
            return true;
        }

        // 3. Bishop checks
        ulong bishopCheckSqs = MagicBitboards.GetBishopAttacks(kSq, occ) & ~ourPieces;
        if (TryFindCheckMoveFromMask(in board, bishopCheckSqs, board.Bishops & ourPieces, theirPieces, occ, PieceType.Bishop, out move))
        {
            return true;
        }

        // 4. Rook checks
        ulong rookCheckSqs = MagicBitboards.GetRookAttacks(kSq, occ) & ~ourPieces;
        if (TryFindCheckMoveFromMask(in board, rookCheckSqs, board.Rooks & ourPieces, theirPieces, occ, PieceType.Rook, out move))
        {
            return true;
        }

        // 5. Queen checks (diagonal & orthogonal)
        return TryFindCheckMoveFromMask(
            in board,
            bishopCheckSqs | rookCheckSqs,
            board.Queens & ourPieces,
            theirPieces,
            occ,
            PieceType.Queen,
            out move);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFindCheckMoveFromMask(
        in Board board,
        ulong checkSqs,
        ulong ourPieceBb,
        ulong theirPieces,
        ulong occ,
        PieceType pt,
        out Move move)
    {
        if (ourPieceBb == 0 || checkSqs == 0)
        {
            move = Move.Null;
            return false;
        }

        while (checkSqs != 0)
        {
            Square to = Bitboard.PopLsb(ref checkSqs);
            bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
            ulong attackers = pt switch
            {
                PieceType.Knight => Bitboard.KnightAttacks[(byte)to],
                PieceType.Bishop => MagicBitboards.GetBishopAttacks(to, occ),
                PieceType.Rook => MagicBitboards.GetRookAttacks(to, occ),
                PieceType.Queen => MagicBitboards.GetBishopAttacks(to, occ) | MagicBitboards.GetRookAttacks(to, occ),
                _ => 0UL
            } & ourPieceBb;

            while (attackers != 0)
            {
                Move m = new Move(Bitboard.PopLsb(ref attackers), to, isCap ? MoveType.Capture : MoveType.Quiet);
                if (IsSafeCheckMove(in board, m, isCap))
                {
                    move = m;
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSafeCheckMove(in Board board, Move m, bool isCap)
    {
        if (!board.IsLegal(in m)) return false;

        if (isCap)
        {
            int see = StaticExchangeEvaluator.Evaluate(in board, m);
            if (see <= 0 || !HasNonLosingDestination(in board, m))
            {
                return IsCheckmateMove(in board, m);
            }
        }
        else if (!IsDestinationSafe(in board, m, maxAllowedEnemySee: -1))
        {
            return IsCheckmateMove(in board, m);
        }

        return !AllowsMateIn1(in board, m);
    }

    private static bool IsCheckmateMove(in Board board, Move m)
    {
        Board after = board;
        if (!after.MakeMove(m, out _)) return false;
        MoveList evasions = default;
        MoveGenerator.GenerateLegalMoves(in after, ref evasions);
        return evasions.Count == 0;
    }

    private const ulong CenterDevelopmentMask = 0x00003C3C3C3C0000UL; // c3..f6

    /// <summary>
    /// Step 24: Safe pawn push scanning advanced friendly pawns on ranks 6–7 first,
    /// then safe passed pawns (ranks 4–5 in middlegame, ranks 2–5 in endgame).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetSafePawnPush(in Board board, bool isEndgame, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong occ = board.Occupied;
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong theirPieces = them == Color.White ? board.White : board.Black;
        ulong pawns = board.Pawns & ourPieces;
        if (pawns == 0)
        {
            move = Move.Null;
            return false;
        }

        int pushDelta = us == Color.White ? 8 : -8;

        for (int relRank = 6; relRank >= 5; relRank--)
        {
            ulong rankPawns = pawns & ZapTables.GetRelativeRankMask(us, relRank);
            while (rankPawns != 0)
            {
                Square from = Bitboard.PopLsb(ref rankPawns);
                Square to = (Square)((int)from + pushDelta);
                if ((occ & (1UL << (byte)to)) != 0) continue;

                int toRank = (int)to / 8;
                MoveType mt = (toRank == 7 || toRank == 0) ? MoveType.QueenPromotion : MoveType.Quiet;
                Move m = new Move(from, to, mt);
                if (board.IsLegal(in m) &&
                    HasNonLosingDestination(in board, m) &&
                    !AllowsMateIn1(in board, m) &&
                    !IntroducesHigherPieceLoss(in board, m, 0))
                {
                    move = m;
                    return true;
                }
            }
        }

        // Safe passed pawn pushes (furthest advanced first)
        ulong enemyPawns = board.Pawns & theirPieces;
        int minPassedRank = isEndgame ? 1 : 3;
        for (int relRank = 4; relRank >= minPassedRank; relRank--)
        {
            ulong rankPawns = pawns & ZapTables.GetRelativeRankMask(us, relRank);
            while (rankPawns != 0)
            {
                Square from = Bitboard.PopLsb(ref rankPawns);
                if ((enemyPawns & ZapTables.GetPawnForwardSpanMask(us, from)) != 0) continue;

                Square to = (Square)((int)from + pushDelta);
                if ((occ & (1UL << (byte)to)) != 0) continue;

                if (relRank == 1)
                {
                    Square toDouble = (Square)((int)from + (pushDelta * 2));
                    if ((occ & (1UL << (byte)toDouble)) == 0)
                    {
                        Move dbl = new Move(from, toDouble, MoveType.DoublePawnPush);
                        if (board.IsLegal(in dbl) &&
                            HasNonLosingDestination(in board, dbl))
                        {
                            move = dbl;
                            return true;
                        }
                    }
                }

                Move m = new Move(from, to, MoveType.Quiet);
                if (board.IsLegal(in m) &&
                    HasNonLosingDestination(in board, m))
                {
                    move = m;
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Step 24.5a (Opening/Middlegame): Safe castling (kingside first, then queenside). O(1) zero move-gen.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetSafeCastling(in Board board, out Move move)
    {
        Color us = board.SideToMove;
        CastlingRights rights = board.Castling;
        if (us == Color.White)
        {
            if ((rights & CastlingRights.WhiteKingside) != 0)
            {
                Move ks = new Move(Square.E1, Square.G1, MoveType.KingCastle);
                if (board.IsLegal(in ks))
                {
                    move = ks;
                    return true;
                }
            }
            if ((rights & CastlingRights.WhiteQueenside) != 0)
            {
                Move qs = new Move(Square.E1, Square.C1, MoveType.QueenCastle);
                if (board.IsLegal(in qs))
                {
                    move = qs;
                    return true;
                }
            }
        }
        else
        {
            if ((rights & CastlingRights.BlackKingside) != 0)
            {
                Move ks = new Move(Square.E8, Square.G8, MoveType.KingCastle);
                if (board.IsLegal(in ks))
                {
                    move = ks;
                    return true;
                }
            }
            if ((rights & CastlingRights.BlackQueenside) != 0)
            {
                Move qs = new Move(Square.E8, Square.C8, MoveType.QueenCastle);
                if (board.IsLegal(in qs))
                {
                    move = qs;
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Step 24.5b (Opening/Middlegame): Develop an undeveloped Knight or Bishop off the home rank
    /// onto a safe central/active square (c3..f6). Pure bitboard lookup; zero move-gen.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetSafeMinorDevelopment(in Board board, out Move move)
    {
        Color us = board.SideToMove;
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong homeRank = ZapTables.GetRelativeRankMask(us, 0);
        ulong occ = board.Occupied;
        ulong emptyCenter = ~occ & CenterDevelopmentMask;

        ulong homeKnights = board.Knights & ourPieces & homeRank;
        while (homeKnights != 0)
        {
            Square from = Bitboard.PopLsb(ref homeKnights);
            ulong targets = Bitboard.KnightAttacks[(byte)from] & emptyCenter;
            while (targets != 0)
            {
                Square to = Bitboard.PopLsb(ref targets);
                Move m = new Move(from, to, MoveType.Quiet);
                if (board.IsLegal(in m) &&
                    IsDestinationSafe(in board, m, maxAllowedEnemySee: -1))
                {
                    move = m;
                    return true;
                }
            }
        }

        ulong homeBishops = board.Bishops & ourPieces & homeRank;
        while (homeBishops != 0)
        {
            Square from = Bitboard.PopLsb(ref homeBishops);
            ulong targets = MagicBitboards.GetBishopAttacks(from, occ) & emptyCenter;
            while (targets != 0)
            {
                Square to = Bitboard.PopLsb(ref targets);
                Move m = new Move(from, to, MoveType.Quiet);
                if (board.IsLegal(in m) &&
                    IsDestinationSafe(in board, m, maxAllowedEnemySee: -1))
                {
                    move = m;
                    return true;
                }
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Step 24.5c (Opening/Middlegame): Safe central pawn advance (d/e files from home or third rank).
    /// Pure bitboard lookup; zero move-gen.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetSafeCentralPawnPush(in Board board, out Move move)
    {
        Color us = board.SideToMove;
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong occ = board.Occupied;
        ulong centerFiles = ZapTables.FileMasks[3] | ZapTables.FileMasks[4];
        int pushDelta = us == Color.White ? 8 : -8;

        ulong homeCenterPawns = board.Pawns & ourPieces & centerFiles & ZapTables.GetRelativeRankMask(us, 1);
        while (homeCenterPawns != 0)
        {
            Square from = Bitboard.PopLsb(ref homeCenterPawns);
            Square mid = (Square)((int)from + pushDelta);
            if ((occ & (1UL << (byte)mid)) != 0) continue;

            Square dblTo = (Square)((int)from + (pushDelta * 2));
            if ((occ & (1UL << (byte)dblTo)) == 0)
            {
                Move dbl = new Move(from, dblTo, MoveType.DoublePawnPush);
                if (board.IsLegal(in dbl) &&
                    IsDestinationSafe(in board, dbl, maxAllowedEnemySee: -1))
                {
                    move = dbl;
                    return true;
                }
            }

            Move single = new Move(from, mid, MoveType.Quiet);
            if (board.IsLegal(in single) &&
                IsDestinationSafe(in board, single, maxAllowedEnemySee: -1))
            {
                move = single;
                return true;
            }
        }

        ulong thirdRankCenterPawns = board.Pawns & ourPieces & centerFiles & ZapTables.GetRelativeRankMask(us, 2);
        while (thirdRankCenterPawns != 0)
        {
            Square from = Bitboard.PopLsb(ref thirdRankCenterPawns);
            Square to = (Square)((int)from + pushDelta);
            if ((occ & (1UL << (byte)to)) != 0) continue;

            Move single = new Move(from, to, MoveType.Quiet);
            if (board.IsLegal(in single) &&
                IsDestinationSafe(in board, single, maxAllowedEnemySee: -1))
            {
                move = single;
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Step 24.5 (Endgame): Activate the King toward the center or nearest pawn when queens are off.
    /// Pure bitboard lookup; zero move-gen.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetSafeEndgameKingMove(in Board board, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong theirPieces = them == Color.White ? board.White : board.Black;
        ulong ourKingBb = board.Kings & ourPieces;
        if (ourKingBb == 0)
        {
            move = Move.Null;
            return false;
        }

        Square kingSq = Bitboard.Lsb(ourKingBb);
        ulong kingTargets = Bitboard.KingAttacks[(byte)kingSq] & ~board.Occupied;
        if (kingTargets == 0)
        {
            move = Move.Null;
            return false;
        }

        // Optional pawn target: nearest enemy pawn, else nearest friendly pawn
        Square targetPawn = Square.None;
        int bestPawnDist = int.MaxValue;
        ulong scanPawns = (board.Pawns & theirPieces) != 0 ? (board.Pawns & theirPieces) : (board.Pawns & ourPieces);
        while (scanPawns != 0)
        {
            Square pSq = Bitboard.PopLsb(ref scanPawns);
            int d = ZapTables.GetChebyshevDistance(kingSq, pSq);
            if (d < bestPawnDist)
            {
                bestPawnDist = d;
                targetPawn = pSq;
            }
        }

        int currentScore = _centerScoreTable[(byte)kingSq] * 4
            + (targetPawn != Square.None ? (7 - bestPawnDist) * 3 : 0);

        Move bestMove = Move.Null;
        int bestScore = currentScore;

        while (kingTargets != 0)
        {
            Square to = Bitboard.PopLsb(ref kingTargets);
            int score = _centerScoreTable[(byte)to] * 4
                + (targetPawn != Square.None ? (7 - ZapTables.GetChebyshevDistance(to, targetPawn)) * 3 : 0);
            if (score <= bestScore) continue;

            Move m = new Move(kingSq, to, MoveType.Quiet);
            if (board.IsLegal(in m))
            {
                bestScore = score;
                bestMove = m;
            }
        }

        if (!bestMove.IsNull)
        {
            move = bestMove;
            return true;
        }

        move = Move.Null;
        return false;
    }

    private static readonly int[] _centerScoreTable =
    [
        0, 1, 2, 3, 3, 2, 1, 0,
        1, 2, 4, 5, 5, 4, 2, 1,
        2, 4, 6, 8, 8, 6, 4, 2,
        3, 5, 8, 10, 10, 8, 5, 3,
        3, 5, 8, 10, 10, 8, 5, 3,
        2, 4, 6, 8, 8, 6, 4, 2,
        1, 2, 4, 5, 5, 4, 2, 1,
        0, 1, 2, 3, 3, 2, 1, 0
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ScoreQuietCentrality(Move move)
    {
        return (_centerScoreTable[(byte)move.To] - _centerScoreTable[(byte)move.From]) * 2
            + (move.IsCastling ? 40 : 0);
    }

    /// <summary>
    /// Step 25: Legal fallback in MoveGenerator order, preferring HasNonLosingDestination without mate/decisive loss.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryGetFallbackMove(in Board board, out Move move)
    {
        MoveList legal = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legal);
        if (legal.Count == 0)
        {
            move = Move.Null;
            return false;
        }

        Move safeKingFallback = Move.Null;
        Move nonLosingNoMate = Move.Null;
        Move nonLosingAny = Move.Null;
        Move nonMateAny = Move.Null;

        for (int i = 0; i < legal.Count; i++)
        {
            Move candidate = legal[i];
            bool isNonCastlingKing = !candidate.IsCastling && board.GetPieceAt(candidate.From).Type() == PieceType.King;
            bool nonLosing = HasNonLosingDestination(in board, candidate);
            CheckOpponentThreats(in board, candidate, out bool allowsMate, out bool allowsDecisiveLoss);

            if (nonLosing && !allowsMate)
            {
                if (!allowsDecisiveLoss)
                {
                    if (!isNonCastlingKing)
                    {
                        move = candidate;
                        return true;
                    }
                    if (safeKingFallback.IsNull)
                    {
                        safeKingFallback = candidate;
                    }
                    continue;
                }
                if (nonLosingNoMate.IsNull)
                {
                    nonLosingNoMate = candidate;
                }
            }
            else if (nonLosing && nonLosingAny.IsNull)
            {
                nonLosingAny = candidate;
            }
            else if (!allowsMate && nonMateAny.IsNull)
            {
                nonMateAny = candidate;
            }
        }

        if (!safeKingFallback.IsNull)
        {
            move = safeKingFallback;
            return true;
        }
        if (!nonLosingNoMate.IsNull)
        {
            move = nonLosingNoMate;
            return true;
        }
        if (!nonLosingAny.IsNull)
        {
            move = nonLosingAny;
            return true;
        }
        if (!nonMateAny.IsNull)
        {
            move = nonMateAny;
            return true;
        }

        move = legal[0];
        return true;
    }

    #endregion

    #region Safety & Tactical Evaluation Helpers

    /// <summary>
    /// Returns true if playing <paramref name="move"/> leaves no legal enemy capture on its destination
    /// (or en passant square for double pawn pushes) whose SEE exceeds <paramref name="maxAllowedEnemySee"/>.
    /// </summary>
    private static bool IsDestinationSafe(in Board board, Move move, int maxAllowedEnemySee)
    {
        Board after = board;
        if (!after.MakeMove(move, out _)) return false;

        ulong enemyPieces = after.SideToMove == Color.White ? after.White : after.Black;
        ulong attackers = StaticExchangeEvaluator.GetAllAttackers(in after, move.To, after.Occupied) & enemyPieces;
        while (attackers != 0)
        {
            Square from = Bitboard.PopLsb(ref attackers);
            bool promotes = after.GetPieceAt(from).Type() == PieceType.Pawn &&
                ((int)move.To / 8 == 0 || (int)move.To / 8 == 7);
            Move capture = new Move(from, move.To, promotes ? MoveType.QueenPromotionCapture : MoveType.Capture);
            if (after.IsLegal(in capture) && StaticExchangeEvaluator.Evaluate(in after, capture) > maxAllowedEnemySee)
            {
                return false;
            }
        }

        if (after.EpSquare != Square.None && move.Type == MoveType.DoublePawnPush)
        {
            ulong epAttackers = (after.SideToMove == Color.White
                ? Bitboard.BlackPawnAttacks[(byte)after.EpSquare]
                : Bitboard.WhitePawnAttacks[(byte)after.EpSquare]) & after.Pawns & enemyPieces;
            while (epAttackers != 0)
            {
                Square from = Bitboard.PopLsb(ref epAttackers);
                Move epCapture = new Move(from, after.EpSquare, MoveType.EnPassant);
                if (after.IsLegal(in epCapture) && StaticExchangeEvaluator.Evaluate(in after, epCapture) > maxAllowedEnemySee)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasNonLosingDestination(in Board board, Move move)
    {
        Color them = board.SideToMove.Opponent();
        Piece captured = move.Type == MoveType.EnPassant
            ? PieceExtensions.Create(them, PieceType.Pawn)
            : board.GetPieceAt(move.To);

        int gain = StaticExchangeEvaluator.GetPieceValue(captured.Type());
        if (move.IsPromotion)
        {
            gain += StaticExchangeEvaluator.GetPieceValue(move.PromotionPieceType) - 100;
        }

        return IsDestinationSafe(in board, move, gain);
    }

    /// <summary>
    /// Fast O(1) bitboard check verifying <paramref name="move"/> does not uncover or leave
    /// a non-destination friendly piece worth strictly more than <paramref name="netGainCp"/> newly threatened.
    /// Skips checking moves because the opponent must answer the check first.
    /// </summary>
    private static bool IntroducesHigherPieceLoss(in Board board, Move move, int netGainCp)
    {
        if (netGainCp >= 900) return false;

        Board after = board;
        if (!after.MakeMove(move, out _)) return true;
        if (after.IsInCheck()) return false;

        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong ourAfter = us == Color.White ? after.White : after.Black;
        ulong excludeTo = ~(1UL << (byte)move.To);

        if (HasNewlyThreatenedPiece(in board, in after, after.Queens & ourAfter & excludeTo, 900, them, us)) return true;
        if (netGainCp >= 500) return false;
        if (HasNewlyThreatenedPiece(in board, in after, after.Rooks & ourAfter & excludeTo, 500, them, us)) return true;
        if (netGainCp >= 330) return false;
        if (HasNewlyThreatenedPiece(in board, in after, after.Bishops & ourAfter & excludeTo, 330, them, us)) return true;
        if (netGainCp >= 320) return false;
        return HasNewlyThreatenedPiece(in board, in after, after.Knights & ourAfter & excludeTo, 320, them, us);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasNewlyThreatenedPiece(
        in Board before,
        in Board after,
        ulong piecesBb,
        int value,
        Color them,
        Color us)
    {
        while (piecesBb != 0)
        {
            Square sq = Bitboard.PopLsb(ref piecesBb);
            if (!IsThreatenedByCheaper(in before, sq, value, them, us) &&
                IsThreatenedByCheaper(in after, sq, value, them, us))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A piece is threatened when it is undefended and attacked, or attacked by a clearly cheaper piece
    /// (a defender does not save a rook from a bishop: BxR RxB still loses the exchange).
    /// </summary>
    private static bool IsThreatenedByCheaper(in Board board, Square square, int value, Color enemy, Color us)
    {
        ulong enemies = enemy == Color.White ? board.White : board.Black;
        ulong attackers = StaticExchangeEvaluator.GetAllAttackers(in board, square, board.Occupied) & enemies;
        if (attackers == 0) return false;
        if (!board.IsSquareAttacked(square, us)) return true;
        while (attackers != 0)
        {
            Square attacker = Bitboard.PopLsb(ref attackers);
            if (value - StaticExchangeEvaluator.GetPieceValue(board.GetPieceAt(attacker).Type()) >= 100) return true;
        }
        return false;
    }

    #endregion

    #region Preserved Public / Internal API

    /// <summary>
    /// Checks whether playing <paramref name="move"/> allows the opponent to deliver an immediate mate or decisive material win
    /// (e.g. checking fork winning Rook/Queen, double attack on heavy pieces, or free Queen/Rook win) on reply 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CheckOpponentThreats(in Board board, Move move, out bool allowsMateIn1, out bool allowsDecisiveLoss)
    {
        allowsMateIn1 = false;
        allowsDecisiveLoss = false;
        Board after = board;
        if (!after.MakeMove(move, out _)) return;

        MoveList replies = default;
        MoveGenerator.GenerateLegalMoves(in after, ref replies);
        for (int i = 0; i < replies.Count; i++)
        {
            Move reply = replies[i];
            bool givesCheck = after.GivesCheck(reply);
            Piece replyPiece = after.GetPieceAt(reply.From);
            if (givesCheck || reply.IsCapture || replyPiece.Type() == PieceType.Pawn)
            {
                Board replyBoard = after;
                if (replyBoard.MakeMove(reply, out _))
                {
                    Piece replyPieceInBoard = replyBoard.GetPieceAt(reply.To);
                    PieceType replyPt = replyPieceInBoard.Type();
                    Color mover = after.SideToMove;
                    Color victimColor = mover.Opponent();
                    ulong victimPieces = victimColor == Color.White ? replyBoard.White : replyBoard.Black;

                    if (!allowsDecisiveLoss && replyPt == PieceType.Pawn)
                    {
                        byte toByte = (byte)reply.To;
                        ulong pAttacks = mover == Color.White
                            ? Bitboard.WhitePawnAttacks[toByte]
                            : Bitboard.BlackPawnAttacks[toByte];

                        ulong victimNonPawns = victimPieces & ~replyBoard.Pawns;
                        ulong forkedTargets = pAttacks & victimNonPawns;
                        if (BitOperations.PopCount(forkedTargets) >= 2)
                        {
                            allowsDecisiveLoss = true;
                        }
                    }

                    if (givesCheck)
                    {
                        MoveList evasions = default;
                        MoveGenerator.GenerateLegalMoves(in replyBoard, ref evasions);
                        if (evasions.Count == 0)
                        {
                            allowsMateIn1 = true;
                            allowsDecisiveLoss = true;
                            return;
                        }

                        if (!allowsDecisiveLoss)
                        {
                            bool canCaptureChecker = false;
                            for (int e = 0; e < evasions.Count; e++)
                            {
                                if (evasions[e].To == reply.To)
                                {
                                    canCaptureChecker = true;
                                    break;
                                }
                            }

                            if (!canCaptureChecker)
                            {
                                ulong attacksFromReply = replyPt switch
                                {
                                    PieceType.Queen => MagicBitboards.GetQueenAttacks(reply.To, replyBoard.Occupied),
                                    PieceType.Rook => MagicBitboards.GetRookAttacks(reply.To, replyBoard.Occupied),
                                    PieceType.Bishop => MagicBitboards.GetBishopAttacks(reply.To, replyBoard.Occupied),
                                    PieceType.Knight => Bitboard.KnightAttacks[(byte)reply.To],
                                    PieceType.Pawn => mover == Color.White ? Bitboard.WhitePawnAttacks[(byte)reply.To] : Bitboard.BlackPawnAttacks[(byte)reply.To],
                                    _ => 0
                                };

                                ulong attackedTargets = attacksFromReply & victimPieces;
                                while (attackedTargets != 0)
                                {
                                    Square tSq = Bitboard.PopLsb(ref attackedTargets);
                                    PieceType tPt = replyBoard.GetPieceAt(tSq).Type();
                                    if (tPt != PieceType.King && tPt != PieceType.Pawn)
                                    {
                                        if (!replyBoard.IsSquareAttacked(tSq, victimColor))
                                        {
                                            allowsDecisiveLoss = true;
                                            break;
                                        }
                                    }
                                }

                                if (!allowsDecisiveLoss)
                                {
                                    int rank = (int)reply.To / 8;
                                    bool isDeepInCamp = mover == Color.White ? rank >= 5 : rank <= 2;
                                    if (replyPt == PieceType.Queen && isDeepInCamp)
                                    {
                                        MoveList repliesAfterCheck = default;
                                        MoveGenerator.GenerateLegalCaptures(in replyBoard, ref repliesAfterCheck);
                                        for (int r = 0; r < repliesAfterCheck.Count; r++)
                                        {
                                            if (repliesAfterCheck[r].From == reply.To &&
                                                StaticExchangeEvaluator.Evaluate(in replyBoard, repliesAfterCheck[r]) >= 0)
                                            {
                                                allowsDecisiveLoss = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (!allowsDecisiveLoss && reply.IsCapture)
                    {
                        int see = StaticExchangeEvaluator.Evaluate(in after, reply);

                        if (move.IsCapture && reply.To == move.To)
                        {
                            Piece ourVictim = move.Type == MoveType.EnPassant
                                ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
                                : board.GetPieceAt(move.To);
                            if (ourVictim != Piece.None)
                            {
                                see -= StaticExchangeEvaluator.GetPieceValue(ourVictim.Type());
                            }
                        }

                        if (see >= 250)
                        {
                            Color us = board.SideToMove;
                            Color enemy = us.Opponent();
                            bool isPreExistingHangingPiece =
                                move.To != reply.To &&
                                board.IsSquareAttacked(reply.To, enemy) &&
                                !board.IsSquareAttacked(reply.To, us);
                            if (!isPreExistingHangingPiece)
                            {
                                allowsDecisiveLoss = true;
                            }
                        }

                        if (!allowsDecisiveLoss && see >= 0)
                        {
                            ulong heavyPieces = (replyBoard.Queens | replyBoard.Rooks) & victimPieces;
                            ulong attacksFromReply = replyPt switch
                            {
                                PieceType.Queen => MagicBitboards.GetQueenAttacks(reply.To, replyBoard.Occupied),
                                PieceType.Rook => MagicBitboards.GetRookAttacks(reply.To, replyBoard.Occupied),
                                PieceType.Bishop => MagicBitboards.GetBishopAttacks(reply.To, replyBoard.Occupied),
                                PieceType.Knight => Bitboard.KnightAttacks[(byte)reply.To],
                                PieceType.Pawn => mover == Color.White ? Bitboard.WhitePawnAttacks[(byte)reply.To] : Bitboard.BlackPawnAttacks[(byte)reply.To],
                                _ => 0
                            };

                            ulong forkedHeavy = attacksFromReply & heavyPieces;
                            int totalForkCount = BitOperations.PopCount(forkedHeavy);
                            int undefendedForkCount = 0;
                            while (forkedHeavy != 0)
                            {
                                Square hSq = Bitboard.PopLsb(ref forkedHeavy);
                                if (!replyBoard.IsSquareAttacked(hSq, victimColor))
                                {
                                    undefendedForkCount++;
                                }
                            }

                            if ((totalForkCount >= 2 && undefendedForkCount >= 1) || (givesCheck && undefendedForkCount >= 1))
                            {
                                allowsDecisiveLoss = true;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks whether playing <paramref name="move"/> allows the opponent to deliver an immediate checkmate on reply 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool AllowsMateIn1(in Board board, Move move)
    {
        Board after = board;
        if (!after.MakeMove(move, out _)) return false;

        MoveList replies = default;
        MoveGenerator.GenerateLegalMoves(in after, ref replies);
        for (int i = 0; i < replies.Count; i++)
        {
            Move reply = replies[i];
            if (after.GivesCheck(reply))
            {
                Board mateCheck = after;
                if (mateCheck.MakeMove(reply, out _))
                {
                    MoveList evasions = default;
                    MoveGenerator.GenerateLegalMoves(in mateCheck, ref evasions);
                    if (evasions.Count == 0)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Scans legal checking moves to see if an immediate checkmate-in-1 can be delivered.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryGetImmediateMateIn1(in Board board, out Move mateMove)
    {
        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (board.GivesCheck(m))
            {
                Board checkBoard = board;
                if (checkBoard.MakeMove(m, out _))
                {
                    MoveList evasions = default;
                    MoveGenerator.GenerateLegalMoves(in checkBoard, ref evasions);
                    if (evasions.Count == 0)
                    {
                        mateMove = m;
                        return true;
                    }
                }
            }
        }

        mateMove = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int GetMaxThreatenedPiece(in Board board, out Square threatenedSquare)
    {
        Color us = board.SideToMove;
        Color enemy = us.Opponent();
        ulong friendly = us == Color.White ? board.White : board.Black;
        ulong enemyPawns = enemy == Color.White ? (board.White & board.Pawns) : (board.Black & board.Pawns);

        ulong queens = board.Queens & friendly;
        while (queens != 0)
        {
            Square qSq = Bitboard.PopLsb(ref queens);
            if (board.IsSquareAttacked(qSq, enemy))
            {
                threatenedSquare = qSq;
                return 900;
            }
        }

        ulong rooks = board.Rooks & friendly;
        while (rooks != 0)
        {
            Square rSq = Bitboard.PopLsb(ref rooks);
            byte sqByte = (byte)rSq;
            ulong pawnAttacks = enemy == Color.White ? Bitboard.BlackPawnAttacks[sqByte] : Bitboard.WhitePawnAttacks[sqByte];
            bool attackedByPawn = (pawnAttacks & enemyPawns) != 0;
            if (attackedByPawn || IsThreatenedByCheaper(in board, rSq, 500, enemy, us))
            {
                threatenedSquare = rSq;
                return 500;
            }
        }

        ulong minors = (board.Knights | board.Bishops) & friendly;
        while (minors != 0)
        {
            Square mSq = Bitboard.PopLsb(ref minors);
            byte sqByte = (byte)mSq;
            ulong pawnAttacks = enemy == Color.White ? Bitboard.BlackPawnAttacks[sqByte] : Bitboard.WhitePawnAttacks[sqByte];
            bool attackedByPawn = (pawnAttacks & enemyPawns) != 0;
            if (attackedByPawn || (board.IsSquareAttacked(mSq, enemy) && !board.IsSquareAttacked(mSq, us)))
            {
                threatenedSquare = mSq;
                return 300;
            }
        }

        threatenedSquare = Square.None;
        return 0;
    }

    /// <summary>
    /// Zero-allocation Least Valuable Aggressor (LVA) recapture locality used by ZapProbe quiescence.
    /// When the recaptured square is undefended (and not a promotion rank), prefers Knight/Bishop over Pawn
    /// to avoid unnecessary pawn-structure damage when a minor piece can safely recapture.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryGetLvaRecapture(in Board board, Move lastMove, out Move move)
    {
        if (lastMove.IsNull || !lastMove.IsCapture)
        {
            move = Move.Null;
            return false;
        }

        Square targetSq = lastMove.To;
        int rank = (int)targetSq / 8;
        if (rank != 0 && rank != 7 && !board.IsSquareAttacked(targetSq, board.SideToMove.Opponent()))
        {
            if (TryGetMinorCapture(in board, targetSq, out move))
            {
                return true;
            }
        }

        return TryGetLvaCapture(in board, targetSq, out move);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetMinorCapture(in Board board, Square targetSq, out Move move)
    {
        byte toByte = (byte)targetSq;
        Color us = board.SideToMove;
        ulong friendly = us == Color.White ? board.White : board.Black;

        ulong knightAttackers = Bitboard.KnightAttacks[toByte] & board.Knights & friendly;
        while (knightAttackers != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref knightAttackers), targetSq, MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, LvaCaptureMode.NonNegativeSee, 0)) { move = m; return true; }
        }

        ulong bishopAttackers = MagicBitboards.GetBishopAttacks(targetSq, board.Occupied) & board.Bishops & friendly;
        while (bishopAttackers != 0)
        {
            Move m = new Move(Bitboard.PopLsb(ref bishopAttackers), targetSq, MoveType.Capture);
            if (IsLvaCandidateValid(in board, m, LvaCaptureMode.NonNegativeSee, 0)) { move = m; return true; }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Finds the least valuable friendly attacker that can legally capture on <paramref name="targetSq"/>
    /// with SEE >= 0. Order: Pawn -> Knight -> Bishop -> Rook -> Queen -> King.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetLvaCapture(in Board board, Square targetSq, out Move move)
    {
        return TryGetLvaCaptureWithMode(in board, targetSq, LvaCaptureMode.NonNegativeSee, 0, out move);
    }

    /// <summary>
    /// Scans legal captures and returns the best capture with SEE >= threshold,
    /// ordered strictly by victim piece value (Queen > Rook > Minor > Pawn + Recapture bonus).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGetWinningCapture(in Board board, Move lastMove, int threatenedValue, Square threatenedSquare, out Move move)
    {
        MoveList captures = default;
        MoveGenerator.GenerateLegalCaptures(in board, ref captures);

        if (captures.Count > 0)
        {
            Move bestCapture = Move.Null;
            int bestScore = int.MinValue;

            for (int i = 0; i < captures.Count; i++)
            {
                Move cap = captures[i];
                if (StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, cap, _params.WinningSeeThresholdCp))
                {
                    Piece victim = cap.Type == MoveType.EnPassant
                        ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
                        : board.GetPieceAt(cap.To);
                    Piece attacker = board.GetPieceAt(cap.From);

                    int victimVal = StaticExchangeEvaluator.GetPieceValue(victim.Type());
                    int attackerVal = StaticExchangeEvaluator.GetPieceValue(attacker.Type());
                    int see = StaticExchangeEvaluator.Evaluate(in board, cap);

                    if (threatenedValue > 0)
                    {
                        bool isThreatenedPieceMoving = cap.From == threatenedSquare;
                        bool capturesHigherOrEqualValue = victimVal >= threatenedValue;

                        if (!isThreatenedPieceMoving && !capturesHigherOrEqualValue)
                        {
                            Board afterCap = board;
                            if (afterCap.MakeMove(cap, out _))
                            {
                                Color enemyColor = board.SideToMove.Opponent();
                                if (afterCap.IsSquareAttacked(threatenedSquare, enemyColor))
                                {
                                    continue;
                                }
                            }
                        }
                    }

                    if (AllowsMateIn1(in board, cap))
                    {
                        continue;
                    }

                    int score = (victimVal * 20) - attackerVal + see;

                    if (!lastMove.IsNull && lastMove.IsCapture && cap.To == lastMove.To)
                    {
                        score += _params.RecaptureBonus;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestCapture = cap;
                    }
                }
            }

            if (!bestCapture.IsNull && board.IsLegal(in bestCapture))
            {
                move = bestCapture;
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Selects a winning capture (including immediate recaptures) from the current position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetWinningCapture(in Board board, Move lastMove, out Move move)
    {
        return TryGetWinningCapture(in board, lastMove, 0, Square.None, out move);
    }

    /// <summary>
    /// When the side to move is ahead in material by at least 200 cp, selects an equal-value (SEE >= 0)
    /// piece trade (Queen > Rook > Minor) to simplify the position and reduce opponent counterplay.
    /// </summary>
    private static bool TryGetSimplifyingTrade(in Board board, out Move move)
    {
        if (GetMaterialAdvantageCp(in board) < 200)
        {
            move = Move.Null;
            return false;
        }

        MoveList captures = default;
        MoveGenerator.GenerateLegalCaptures(in board, ref captures);
        Move bestTrade = Move.Null;
        int bestVictimValue = 0;

        for (int i = 0; i < captures.Count; i++)
        {
            Move cap = captures[i];
            PieceType victim = board.GetPieceAt(cap.To).Type();
            if (victim is PieceType.None or PieceType.Pawn) continue;

            int victimValue = StaticExchangeEvaluator.GetPieceValue(victim);
            if (victimValue <= bestVictimValue) continue;

            if (StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, cap, 0))
            {
                bestTrade = cap;
                bestVictimValue = victimValue;
            }
        }

        if (!bestTrade.IsNull)
        {
            move = bestTrade;
            return true;
        }

        move = Move.Null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetMaterialAdvantageCp(in Board board)
    {
        Color us = board.SideToMove;
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong theirPieces = us == Color.White ? board.Black : board.White;

        int ours =
            BitOperations.PopCount(board.Pawns & ourPieces) * 100 +
            BitOperations.PopCount(board.Knights & ourPieces) * 320 +
            BitOperations.PopCount(board.Bishops & ourPieces) * 330 +
            BitOperations.PopCount(board.Rooks & ourPieces) * 500 +
            BitOperations.PopCount(board.Queens & ourPieces) * 900;

        int theirs =
            BitOperations.PopCount(board.Pawns & theirPieces) * 100 +
            BitOperations.PopCount(board.Knights & theirPieces) * 320 +
            BitOperations.PopCount(board.Bishops & theirPieces) * 330 +
            BitOperations.PopCount(board.Rooks & theirPieces) * 500 +
            BitOperations.PopCount(board.Queens & theirPieces) * 900;

        return ours - theirs;
    }

    #endregion
}
