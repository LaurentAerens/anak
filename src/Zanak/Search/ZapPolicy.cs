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
    /// <summary>
    /// Attempts to find a single high-quality greedy move for the given board state using
    /// a lightweight, bitboard-first priority ladder without general move generation:
    /// Check 1: In check -> King safe square / King move / block or capture checker.
    /// Check 2: Can take enemy Queen? -> Take it.
    /// Check 3: Can enemy take my Queen? -> Evade to safe square.
    /// Check 4: Can take enemy Rook? -> Take it.
    /// Check 5: Can enemy take my Rook? -> Evade to safe square.
    /// Check 6: Can take enemy Bishop? -> Take it.
    /// Check 7: Can enemy take my Bishop? -> Evade to safe square.
    /// Check 8: Knight -> Can take enemy Knight? Take it. Can enemy take my Knight? Evade.
    /// Check 9: Can I give a check?
    /// Check 10: Can take enemy Pawn? -> Take it.
    /// Check 11: Can enemy take my Pawn? -> Push or capture with it.
    /// Check 12: Can I push a pawn?
    /// Fallback: Pick first legal move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGetQuickMove(in Board board, int ply, Move lastMove, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong theirPieces = them == Color.White ? board.White : board.Black;

        // Check 1: Am I in check?
        // Yes => Move king. Can't move king => block or capture checker.
        if (board.IsInCheck())
        {
            ulong kingBb = board.Kings & ourPieces;
            if (kingBb != 0)
            {
                Square kingSq = Bitboard.Lsb(kingBb);
                ulong kingAttacks = Bitboard.KingAttacks[(byte)kingSq] & ~ourPieces;

                // 1a: Try safe squares (not attacked by enemy)
                ulong safeKingSquares = kingAttacks;
                while (safeKingSquares != 0)
                {
                    Square to = (Square)BitOperations.TrailingZeroCount(safeKingSquares);
                    safeKingSquares &= safeKingSquares - 1;
                    if (!board.IsSquareAttacked(to, them))
                    {
                        bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
                        Move km = new Move(kingSq, to, isCap ? MoveType.Capture : MoveType.Quiet);
                        if (board.IsLegal(in km))
                        {
                            move = km;
                            return true;
                        }
                    }
                }

                // 1b: Try any king move (e.g. capturing checking piece directly)
                while (kingAttacks != 0)
                {
                    Square to = (Square)BitOperations.TrailingZeroCount(kingAttacks);
                    kingAttacks &= kingAttacks - 1;
                    bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
                    Move km = new Move(kingSq, to, isCap ? MoveType.Capture : MoveType.Quiet);
                    if (board.IsLegal(in km))
                    {
                        move = km;
                        return true;
                    }
                }
            }

            // 1c: King cannot move => block check or capture checker
            MoveList evasions = default;
            MoveGenerator.GenerateLegalMoves(in board, ref evasions);
            if (evasions.Count > 0)
            {
                move = evasions[0];
                return true;
            }

            // Checkmate!
            move = Move.Null;
            return false;
        }

        // Priority 0: Immediate Recapture locality (if lastMove was a capture, resolve immediate recapture on that square)
        if (!lastMove.IsNull && lastMove.IsCapture)
        {
            if (TryGetLvaCapture(in board, lastMove.To, out Move recapMove))
            {
                move = recapMove;
                return true;
            }
        }

        // Check 2: Can any of my pieces take enemy Queen? => Take it!
        ulong enemyQueens = board.Queens & theirPieces;
        while (enemyQueens != 0)
        {
            Square eqSq = (Square)BitOperations.TrailingZeroCount(enemyQueens);
            enemyQueens &= enemyQueens - 1;
            if (TryGetLvaCapture(in board, eqSq, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }

        // Check 3: Can any of their pieces take my Queen? => Move it somewhere (first safe square)
        ulong ourQueens = board.Queens & ourPieces;
        while (ourQueens != 0)
        {
            Square myQueenSq = (Square)BitOperations.TrailingZeroCount(ourQueens);
            ourQueens &= ourQueens - 1;
            if (board.IsSquareAttacked(myQueenSq, them))
            {
                if (TryEvadePiece(in board, myQueenSq, PieceType.Queen, out Move evadeMove))
                {
                    move = evadeMove;
                    return true;
                }
            }
        }

        // Check 4: Can any of my pieces take enemy Rook? => Take it!
        ulong enemyRooks = board.Rooks & theirPieces;
        // Prioritize lastMove.To if enemy just captured on that square
        if (!lastMove.IsNull && (enemyRooks & (1UL << (byte)lastMove.To)) != 0)
        {
            if (TryGetLvaCapture(in board, lastMove.To, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }
        while (enemyRooks != 0)
        {
            Square erSq = (Square)BitOperations.TrailingZeroCount(enemyRooks);
            enemyRooks &= enemyRooks - 1;
            if (TryGetLvaCapture(in board, erSq, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }

        // Check 5: Can any of their pieces take my Rook? => Move it somewhere (first safe square)
        ulong ourRooks = board.Rooks & ourPieces;
        while (ourRooks != 0)
        {
            Square myRookSq = (Square)BitOperations.TrailingZeroCount(ourRooks);
            ourRooks &= ourRooks - 1;
            if (board.IsSquareAttacked(myRookSq, them))
            {
                if (TryEvadePiece(in board, myRookSq, PieceType.Rook, out Move evadeMove))
                {
                    move = evadeMove;
                    return true;
                }
            }
        }

        // Check 6: Can any of my pieces take enemy Bishop? => Take it!
        ulong enemyBishops = board.Bishops & theirPieces;
        if (!lastMove.IsNull && (enemyBishops & (1UL << (byte)lastMove.To)) != 0)
        {
            if (TryGetLvaCapture(in board, lastMove.To, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }
        while (enemyBishops != 0)
        {
            Square ebSq = (Square)BitOperations.TrailingZeroCount(enemyBishops);
            enemyBishops &= enemyBishops - 1;
            if (TryGetLvaCapture(in board, ebSq, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }

        // Check 7: Can any of their pieces take my Bishop? => Move it somewhere (first safe square)
        ulong ourBishops = board.Bishops & ourPieces;
        while (ourBishops != 0)
        {
            Square myBishopSq = (Square)BitOperations.TrailingZeroCount(ourBishops);
            ourBishops &= ourBishops - 1;
            if (board.IsSquareAttacked(myBishopSq, them))
            {
                if (TryEvadePiece(in board, myBishopSq, PieceType.Bishop, out Move evadeMove))
                {
                    move = evadeMove;
                    return true;
                }
            }
        }

        // Check 8a: Can any of my pieces take enemy Knight? => Take it!
        ulong enemyKnights = board.Knights & theirPieces;
        if (!lastMove.IsNull && (enemyKnights & (1UL << (byte)lastMove.To)) != 0)
        {
            if (TryGetLvaCapture(in board, lastMove.To, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }
        while (enemyKnights != 0)
        {
            Square enSq = (Square)BitOperations.TrailingZeroCount(enemyKnights);
            enemyKnights &= enemyKnights - 1;
            if (TryGetLvaCapture(in board, enSq, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }

        // Check 8b: Can any of their pieces take my Knight? => Move it somewhere (first safe square)
        ulong ourKnights = board.Knights & ourPieces;
        while (ourKnights != 0)
        {
            Square myKnightSq = (Square)BitOperations.TrailingZeroCount(ourKnights);
            ourKnights &= ourKnights - 1;
            if (board.IsSquareAttacked(myKnightSq, them))
            {
                if (TryEvadePiece(in board, myKnightSq, PieceType.Knight, out Move evadeMove))
                {
                    move = evadeMove;
                    return true;
                }
            }
        }

        // Check 9: Can I give a check?
        if (TryGetCheckMove(in board, out Move checkMove))
        {
            move = checkMove;
            return true;
        }

        // Check 10: Can any of my pieces take enemy Pawn? => Take it!
        ulong enemyPawns = board.Pawns & theirPieces;
        if (!lastMove.IsNull && (enemyPawns & (1UL << (byte)lastMove.To)) != 0)
        {
            if (TryGetLvaCapture(in board, lastMove.To, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }
        while (enemyPawns != 0)
        {
            Square epSq = (Square)BitOperations.TrailingZeroCount(enemyPawns);
            enemyPawns &= enemyPawns - 1;
            if (TryGetLvaCapture(in board, epSq, out Move capMove))
            {
                move = capMove;
                return true;
            }
        }

        // Check 11: Can any of their pieces take my Pawn? => Push or capture with it!
        ulong ourPawns = board.Pawns & ourPieces;
        while (ourPawns != 0)
        {
            Square myPawnSq = (Square)BitOperations.TrailingZeroCount(ourPawns);
            ourPawns &= ourPawns - 1;
            if (board.IsSquareAttacked(myPawnSq, them))
            {
                if (TryEvadePawn(in board, myPawnSq, out Move evadeMove))
                {
                    move = evadeMove;
                    return true;
                }
            }
        }

        // Check 12: Can I push a pawn?
        if (TryGetPawnPush(in board, out Move pushMove))
        {
            move = pushMove;
            return true;
        }

        // Fallback: Pick first legal move
        MoveList legal = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legal);
        if (legal.Count > 0)
        {
            move = legal[0];
            return true;
        }

        move = Move.Null;
        return false;
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

        return TryGetLvaCapture(in board, lastMove.To, out move);
    }

    /// <summary>
    /// Finds the least valuable friendly attacker that can legally capture on <paramref name="targetSq"/>.
    /// Order: Pawn -> Knight -> Bishop -> Rook -> Queen -> King.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetLvaCapture(in Board board, Square targetSq, out Move move)
    {
        byte toByte = (byte)targetSq;
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
            int rank = (int)targetSq / 8;
            Move m = (rank == 7 || rank == 0)
                ? new Move(from, targetSq, MoveType.QueenPromotionCapture)
                : new Move(from, targetSq, MoveType.Capture);

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
            Move m = new Move(from, targetSq, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            knightAttackers &= knightAttackers - 1;
        }

        // 3. Bishop
        ulong bishopAttackers = MagicBitboards.GetBishopAttacks(targetSq, occ) & board.Bishops & friendly;
        while (bishopAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(bishopAttackers);
            Move m = new Move(from, targetSq, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            bishopAttackers &= bishopAttackers - 1;
        }

        // 4. Rook
        ulong rookAttackers = MagicBitboards.GetRookAttacks(targetSq, occ) & board.Rooks & friendly;
        while (rookAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(rookAttackers);
            Move m = new Move(from, targetSq, MoveType.Capture);
            if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
            {
                move = m;
                return true;
            }
            rookAttackers &= rookAttackers - 1;
        }

        // 5. Queen
        ulong queenAttackers = (MagicBitboards.GetBishopAttacks(targetSq, occ) | MagicBitboards.GetRookAttacks(targetSq, occ)) & board.Queens & friendly;
        while (queenAttackers != 0)
        {
            Square from = (Square)BitOperations.TrailingZeroCount(queenAttackers);
            Move m = new Move(from, targetSq, MoveType.Capture);
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
            Move m = new Move(from, targetSq, MoveType.Capture);
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
    /// Evades an attacked piece by moving it to the first safe (unattacked) square, or any legal square.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryEvadePiece(in Board board, Square fromSq, PieceType pt, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong theirPieces = them == Color.White ? board.White : board.Black;
        ulong occ = board.Occupied;

        ulong destMask = pt switch
        {
            PieceType.Queen => (MagicBitboards.GetBishopAttacks(fromSq, occ) | MagicBitboards.GetRookAttacks(fromSq, occ)) & ~ourPieces,
            PieceType.Rook => MagicBitboards.GetRookAttacks(fromSq, occ) & ~ourPieces,
            PieceType.Bishop => MagicBitboards.GetBishopAttacks(fromSq, occ) & ~ourPieces,
            PieceType.Knight => Bitboard.KnightAttacks[(byte)fromSq] & ~ourPieces,
            _ => 0
        };

        // First pass: look for a safe square (not attacked by enemy)
        ulong safeSquares = destMask;
        while (safeSquares != 0)
        {
            Square to = (Square)BitOperations.TrailingZeroCount(safeSquares);
            safeSquares &= safeSquares - 1;
            if (!board.IsSquareAttacked(to, them))
            {
                bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
                Move m = new Move(fromSq, to, isCap ? MoveType.Capture : MoveType.Quiet);
                if (board.IsLegal(in m))
                {
                    move = m;
                    return true;
                }
            }
        }

        // Second pass: try any destination square
        while (destMask != 0)
        {
            Square to = (Square)BitOperations.TrailingZeroCount(destMask);
            destMask &= destMask - 1;
            bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
            Move m = new Move(fromSq, to, isCap ? MoveType.Capture : MoveType.Quiet);
            if (board.IsLegal(in m))
            {
                move = m;
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Finds a checking move directed at the enemy King using piece attack bitboards.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetCheckMove(in Board board, out Move move)
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

        // 1. Knight checks
        ulong knightCheckSqs = Bitboard.KnightAttacks[(byte)kSq] & ~ourPieces;
        while (knightCheckSqs != 0)
        {
            Square to = (Square)BitOperations.TrailingZeroCount(knightCheckSqs);
            knightCheckSqs &= knightCheckSqs - 1;

            ulong attackers = Bitboard.KnightAttacks[(byte)to] & board.Knights & ourPieces;
            while (attackers != 0)
            {
                Square from = (Square)BitOperations.TrailingZeroCount(attackers);
                attackers &= attackers - 1;
                bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
                Move m = new Move(from, to, isCap ? MoveType.Capture : MoveType.Quiet);
                if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
                {
                    move = m;
                    return true;
                }
            }
        }

        // 2. Bishop & Queen diagonal checks
        ulong bishopCheckSqs = MagicBitboards.GetBishopAttacks(kSq, occ) & ~ourPieces;
        while (bishopCheckSqs != 0)
        {
            Square to = (Square)BitOperations.TrailingZeroCount(bishopCheckSqs);
            bishopCheckSqs &= bishopCheckSqs - 1;

            ulong attackers = MagicBitboards.GetBishopAttacks(to, occ) & (board.Bishops | board.Queens) & ourPieces;
            while (attackers != 0)
            {
                Square from = (Square)BitOperations.TrailingZeroCount(attackers);
                attackers &= attackers - 1;
                bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
                Move m = new Move(from, to, isCap ? MoveType.Capture : MoveType.Quiet);
                if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
                {
                    move = m;
                    return true;
                }
            }
        }

        // 3. Rook & Queen orthogonal checks
        ulong rookCheckSqs = MagicBitboards.GetRookAttacks(kSq, occ) & ~ourPieces;
        while (rookCheckSqs != 0)
        {
            Square to = (Square)BitOperations.TrailingZeroCount(rookCheckSqs);
            rookCheckSqs &= rookCheckSqs - 1;

            ulong attackers = MagicBitboards.GetRookAttacks(to, occ) & (board.Rooks | board.Queens) & ourPieces;
            while (attackers != 0)
            {
                Square from = (Square)BitOperations.TrailingZeroCount(attackers);
                attackers &= attackers - 1;
                bool isCap = (theirPieces & (1UL << (byte)to)) != 0;
                Move m = new Move(from, to, isCap ? MoveType.Capture : MoveType.Quiet);
                if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
                {
                    move = m;
                    return true;
                }
            }
        }

        // 4. Pawn checks
        ulong pawnCheckSqs = (them == Color.White ? Bitboard.BlackPawnAttacks[(byte)kSq] : Bitboard.WhitePawnAttacks[(byte)kSq]) & ~ourPieces;
        while (pawnCheckSqs != 0)
        {
            Square to = (Square)BitOperations.TrailingZeroCount(pawnCheckSqs);
            pawnCheckSqs &= pawnCheckSqs - 1;
            Square fromPush = us == Color.White ? (Square)((int)to - 8) : (Square)((int)to + 8);
            if ((int)fromPush >= 0 && (int)fromPush < 64 && ((board.Pawns & ourPieces) & (1UL << (byte)fromPush)) != 0 && (occ & (1UL << (byte)to)) == 0)
            {
                Move m = new Move(fromPush, to, (int)to / 8 == 7 || (int)to / 8 == 0 ? MoveType.QueenPromotion : MoveType.Quiet);
                if (board.IsLegal(in m) && StaticExchangeEvaluator.Evaluate(in board, m) >= 0)
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
    /// Evades an attacked pawn by capturing an enemy piece or pushing forward.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryEvadePawn(in Board board, Square pawnSq, out Move move)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();
        ulong occ = board.Occupied;
        ulong theirPieces = them == Color.White ? board.White : board.Black;
        int from = (int)pawnSq;
        int file = from % 8;

        if (us == Color.White)
        {
            if (file > 0)
            {
                Square toCapLeft = (Square)(from + 7);
                if ((theirPieces & (1UL << (byte)toCapLeft)) != 0)
                {
                    Move m = new Move(pawnSq, toCapLeft, (int)toCapLeft / 8 == 7 ? MoveType.QueenPromotionCapture : MoveType.Capture);
                    if (board.IsLegal(in m)) { move = m; return true; }
                }
            }
            if (file < 7)
            {
                Square toCapRight = (Square)(from + 9);
                if ((theirPieces & (1UL << (byte)toCapRight)) != 0)
                {
                    Move m = new Move(pawnSq, toCapRight, (int)toCapRight / 8 == 7 ? MoveType.QueenPromotionCapture : MoveType.Capture);
                    if (board.IsLegal(in m)) { move = m; return true; }
                }
            }
            Square toPush = (Square)(from + 8);
            if ((occ & (1UL << (byte)toPush)) == 0)
            {
                Move m = new Move(pawnSq, toPush, (int)toPush / 8 == 7 ? MoveType.QueenPromotion : MoveType.Quiet);
                if (board.IsLegal(in m)) { move = m; return true; }
            }
        }
        else
        {
            if (file > 0)
            {
                Square toCapLeft = (Square)(from - 9);
                if ((theirPieces & (1UL << (byte)toCapLeft)) != 0)
                {
                    Move m = new Move(pawnSq, toCapLeft, (int)toCapLeft / 8 == 0 ? MoveType.QueenPromotionCapture : MoveType.Capture);
                    if (board.IsLegal(in m)) { move = m; return true; }
                }
            }
            if (file < 7)
            {
                Square toCapRight = (Square)(from - 7);
                if ((theirPieces & (1UL << (byte)toCapRight)) != 0)
                {
                    Move m = new Move(pawnSq, toCapRight, (int)toCapRight / 8 == 0 ? MoveType.QueenPromotionCapture : MoveType.Capture);
                    if (board.IsLegal(in m)) { move = m; return true; }
                }
            }
            Square toPush = (Square)(from - 8);
            if ((occ & (1UL << (byte)toPush)) == 0)
            {
                Move m = new Move(pawnSq, toPush, (int)toPush / 8 == 0 ? MoveType.QueenPromotion : MoveType.Quiet);
                if (board.IsLegal(in m)) { move = m; return true; }
            }
        }

        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Finds a legal pawn push (single or double push).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetPawnPush(in Board board, out Move move)
    {
        Color us = board.SideToMove;
        ulong occ = board.Occupied;
        ulong ourPieces = us == Color.White ? board.White : board.Black;
        ulong pawns = board.Pawns & ourPieces;

        if (us == Color.White)
        {
            // Single pushes
            ulong singlePushes = (pawns << 8) & ~occ;
            ulong pushBits = singlePushes;
            while (pushBits != 0)
            {
                Square to = (Square)BitOperations.TrailingZeroCount(pushBits);
                pushBits &= pushBits - 1;
                Square from = (Square)((int)to - 8);
                Move m = new Move(from, to, (int)to / 8 == 7 ? MoveType.QueenPromotion : MoveType.Quiet);
                if (board.IsLegal(in m))
                {
                    move = m;
                    return true;
                }
            }

            // Double pushes: from Rank 2 through Rank 3 to Rank 4
            ulong singlePushesOnRank3 = ((pawns & 0x000000000000FF00UL) << 8) & ~occ;
            ulong doublePushes = (singlePushesOnRank3 << 8) & ~occ;
            while (doublePushes != 0)
            {
                Square to = (Square)BitOperations.TrailingZeroCount(doublePushes);
                doublePushes &= doublePushes - 1;
                Square from = (Square)((int)to - 16);
                Move m = new Move(from, to, MoveType.Quiet);
                if (board.IsLegal(in m))
                {
                    move = m;
                    return true;
                }
            }
        }
        else
        {
            // Black single pushes
            ulong singlePushes = (pawns >> 8) & ~occ;
            ulong pushBits = singlePushes;
            while (pushBits != 0)
            {
                Square to = (Square)BitOperations.TrailingZeroCount(pushBits);
                pushBits &= pushBits - 1;
                Square from = (Square)((int)to + 8);
                Move m = new Move(from, to, (int)to / 8 == 0 ? MoveType.QueenPromotion : MoveType.Quiet);
                if (board.IsLegal(in m))
                {
                    move = m;
                    return true;
                }
            }

            // Black double pushes: from Rank 7 through Rank 6 to Rank 5
            ulong singlePushesOnRank6 = ((pawns & 0x00FF000000000000UL) >> 8) & ~occ;
            ulong doublePushes = (singlePushesOnRank6 >> 8) & ~occ;
            while (doublePushes != 0)
            {
                Square to = (Square)BitOperations.TrailingZeroCount(doublePushes);
                doublePushes &= doublePushes - 1;
                Square from = (Square)((int)to + 16);
                Move m = new Move(from, to, MoveType.Quiet);
                if (board.IsLegal(in m))
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
