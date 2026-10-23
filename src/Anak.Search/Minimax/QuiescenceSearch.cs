using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Quiescence Search (QS) to resolve tactical captures and promotions at leaf nodes.
/// Features:
/// - Fast stand-pat score evaluation using IMultiTierEvaluator.EvaluateMicro pre-screening
/// - Delta pruning to eliminate futile captures
/// - SEE (Static Exchange Evaluation) filtering to prune losing captures
/// - MVV-LVA move ordering
/// - Transposition Table probing and recording
/// </summary>
public sealed class QuiescenceSearch
{
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable? _tt;
    private readonly CorrectionHistory? _correctionHistory;
    private readonly IIncrementalEvaluator? _incEval;

    public const int MaxQsDepth = 128;
    public const int DeltaMargin = 900;
    public int DeltaSafetyMargin { get; set; } = SearchHeuristics.TunedQsDeltaMargin;

    public QuiescenceSearch(IEvaluator evaluator, TranspositionTable? tt = null, CorrectionHistory? correctionHistory = null, IIncrementalEvaluator? incEval = null)
    {
        _evaluator = evaluator;
        _tt = tt;
        _correctionHistory = correctionHistory;
        _incEval = incEval ?? (evaluator as IIncrementalEvaluator);
    }

    /// <summary>
    /// Searches captures and promotions until a quiet position is reached.
    /// </summary>
    public int Search(ref Board board, int alpha, int beta, int ply = 0)
    {
        ulong key = board.ZobristKey;
        int originalAlpha = alpha;
        Move bestMove = Move.Null;

        // 1. Transposition Table probe
        if (_tt != null && _tt.TryGet(key, out TTEntry entry))
        {
            int ttScore = TTEntry.ScoreFromTt(entry.Score, ply);
            if (entry.Flags == TTFlags.Exact) return ttScore;
            if (entry.Flags == TTFlags.LowerBound && ttScore >= beta) return ttScore;
            if (entry.Flags == TTFlags.UpperBound && ttScore <= alpha) return ttScore;
        }

        bool inCheck = board.IsInCheck();

        // 2. Stand-pat evaluation (only when not in check)
        int standPat = -Value.InfiniteValue;
        if (!inCheck)
        {
            // Full evaluation (fast incremental evaluation via IIncrementalEvaluator)
            standPat = _evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty).Raw;
            if (_correctionHistory != null)
            {
                standPat = _correctionHistory.GetAdjustedEval(in board, standPat);
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
        MoveList moves = new MoveList();
        MoveGenerator.GenerateLegalMoves(in board, ref moves);

        if (moves.Count == 0)
        {
            // If in check and no moves exist, it is checkmate
            return inCheck ? -(Value.MateValue - ply) : 0;
        }

        // 5. Filter and score candidate moves
        ScoreBuffer scores = default;
        for (int i = 0; i < moves.Count; i++)
        {
            scores[i] = int.MinValue;
        }
        int candidateCount = 0;

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];

            if (!inCheck)
            {
                // Only search captures and promotions in QS
                if (!m.IsCapture && !m.IsPromotion) continue;

                // Move-level Delta Pruning
                if (!m.IsPromotion)
                {
                    Piece captured = m.Type == MoveType.EnPassant
                        ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
                        : board.GetPieceAt(m.To);
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

                scores[i] = MovePicker.GetMvvLva(in board, m) + see;
            }
            else
            {
                // In check: search all legal evasions, prioritizing captures
                scores[i] = m.IsCapture ? MovePicker.GetMvvLva(in board, m) + 10000 : 0;
            }

            candidateCount++;
        }

        if (candidateCount == 0)
        {
            return standPat != -Value.InfiniteValue ? standPat : alpha;
        }

        // 6. Search candidates in MVV-LVA order using selection sort
        for (int step = 0; step < moves.Count; step++)
        {
            int bestIdx = -1;
            int bestScore = int.MinValue;

            for (int i = 0; i < moves.Count; i++)
            {
                if (scores[i] == int.MinValue) continue;
                if (!inCheck && !moves[i].IsCapture && !moves[i].IsPromotion) continue;

                if (scores[i] > bestScore)
                {
                    bestScore = scores[i];
                    bestIdx = i;
                }
            }

            if (bestIdx < 0 || bestScore == int.MinValue) break;

            Move move = moves[bestIdx];
            scores[bestIdx] = int.MinValue; // Mark as picked

            Piece movedPiece = board.GetPieceAt(move.From);
            board.MakeMove(move, out BoardState state);
            _incEval?.PushMove(move, movedPiece, state.CapturedPiece, in board);

            int score = -Search(ref board, -beta, -alpha, ply + 1);

            board.UnmakeMove(move, in state);
            _incEval?.RollbackTo(ply);

            if (score >= beta)
            {
                _tt?.Store(key, move, 0, TTEntry.ScoreToTt(beta, ply), _tt.CurrentAge, TTFlags.LowerBound);
                return beta;
            }

            if (score > alpha)
            {
                alpha = score;
                bestMove = move;
            }
        }

        TTFlags flag = (alpha > originalAlpha) ? TTFlags.Exact : TTFlags.UpperBound;
        _tt?.Store(key, bestMove, 0, TTEntry.ScoreToTt(alpha, ply), _tt?.CurrentAge ?? 1, flag);
        return alpha;
    }
}
