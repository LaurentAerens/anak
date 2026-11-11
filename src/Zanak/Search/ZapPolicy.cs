using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// High-performance zero-branching greedy policy move selector.
/// Selects high-quality tactical, recapture, threat evasion, and positional moves
/// along a probe spine without expanding full search trees.
/// </summary>
public sealed class ZapPolicy
{
    private readonly ZapParams _params;
    private readonly ZapTranspositionTable? _tt;
    private readonly Move[] _killerMoves = new Move[128];

    #region Piece-Square Tables (Midgame)
    private static readonly int[] PawnPst =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
          5,  10,  10, -20, -20,  10,  10,   5,
          5,  -5, -10,   0,   0, -10,  -5,   5,
          0,   0,   0,  20,  20,   0,   0,   0,
          5,   5,  10,  25,  25,  10,   5,   5,
         10,  10,  20,  30,  30,  20,  10,  10,
         50,  50,  50,  50,  50,  50,  50,  50,
          0,   0,   0,   0,   0,   0,   0,   0
    ];

    private static readonly int[] KnightPst =
    [
        -50, -40, -30, -30, -30, -30, -40, -50,
        -40, -20,   0,   5,   5,   0, -20, -40,
        -30,   5,  10,  15,  15,  10,   5, -30,
        -30,   0,  15,  20,  20,  15,   0, -30,
        -30,   5,  15,  20,  20,  15,   5, -30,
        -30,   0,  10,  15,  15,  10,   0, -30,
        -40, -20,   0,   0,   0,   0, -20, -40,
        -50, -40, -30, -30, -30, -30, -40, -50
    ];

    private static readonly int[] BishopPst =
    [
        -20, -10, -10, -10, -10, -10, -10, -20,
        -10,   5,   0,   0,   0,   0,   5, -10,
        -10,  10,  10,  10,  10,  10,  10, -10,
        -10,   0,  10,  10,  10,  10,   0, -10,
        -10,   5,   5,  10,  10,   5,   5, -10,
        -10,   0,   5,  10,  10,   5,   0, -10,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -20, -10, -10, -10, -10, -10, -10, -20
    ];

    private static readonly int[] RookPst =
    [
          0,   0,   0,   5,   5,   0,   0,   0,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
          5,  10,  10,  10,  10,  10,  10,   5,
          0,   0,   0,   0,   0,   0,   0,   0
    ];

    private static readonly int[] QueenPst =
    [
        -20, -10, -10,  -5,  -5, -10, -10, -20,
        -10,   0,   5,   0,   0,   0,   0, -10,
        -10,   5,   5,   5,   5,   5,   0, -10,
          0,   0,   5,   5,   5,   5,   0,  -5,
         -5,   0,   5,   5,   5,   5,   0,  -5,
        -10,   0,   5,   5,   5,   5,   0, -10,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -20, -10, -10,  -5,  -5, -10, -10, -20
    ];

    private static readonly int[] KingPst =
    [
         20,  30,  10,   0,   0,  10,  30,  20,
         20,  20, -10, -30, -30, -10,  20,  20,
        -10, -20, -30, -40, -40, -30, -20, -10,
        -20, -30, -40, -50, -50, -40, -30, -20,
        -30, -40, -50, -60, -60, -50, -40, -30,
        -30, -40, -50, -60, -60, -50, -40, -30,
        -30, -40, -50, -60, -60, -50, -40, -30,
        -30, -40, -50, -60, -60, -50, -40, -30
    ];
    #endregion

    public ZapPolicy(ZapParams? parameters = null, ZapTranspositionTable? tt = null)
    {
        _params = parameters ?? ZapParams.Default;
        _tt = tt;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetKiller(int ply, Move move)
    {
        if ((uint)ply < (uint)_killerMoves.Length)
        {
            _killerMoves[ply] = move;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearKillers()
    {
        Array.Clear(_killerMoves);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetQuickMove(in Board board, int ply, out Move move)
    {
        return TryGetQuickMove(in board, ply, Move.Null, out move);
    }

    /// <summary>
    /// Attempts to find a single high-quality greedy move for the given board state.
    /// Prioritizes LVA recaptures, check evasions, TT hits, winning captures, and quiet heuristics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGetQuickMove(in Board board, int ply, Move lastMove, out Move move)
    {
        // Priority 0: Immediate Recapture Check via Least Valuable Aggressor (LVA)
        if (TryGetLvaRecapture(in board, lastMove, out Move lvaRecapture) && !AllowsMateIn1(in board, lvaRecapture))
        {
            move = lvaRecapture;
            return true;
        }

        // Priority 1: Check Evasions (when moving king is in check)
        if (board.IsInCheck())
        {
            MoveList evasions = default;
            MoveGenerator.GenerateLegalMoves(in board, ref evasions);
            if (evasions.Count == 0)
            {
                move = Move.Null;
                return false; // Checkmate!
            }

            Move bestEvasion = evasions[0];
            int bestEvasionScore = int.MinValue;

            for (int i = 0; i < evasions.Count; i++)
            {
                Move ev = evasions[i];
                int score = ScoreEvasion(in board, ev);
                if (AllowsMateIn1(in board, ev)) score -= 30000;
                else if (AllowsDecisiveLoss(in board, ev)) score -= 15000;

                if (score > bestEvasionScore)
                {
                    bestEvasionScore = score;
                    bestEvasion = ev;
                }
            }

            move = bestEvasion;
            return true;
        }

        // Priority 1.5: Immediate Winning Checkmate (if we can deliver checkmate in 1, play it immediately!)
        if (TryGetImmediateMateIn1(in board, out Move winningMate))
        {
            move = winningMate;
            return true;
        }

        // Priority 2: Transposition Table hit
        if (_tt != null && _tt.TryGet(board.ZobristKey, out ZapTTEntry entry) && !entry.BestMove.IsNull)
        {
            if (board.IsLegal(in entry.BestMove))
            {
                move = entry.BestMove;
                return true;
            }
        }

        // Detect if any friendly major/minor piece is en prise
        int threatenedValue = GetMaxThreatenedPiece(in board, out Square threatenedSquare);

        // Priority 3: Winning & Equal Tactical Captures
        // (Guards against hung pieces: if a piece is en prise, only allows captures that rescue the piece,
        // take the attacker, or capture an equal/higher-value opponent piece like their Queen!)
        if (TryGetWinningCapture(in board, lastMove, threatenedValue, threatenedSquare, out Move tacticalCapture) && !AllowsMateIn1(in board, tacticalCapture))
        {
            move = tacticalCapture;
            return true;
        }

        // Priority 3.5: Rescue En-Prise Major / Minor Piece!
        if (threatenedValue > 0 && TryFindSafeMoveForPiece(in board, threatenedSquare, board.SideToMove.Opponent(), out Move rescueMove) && !AllowsMateIn1(in board, rescueMove))
        {
            move = rescueMove;
            return true;
        }

        // Priority 4: Threat Evasion & Positional Quiet Move Heuristics
        MoveList legal = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legal);
        if (legal.Count == 0)
        {
            move = Move.Null;
            return false; // Stalemate
        }

        Move bestQuiet = Move.Null;
        Span<int> scores = legal.Count <= 64 ? stackalloc int[legal.Count] : new int[legal.Count];
        for (int i = 0; i < legal.Count; i++)
        {
            scores[i] = ScoreMove(in board, legal[i], ply, lastMove);
        }

        // Select the highest-scoring candidate that does not suicidally allow an immediate mate-in-1
        while (true)
        {
            int topIdx = -1;
            int topScore = int.MinValue;
            for (int i = 0; i < legal.Count; i++)
            {
                if (scores[i] > topScore)
                {
                    topScore = scores[i];
                    topIdx = i;
                }
            }

            if (topIdx < 0 || topScore <= -20000)
            {
                break;
            }

            Move candidate = legal[topIdx];
            if (!AllowsDecisiveLoss(in board, candidate))
            {
                bestQuiet = candidate;
                break;
            }

            // Suicidal move allowing opponent to checkmate us or deliver decisive loss in 1: penalize and pick next
            scores[topIdx] = -25000;
        }

        if (bestQuiet.IsNull)
        {
            bestQuiet = legal[0];
        }

        move = bestQuiet;
        return true;
    }

    /// <summary>
    /// Checks whether playing <paramref name="move"/> allows the opponent to deliver an immediate mate or decisive material win (e.g. checking fork winning Rook/Queen, double attack on heavy pieces, or free Queen/Rook win) on reply 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool AllowsDecisiveLoss(in Board board, Move move)
    {
        Board after = board;
        if (!after.MakeMove(move, out _)) return false;

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

                    // Pawn fork detection on reply (e.g. c7c6 forking Queen and Bishop):
                    if (replyPt == PieceType.Pawn)
                    {
                        byte toByte = (byte)reply.To;
                        ulong pAttacks = mover == Color.White
                            ? Bitboard.WhitePawnAttacks[toByte]
                            : Bitboard.BlackPawnAttacks[toByte];

                        ulong victimNonPawns = victimPieces & ~replyBoard.Pawns;
                        ulong forkedTargets = pAttacks & victimNonPawns;
                        if (BitOperations.PopCount(forkedTargets) >= 2)
                        {
                            return true; // Pawn fork winning a minor or major piece!
                        }
                    }

                    if (givesCheck)
                    {
                        MoveList evasions = default;
                        MoveGenerator.GenerateLegalMoves(in replyBoard, ref evasions);
                        if (evasions.Count == 0)
                        {
                            return true; // Immediate Mate in 1
                        }

                        // Can the checking piece be captured by any evasion?
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
                            // Checking fork: checking piece attacks an undefended heavy or minor piece
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
                                Square tSq = (Square)BitOperations.TrailingZeroCount(attackedTargets);
                                Piece tPiece = replyBoard.GetPieceAt(tSq);
                                PieceType tPt = tPiece.Type();
                                if (tPt != PieceType.King && tPt != PieceType.Pawn)
                                {
                                    if (!replyBoard.IsSquareAttacked(tSq, victimColor))
                                    {
                                        return true; // Checking fork winning an undefended minor/heavy piece!
                                    }
                                }
                                attackedTargets &= attackedTargets - 1;
                            }

                            // Uncapturable Queen check penetration into enemy camp (rank 6/7/8) with follow-up capture:
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
                                        return true; // Uncapturable Queen penetration with free capture on next move!
                                    }
                                }
                            }
                        }
                    }

                    if (reply.IsCapture)
                    {
                        int see = StaticExchangeEvaluator.Evaluate(in after, reply);

                        // If our move was a capture and opponent is recapturing on that square,
                        // subtract the material we just captured to measure the net tactical exchange:
                        if (move.IsCapture && reply.To == move.To)
                        {
                            Piece ourVictim = move.Type == MoveType.EnPassant
                                ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
                                : board.GetPieceAt(move.To);
                            if (ourVictim != Piece.None)
                            {
                                int ourGain = StaticExchangeEvaluator.GetPieceValue(ourVictim.Type());
                                see -= ourGain;
                            }
                        }

                        if (see >= 250)
                        {
                            // If the captured target was NOT the piece we just moved,
                            // and that piece was ALREADY attacked by enemy and undefended in board prior to our move:
                            Color us = board.SideToMove;
                            Color enemy = us.Opponent();
                            if (move.To != reply.To &&
                                board.IsSquareAttacked(reply.To, enemy) &&
                                !board.IsSquareAttacked(reply.To, us))
                            {
                                // Pre-existing hanging piece that was already en-prise prior to our move!
                            }
                            else
                            {
                                return true; // Free Queen, Rook, or Minor piece lost!
                            }
                        }

                        if (see >= 0)
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
                                Square hSq = (Square)BitOperations.TrailingZeroCount(forkedHeavy);
                                if (!replyBoard.IsSquareAttacked(hSq, victimColor))
                                {
                                    undefendedForkCount++;
                                }
                                forkedHeavy &= forkedHeavy - 1;
                            }

                            // If this capture forks 2+ heavy pieces with at least one undefended (e.g. Qxe6 attacking both Rooks), or gives check while attacking an undefended heavy piece:
                            if ((totalForkCount >= 2 && undefendedForkCount >= 1) || (givesCheck && undefendedForkCount >= 1))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        return false;
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

        // 1. Check Queen under attack (value = 900)
        ulong queens = board.Queens & friendly;
        while (queens != 0)
        {
            Square qSq = (Square)BitOperations.TrailingZeroCount(queens);
            if (board.IsSquareAttacked(qSq, enemy))
            {
                threatenedSquare = qSq;
                return 900;
            }
            queens &= queens - 1;
        }

        // 2. Check Rooks under attack by pawns or undefended rooks under attack (value = 500)
        ulong rooks = board.Rooks & friendly;
        while (rooks != 0)
        {
            Square rSq = (Square)BitOperations.TrailingZeroCount(rooks);
            byte sqByte = (byte)rSq;
            ulong pawnAttacks = enemy == Color.White ? Bitboard.BlackPawnAttacks[sqByte] : Bitboard.WhitePawnAttacks[sqByte];
            bool attackedByPawn = (pawnAttacks & enemyPawns) != 0;
            if (attackedByPawn || (board.IsSquareAttacked(rSq, enemy) && !board.IsSquareAttacked(rSq, us)))
            {
                threatenedSquare = rSq;
                return 500;
            }
            rooks &= rooks - 1;
        }

        // 3. Check Minor Pieces under attack by pawns or undefended minors under attack (value = 300)
        ulong minors = (board.Knights | board.Bishops) & friendly;
        while (minors != 0)
        {
            Square mSq = (Square)BitOperations.TrailingZeroCount(minors);
            byte sqByte = (byte)mSq;
            ulong pawnAttacks = enemy == Color.White ? Bitboard.BlackPawnAttacks[sqByte] : Bitboard.WhitePawnAttacks[sqByte];
            bool attackedByPawn = (pawnAttacks & enemyPawns) != 0;
            if (attackedByPawn || (board.IsSquareAttacked(mSq, enemy) && !board.IsSquareAttacked(mSq, us)))
            {
                threatenedSquare = mSq;
                return 300;
            }
            minors &= minors - 1;
        }

        threatenedSquare = Square.None;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool TryFindSafeMoveForPiece(in Board board, Square from, Color enemy, out Move move)
    {
        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);

        Move best = Move.Null;
        int bestScore = int.MinValue;

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (m.From != from) continue;

            int see = StaticExchangeEvaluator.Evaluate(in board, m);
            if (see >= 0)
            {
                int score = (m.IsCapture ? 2000 : 1000) + see + ScoreQuietMove(in board, m, 0);
                if (!AllowsDecisiveLoss(in board, m) && score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }
        }

        if (!best.IsNull)
        {
            move = best;
            return true;
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Priority 0: Zero-allocation Least Valuable Aggressor (LVA) recapture locality.
    /// If lastMove was a capture, directly targets lastMove.To and tests friendly attackers in LVA order:
    /// Pawn -> Knight -> Bishop -> Rook -> Queen -> King without running general movegen.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryGetLvaRecapture(in Board board, Move lastMove, out Move move)
    {
        if (lastMove.IsNull || !lastMove.IsCapture)
        {
            move = Move.Null;
            return false;
        }

        Square to = lastMove.To;
        byte toByte = (byte)to;
        Color us = board.SideToMove;
        ulong friendly = us == Color.White ? board.White : board.Black;
        ulong occ = board.Occupied;

        // 1. Pawn (Least Valuable Aggressor)
        ulong pawnAttackers = us == Color.White
            ? Bitboard.BlackPawnAttacks[toByte] & board.Pawns & friendly
            : Bitboard.WhitePawnAttacks[toByte] & board.Pawns & friendly;

        while (pawnAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(pawnAttackers);
            int rank = (int)to / 8;
            Move m = (rank == 7 || rank == 0)
                ? new Move(from, to, MoveType.QueenPromotionCapture)
                : new Move(from, to, MoveType.Capture);

            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            pawnAttackers &= pawnAttackers - 1;
        }

        // 2. Knight
        ulong knightAttackers = Bitboard.KnightAttacks[toByte] & board.Knights & friendly;
        while (knightAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(knightAttackers);
            Move m = new Move(from, to, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            knightAttackers &= knightAttackers - 1;
        }

        // 3. Bishop
        ulong bishopAttackers = MagicBitboards.GetBishopAttacks(to, occ) & board.Bishops & friendly;
        while (bishopAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(bishopAttackers);
            Move m = new Move(from, to, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            bishopAttackers &= bishopAttackers - 1;
        }

        // 4. Rook
        ulong rookAttackers = MagicBitboards.GetRookAttacks(to, occ) & board.Rooks & friendly;
        while (rookAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(rookAttackers);
            Move m = new Move(from, to, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            rookAttackers &= rookAttackers - 1;
        }

        // 5. Queen
        ulong queenAttackers = MagicBitboards.GetQueenAttacks(to, occ) & board.Queens & friendly;
        while (queenAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(queenAttackers);
            Move m = new Move(from, to, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            queenAttackers &= queenAttackers - 1;
        }

        // 6. King
        ulong kingAttackers = Bitboard.KingAttacks[toByte] & board.Kings & friendly;
        if (kingAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(kingAttackers);
            Move m = new Move(from, to, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Scans legal captures and returns the best capture with SEE >= 0,
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

                    // If a friendly major/minor piece is en prise:
                    // Only allow captures where:
                    // 1) The threatened piece itself makes the capture (escapes by capturing!), OR
                    // 2) The capture takes the opponent's attacking piece on lastMove.To, OR
                    // 3) The captured piece value is >= threatenedValue (e.g. capturing an opponent Queen for 900 when our Rook is attacked)
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
                                    continue; // Avoid capturing low-value pawns while our major piece is still hanging!
                                }
                            }
                        }
                    }

                    // Reject suicidal captures that walk into immediate mate-in-1
                    if (AllowsMateIn1(in board, cap))
                    {
                        continue;
                    }

                    // Heavily weight victim value: Queen (18000) > Rook (10000) > Minor (6600) > Pawn (2000)
                    int score = (victimVal * 20) - attackerVal + see;

                    // Recapture bonus if taking back on opponent's previous target square
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetWinningCapture(in Board board, Move lastMove, out Move move)
    {
        return TryGetWinningCapture(in board, lastMove, 0, Square.None, out move);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetWinningCapture(in Board board, out Move move)
    {
        return TryGetWinningCapture(in board, Move.Null, out move);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int ScoreMove(in Board board, Move move, int ply, Move lastMove = default)
    {
        if (move.IsCapture)
        {
            int seeVal = StaticExchangeEvaluator.Evaluate(in board, move);
            if (seeVal < 0)
            {
                // Losing exchange (e.g. Queen into defended pawn/bishop)
                return -10000 + seeVal;
            }

            Piece victim = move.Type == MoveType.EnPassant
                ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
                : board.GetPieceAt(move.To);
            Piece attacker = board.GetPieceAt(move.From);

            int victimVal = StaticExchangeEvaluator.GetPieceValue(victim.Type());
            int attackerVal = StaticExchangeEvaluator.GetPieceValue(attacker.Type());
            int score = 10000 + (victimVal * 20) - attackerVal + seeVal;

            // Recapture bonus
            if (!lastMove.IsNull && lastMove.IsCapture && move.To == lastMove.To)
            {
                score += _params.RecaptureBonus;
            }

            // Severe En-Prise check: if our Queen is attacked, and this capture does NOT save it or take the attacker:
            ulong ourPieces = board.SideToMove == Color.White ? board.White : board.Black;
            Color enemy = board.SideToMove.Opponent();
            ulong ourQueens = board.Queens & ourPieces;
            while (ourQueens != 0)
            {
                Square qSq = (Square)BitOperations.TrailingZeroCount(ourQueens);
                if (move.From != qSq && (lastMove.IsNull || move.To != lastMove.To) && victimVal < 900 && board.IsSquareAttacked(qSq, enemy))
                {
                    score -= 15000; // Never ignore our hanging Queen to capture a minor piece or pawn!
                    break;
                }
                ourQueens &= ourQueens - 1;
            }

            // En-Prise Minor check: if a minor is attacked by pawn or undefended, and this capture does not save it:
            if (victimVal < 300)
            {
                ulong ourMinors = (board.Knights | board.Bishops) & ourPieces;
                while (ourMinors != 0)
                {
                    Square mSq = (Square)BitOperations.TrailingZeroCount(ourMinors);
                    if (move.From != mSq && (lastMove.IsNull || move.To != lastMove.To))
                    {
                        byte mByte = (byte)mSq;
                        ulong enemyPawns = enemy == Color.White ? (board.White & board.Pawns) : (board.Black & board.Pawns);
                        ulong pawnAttacksM = enemy == Color.White ? Bitboard.BlackPawnAttacks[mByte] : Bitboard.WhitePawnAttacks[mByte];
                        bool attackedByPawn = (pawnAttacksM & enemyPawns) != 0;
                        if (attackedByPawn || (board.IsSquareAttacked(mSq, enemy) && !board.IsSquareAttacked(mSq, board.SideToMove)))
                        {
                            score -= 6000; // Never ignore our hanging minor piece to capture a pawn!
                            break;
                        }
                    }
                    ourMinors &= ourMinors - 1;
                }
            }

            return score;
        }

        return ScoreQuietMove(in board, move, ply);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ScoreEvasion(in Board board, Move move)
    {
        if (move.IsCapture)
        {
            int see = StaticExchangeEvaluator.Evaluate(in board, move);
            return see >= 0 ? 10000 + see : -10000 + see;
        }

        Piece piece = board.GetPieceAt(move.From);
        int score = ScoreQuietMove(in board, move, 0);
        if (piece.Type() == PieceType.King)
        {
            score += 400; // King is in check, so moving the King is forced, not a voluntary opening blunder
        }
        else
        {
            Board after = board;
            if (after.MakeMove(move, out _))
            {
                Color enemy = board.SideToMove.Opponent();
                if (after.IsSquareAttacked(move.To, enemy))
                {
                    score -= 500; // Interposing on an attacked square that is pinned to the King!
                }
            }
        }

        return score;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int ScoreQuietMove(in Board board, Move move, int ply)
    {
        int score = 0;
        Color us = board.SideToMove;
        Color enemy = us.Opponent();

        Piece piece = board.GetPieceAt(move.From);
        PieceType pt = piece.Type();

        // 1. Threat Evasion Bonus: moving an attacked piece to a safe square
        bool fromAttacked = board.IsSquareAttacked(move.From, enemy);
        bool toAttacked = board.IsSquareAttacked(move.To, enemy);
        bool isRimKnight = pt == PieceType.Knight && ((int)move.To.File() == 0 || (int)move.To.File() == 7);

        if (fromAttacked && !toAttacked && !isRimKnight)
        {
            score += pt switch
            {
                PieceType.Queen => 1200,
                PieceType.Rook => 800,
                PieceType.Bishop or PieceType.Knight => 500,
                _ => 0
            };
        }

        if (isRimKnight)
        {
            score -= 600; // Severe rim knight penalty: a knight on the rim is dim and easily trapped!
        }

        // Penalty for moving to an attacked square:
        byte toByte = (byte)move.To;
        ulong enemyPawns = enemy == Color.White ? (board.White & board.Pawns) : (board.Black & board.Pawns);
        ulong pawnAttacksTo = enemy == Color.White ? Bitboard.BlackPawnAttacks[toByte] : Bitboard.WhitePawnAttacks[toByte];
        bool toAttackedByPawn = (pawnAttacksTo & enemyPawns) != 0;

        if (toAttackedByPawn)
        {
            if (pt != PieceType.Pawn)
            {
                score -= 1500; // Never move Queen/Rook/Minor into an enemy pawn attack!
            }
            else if (!board.IsSquareAttacked(move.To, us))
            {
                score -= 1200; // Free pawn giveaway into enemy pawn
            }
            else
            {
                score -= 200; // Contested pawn move into enemy pawn attack
            }
        }
        else if (toAttacked && !board.IsSquareAttacked(move.To, us))
        {
            score -= 1200; // Never hang a piece on an undefended square!
        }

        // En passant vulnerability check for double pawn pushes
        if (pt == PieceType.Pawn && Math.Abs((int)move.To.Rank() - (int)move.From.Rank()) == 2)
        {
            Square passSq = (Square)(((int)move.From + (int)move.To) / 2);
            byte passByte = (byte)passSq;
            ulong pawnAttacksPass = enemy == Color.White ? Bitboard.BlackPawnAttacks[passByte] : Bitboard.WhitePawnAttacks[passByte];
            if ((pawnAttacksPass & enemyPawns) != 0)
            {
                score -= 1200; // Avoid double pushing into an enemy en-passant trap!
            }
        }

        // 1b. Check & Checkmate Detection
        if (board.GivesCheck(move))
        {
            Board checkBoard = board;
            if (checkBoard.MakeMove(move, out _))
            {
                MoveList replies = default;
                MoveGenerator.GenerateLegalMoves(in checkBoard, ref replies);
                if (replies.Count == 0)
                {
                    return 30000; // Checkmate in 1!
                }
            }

            // Only bonus safe checks! If the move hangs the checking piece, do NOT bonus it!
            if (StaticExchangeEvaluator.Evaluate(in board, move) >= 0)
            {
                // Dampen spite checks when our piece is en-prise and this move does not rescue it:
                int threatenedVal = GetMaxThreatenedPiece(in board, out Square threatenedSq);
                if (threatenedVal > 0 && move.From != threatenedSq)
                {
                    score += 200;
                }
                else
                {
                    score += 8500;
                }
            }
            else
            {
                score -= 8000; // Suicidal check giveaway!
            }
        }

        // 1c. Fork & Tactical Attack Threat Detection (Pawn & Knight forks)
        ulong enemyPieces = enemy == Color.White ? board.White : board.Black;
        if (pt == PieceType.Pawn && !toAttackedByPawn)
        {
            ulong attacksFromTo = us == Color.White
                ? Bitboard.WhitePawnAttacks[toByte]
                : Bitboard.BlackPawnAttacks[toByte];

            ulong attackedTargets = attacksFromTo & enemyPieces & ~enemyPawns;
            if (attackedTargets != 0)
            {
                int targetCount = BitOperations.PopCount(attackedTargets);
                if (targetCount >= 2)
                {
                    score += 1500; // Decisive pawn fork attacking multiple pieces!
                }
                else if ((attackedTargets & board.Queens) != 0)
                {
                    score += 900; // Pawn attacking enemy Queen!
                }
                else if ((attackedTargets & board.Rooks) != 0)
                {
                    score += 500; // Pawn attacking enemy Rook!
                }
                else
                {
                    score += 300; // Pawn attacking enemy Minor piece!
                }
            }
        }
        else if (pt == PieceType.Knight && (!toAttacked || board.IsSquareAttacked(move.To, us)))
        {
            ulong nAttacks = Bitboard.KnightAttacks[toByte];
            ulong attackedTargets = nAttacks & enemyPieces & (board.Queens | board.Rooks | board.Kings);
            if (BitOperations.PopCount(attackedTargets) >= 2)
            {
                score += 1200; // Decisive Knight royal/major fork!
            }
        }

        // 2. En-Prise Check: if our pieces are hanging under attack and this move doesn't save them
        ulong ourPieces = us == Color.White ? board.White : board.Black;

        // 2a. Queen en-prise (attacked by any enemy piece)
        ulong ourQueens = board.Queens & ourPieces;
        while (ourQueens != 0)
        {
            Square qSq = (Square)BitOperations.TrailingZeroCount(ourQueens);
            if (move.From != qSq && board.IsSquareAttacked(qSq, enemy))
            {
                score -= _params.EnPriseThreatPenalty; // Severe penalty for ignoring attacked Queen!
                break;
            }
            ourQueens &= ourQueens - 1;
        }

        // 2b. Rook en-prise (attacked and undefended)
        ulong ourRooks = board.Rooks & ourPieces;
        while (ourRooks != 0)
        {
            Square rSq = (Square)BitOperations.TrailingZeroCount(ourRooks);
            if (move.From != rSq && board.IsSquareAttacked(rSq, enemy) && !board.IsSquareAttacked(rSq, us))
            {
                score -= (_params.EnPriseThreatPenalty * 3 / 4);
                break;
            }
            ourRooks &= ourRooks - 1;
        }

        // 2c. Minor piece en-prise (attacked by pawn or undefended)
        ulong ourMinors = (board.Knights | board.Bishops) & ourPieces;
        while (ourMinors != 0)
        {
            Square mSq = (Square)BitOperations.TrailingZeroCount(ourMinors);
            if (move.From != mSq)
            {
                byte mByte = (byte)mSq;
                ulong pawnAttacksM = enemy == Color.White ? Bitboard.BlackPawnAttacks[mByte] : Bitboard.WhitePawnAttacks[mByte];
                bool attackedByPawn = (pawnAttacksM & enemyPawns) != 0;
                if (attackedByPawn || (board.IsSquareAttacked(mSq, enemy) && !board.IsSquareAttacked(mSq, us)))
                {
                    score -= (_params.EnPriseThreatPenalty / 2);
                    break;
                }
            }
            ourMinors &= ourMinors - 1;
        }

        // 3. Killer move bonus
        if ((uint)ply < (uint)_killerMoves.Length && move == _killerMoves[ply])
        {
            score += 150;
        }

        // 4. Castling bonus (king safety) & Early uncastled King move penalty
        if (move.IsCastling)
        {
            // If castling allows an immediate check by enemy queen/rook that cannot be captured,
            // castling does not provide king safety.
            Board afterCastling = board;
            bool hasUncapturableCheck = false;
            if (afterCastling.MakeMove(move, out _))
            {
                MoveList enemyChecks = default;
                MoveGenerator.GenerateLegalMoves(in afterCastling, ref enemyChecks);
                for (int c = 0; c < enemyChecks.Count; c++)
                {
                    Move ec = enemyChecks[c];
                    if (afterCastling.GivesCheck(ec))
                    {
                        Board checkB = afterCastling;
                        if (checkB.MakeMove(ec, out _))
                        {
                            MoveList evas = default;
                            MoveGenerator.GenerateLegalMoves(in checkB, ref evas);
                            bool canCapture = false;
                            for (int e = 0; e < evas.Count; e++)
                            {
                                if (evas[e].To == ec.To) { canCapture = true; break; }
                            }
                            if (!canCapture) { hasUncapturableCheck = true; break; }
                        }
                    }
                }
            }

            score += hasUncapturableCheck ? -400 : 800;
        }
        else if (pt == PieceType.King && ply < 16)
        {
            // Moving the King voluntarily in the opening/early middlegame destroys castling rights
            // and walks into open attacks.
            score -= 400;
        }

        // 5. Promotion bonus
        if (move.IsPromotion)
        {
            score += (move.Type == MoveType.QueenPromotion || move.Type == MoveType.QueenPromotionCapture) ? 800 : 300;
        }

        // 6. Piece-Square Table (PST) differential
        int fromIdx = us == Color.White ? (int)move.From : ((int)move.From ^ 56);
        int toIdx = us == Color.White ? (int)move.To : ((int)move.To ^ 56);

        int pstDelta = GetPstDelta(pt, fromIdx, toIdx);
        score += (pstDelta * _params.QuietPstWeight) / 100;

        // 7. Safety Filter: avoid moves that hang the moved piece
        if (_params.SafetyCheckEnabled)
        {
            int see = StaticExchangeEvaluator.Evaluate(in board, move);
            if (see < 0)
            {
                score -= 3000 - see; // Severe penalty for suicide moves
            }
        }

        // 8. Repetition & Draw Avoidance
        if (board.HalfmoveClock >= 4)
        {
            Board after = board;
            if (after.MakeMove(move, out _))
            {
                int repCount = after.RepetitionCount();
                if (repCount >= 2 || after.IsFiftyMoveRule())
                {
                    score -= 15000; // Immediate 3-fold repetition or 50-move draw
                }
                else if (repCount == 1)
                {
                    score -= 2500; // 2-fold repetition with past game history
                }
            }
        }

        return score;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPstDelta(PieceType pt, int fromIdx, int toIdx)
    {
        return pt switch
        {
            PieceType.Pawn => PawnPst[toIdx] - PawnPst[fromIdx],
            PieceType.Knight => KnightPst[toIdx] - KnightPst[fromIdx],
            PieceType.Bishop => BishopPst[toIdx] - BishopPst[fromIdx],
            PieceType.Rook => RookPst[toIdx] - RookPst[fromIdx],
            PieceType.Queen => QueenPst[toIdx] - QueenPst[fromIdx],
            PieceType.King => KingPst[toIdx] - KingPst[fromIdx],
            _ => 0
        };
    }
}
