using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Quiescence Search (QS) to resolve tactical captures and promotions at leaf nodes.
/// Features:
/// - Fast stand-pat evaluation with correction history
/// - Delta pruning to eliminate futile captures
/// - SEE (Static Exchange Evaluation) filtering to prune losing captures
/// - MVV-LVA move ordering
/// - Transposition Table probing and recording
/// </summary>
[SkipLocalsInit]
public sealed class QuiescenceSearch
{
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable? _tt;
    private readonly CorrectionHistory? _correctionHistory;
    private readonly IIncrementalEvaluator? _incEval;
    private readonly Func<bool>? _isTimeUp;
    private readonly AlphaBetaEngine? _engine;
    private ulong _qsNodes;
    private bool _aborted;
    public ulong Nodes { get => _qsNodes; set => _qsNodes = value; }
    public bool IsAborted => _aborted;
    public SearchTraceWriter? SearchTrace { get; set; }

    public const int MaxQsDepth = 128;
    public const int DeltaMargin = 900;
    public int DeltaSafetyMargin { get; set; } = SearchHeuristics.TunedQsDeltaMargin;
    public bool EnableQsChecks { get; set; } = true;

    /// <summary>
    /// ID of the active NNUE expert network for TT isolation and tagging.
    /// </summary>
    public byte ActiveExpertId { get; set; } = 0;

    public QuiescenceSearch(
        IEvaluator evaluator,
        TranspositionTable? tt = null,
        CorrectionHistory? correctionHistory = null,
        IIncrementalEvaluator? incEval = null,
        Func<bool>? isTimeUp = null,
        AlphaBetaEngine? engine = null)
    {
        _evaluator = evaluator;
        _tt = tt;
        _correctionHistory = correctionHistory;
        _incEval = incEval ?? (evaluator as IIncrementalEvaluator);
        _isTimeUp = isTimeUp;
        _engine = engine;
    }

    /// <summary>
    /// Searches captures and promotions until a quiet position is reached.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Search(ref Board board, int alpha, int beta, int ply = 0, int qsDepth = 0, int knownInCheck = -1)
    {
        if (qsDepth == 0)
        {
            _aborted = _engine != null && _engine._stopped;
            if (_aborted)
            {
                return 0;
            }
            if (_engine == null && _isTimeUp != null && _isTimeUp())
            {
                _aborted = true;
                return 0;
            }
        }
        else if (_aborted || (_engine != null && _engine._stopped))
        {
            _aborted = true;
            return 0;
        }

        bool hasPriorOccurrence = false;
        if (board.HalfmoveClock >= 4)
        {
            bool isRep = board.IsRepetition(ply, out hasPriorOccurrence);
            if (qsDepth > 0 && (board.IsFiftyMoveRule() || isRep))
            {
                return 0;
            }
        }

        ulong key = board.ZobristKey;
        int originalAlpha = alpha;
        Move bestMove = Move.Null;

        // 1. Transposition Table probe
        TTEntry entry = default;
        bool ttHit = _tt != null && _tt.TryGet(key, out entry);
        Move ttMove = ttHit ? entry.BestMove : Move.Null;
        if (ttHit && !hasPriorOccurrence && entry.ExpertId == ActiveExpertId)
        {
            int ttScore = TTEntry.ScoreFromTt(entry.Score, ply);
            if (entry.Flags == TTFlags.Exact) return ttScore;
            if (entry.Flags == TTFlags.LowerBound && ttScore >= beta) return ttScore;
            if (entry.Flags == TTFlags.UpperBound && ttScore <= alpha) return ttScore;
        }

        bool inCheck = knownInCheck >= 0 ? knownInCheck != 0 : board.IsInCheck();

        // 2. Stand-pat evaluation (only when not in check)
        int rawEval = -Value.InfiniteValue;
        int standPat = -Value.InfiniteValue;
        if (!inCheck)
        {
            if (ttHit && entry.ExpertId == ActiveExpertId && entry.StaticEval != short.MinValue)
            {
                rawEval = entry.StaticEval;
            }
            else
            {
                rawEval = _evaluator.Evaluate(in board).Raw;
            }

            standPat = _correctionHistory != null
                ? _correctionHistory.GetAdjustedEval(board.SideToMove, board.PawnKey, rawEval)
                : rawEval;

            if (ttHit && entry.ExpertId == ActiveExpertId && entry.Flags == TTFlags.LowerBound)
            {
                int ttScore = TTEntry.ScoreFromTt(entry.Score, ply);
                if (ttScore > standPat)
                {
                    standPat = ttScore;
                }
            }

            if (standPat >= beta)
            {
                return standPat;
            }

            if (standPat > alpha)
            {
                alpha = standPat;
            }

            // 3. Big Delta Pruning:
            // If stand-pat + Queen (900) < alpha and no pawns are about to promote and opponent has no queen to capture,
            // even the greatest capture cannot bridge the gap.
            if (standPat + DeltaMargin < alpha)
            {
                Color us = board.SideToMove;
                Color them = us.Opponent();
                ulong promoRank = us == Color.White ? Bitboard.Rank7 : Bitboard.Rank2;
                ulong ourPawns = board.Pawns & (us == Color.White ? board.White : board.Black);
                bool hasPromotingPawn = (ourPawns & promoRank) != 0;
                ulong enemyQueens = board.Queens & (them == Color.White ? board.White : board.Black);

                if (!hasPromotingPawn && enemyQueens == 0)
                {
                    return alpha;
                }
            }
        }

        // Safety cutoff on maximum QS depth
        if (ply >= MaxQsDepth)
        {
            return inCheck ? 0 : standPat;
        }

        // 4. Generate legal moves
        Unsafe.SkipInit(out MoveList moves);
        moves.Clear();

        bool genAll = inCheck || (qsDepth == 0 && EnableQsChecks);
        if (genAll)
        {
            MoveGenerator.GenerateLegalMoves(in board, ref moves);
            if (moves.Count == 0)
            {
                // If in check and no moves exist, it is checkmate; if not in check, stalemate
                return inCheck ? -(Value.MateValue - ply) : 0;
            }
        }
        else
        {
            MoveGenerator.GenerateLegalCaptures(in board, ref moves);
        }

        // 5. Filter and score candidate moves into compact arrays
        Unsafe.SkipInit(out IndexBuffer128 candidateIndices);
        Unsafe.SkipInit(out ScoreBuffer candidateScores);
        int candidateCount = 0;

        // Precompute direct-check target masks and discovered-check blocker mask once for QS root quiet-check filtering
        ulong checkPawn = 0UL, checkKnight = 0UL, checkBishop = 0UL, checkRook = 0UL, dcCandidateMask = 0UL;
        bool checkMasksReady = false;
        if (!inCheck && EnableQsChecks && qsDepth == 0)
        {
            Color us = board.SideToMove;
            Color them = us.Opponent();
            ulong enemyKingBb = board.Kings & (them == Color.White ? board.White : board.Black);
            if (enemyKingBb != 0)
            {
                Square ekSq = Bitboard.Lsb(enemyKingBb);
                int ek = (byte)ekSq;
                ulong occ = board.Occupied;
                ulong usPieces = us == Color.White ? board.White : board.Black;

                checkPawn = us == Color.White ? Bitboard.BlackPawnAttacks[ek] : Bitboard.WhitePawnAttacks[ek];
                checkKnight = Bitboard.KnightAttacks[ek];
                checkBishop = MagicBitboards.GetBishopAttacks(ekSq, occ);
                checkRook = MagicBitboards.GetRookAttacks(ekSq, occ);

                if ((usPieces & (board.Bishops | board.Queens) & Bitboard.GetBishopRays(ekSq)) != 0)
                {
                    dcCandidateMask |= checkBishop;
                }
                if ((usPieces & (board.Rooks | board.Queens) & Bitboard.GetRookRays(ekSq)) != 0)
                {
                    dcCandidateMask |= checkRook;
                }
                checkMasksReady = true;
            }
        }

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            int moveScore;

            if (!inCheck)
            {
                if (!m.IsCapture && !m.IsPromotion)
                {
                    // Checks in Quiescence Search: only at QS root (qsDepth == 0) with non-negative SEE
                    if (!EnableQsChecks || qsDepth > 0) continue;

                    if (checkMasksReady && !m.IsCastling)
                    {
                        ulong toBit = 1UL << (byte)m.To;
                        PieceType pt = board.GetPieceAt(m.From).Type();
                        bool directCheck = pt switch
                        {
                            PieceType.Pawn => (checkPawn & toBit) != 0,
                            PieceType.Knight => (checkKnight & toBit) != 0,
                            PieceType.Bishop => (checkBishop & toBit) != 0,
                            PieceType.Rook => (checkRook & toBit) != 0,
                            PieceType.Queen => ((checkBishop | checkRook) & toBit) != 0,
                            _ => false
                        };
                        if (!directCheck)
                        {
                            if ((dcCandidateMask & (1UL << (byte)m.From)) == 0 || !GivesCheck(ref board, m))
                            {
                                continue;
                            }
                        }
                    }
                    else if (!GivesCheck(ref board, m))
                    {
                        continue;
                    }

                    int see = StaticExchangeEvaluator.Evaluate(in board, m);
                    if (see < 0) continue;

                    moveScore = 1000 + see;
                }
                else
                {
                    Piece moved = board.GetPieceAt(m.From);
                    Piece captured = m.Type == MoveType.EnPassant
                        ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
                        : board.GetPieceAt(m.To);

                    // Move-level Delta Pruning
                    if (!m.IsPromotion)
                    {
                        int victimVal = captured != Piece.None
                            ? StaticExchangeEvaluator.GetPieceValue(captured.Type())
                            : 0;

                        if (standPat + victimVal + DeltaSafetyMargin < alpha)
                        {
                            continue;
                        }
                    }

                    // SEE pruning: discard losing captures in quiescence
                    int see = StaticExchangeEvaluator.Evaluate(in board, m);
                    if (see < 0) continue;

                    moveScore = MovePicker.GetMvvLva(moved, captured, m) + see;
                }
            }
            else
            {
                // In check: search all legal evasions, prioritizing captures
                moveScore = m.IsCapture ? MovePicker.GetMvvLva(in board, m) + 10000 : 0;
            }

            if (m == ttMove)
            {
                moveScore = 1_000_000;
            }

            candidateIndices[candidateCount] = (byte)i;
            candidateScores[candidateCount] = moveScore;
            candidateCount++;
        }

        if (candidateCount == 0)
        {
            return standPat != -Value.InfiniteValue ? standPat : alpha;
        }

        // 6. Search candidates in MVV-LVA order using compact selection sort
        for (int step = 0; step < candidateCount; step++)
        {
            int bestSlot = -1;
            int bestScore = int.MinValue;

            for (int i = 0; i < candidateCount; i++)
            {
                int sc = candidateScores[i];
                if (sc > bestScore)
                {
                    bestScore = sc;
                    bestSlot = i;
                }
            }

            if (bestSlot < 0 || bestScore == int.MinValue) break;

            ulong checkMask = _engine != null ? _engine.TimeManager.CheckMask : 511UL;
            if (((++_qsNodes) & checkMask) == 0 && _isTimeUp != null && _isTimeUp())
            {
                _aborted = true;
                return 0;
            }

            Move move = moves[candidateIndices[bestSlot]];
            candidateScores[bestSlot] = int.MinValue; // Mark as picked

            Piece movedPiece = board.GetPieceAt(move.From);
            board.MakeMove(move, out BoardState state, movedPiece);
            SearchTrace?.WriteEdge(key, board.ZobristKey, move);
            _tt?.Prefetch(board.ZobristKey);
            _incEval?.PushMove(move, movedPiece, state.CapturedPiece, in board);

            int score = -Search(ref board, -beta, -alpha, ply + 1, qsDepth + 1);

            board.UnmakeMove(move, in state, movedPiece);
            _incEval?.RollbackTo(ply);

            if (_aborted || (_engine != null && _engine._stopped))
            {
                _aborted = true;
                return 0;
            }

            if (score >= beta)
            {
                short evalShort = TTEntry.ClampEval(rawEval);
                _tt?.Store(key, move, 0, TTEntry.ScoreToTt(beta, ply), _tt.CurrentAge, TTFlags.LowerBound, ActiveExpertId, evalShort);
                return beta;
            }

            if (score > alpha)
            {
                alpha = score;
                bestMove = move;
            }
        }

        TTFlags flag = (alpha > originalAlpha) ? TTFlags.Exact : TTFlags.UpperBound;
        short finalEvalShort = TTEntry.ClampEval(rawEval);
        _tt?.Store(key, bestMove, 0, TTEntry.ScoreToTt(alpha, ply), _tt?.CurrentAge ?? 1, flag, ActiveExpertId, finalEvalShort);
        return alpha;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GivesCheck(ref Board board, Move m) => board.GivesCheck(m);
}
