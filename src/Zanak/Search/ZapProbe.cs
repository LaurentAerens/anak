using System.Runtime.CompilerServices;
using Anak.Core;
using Anak.Data.Nnue;
using SearchTraceWriter = Anak.Search.SearchTraceWriter;

namespace Zanak.Search;

/// <summary>
/// Linear trajectory tracer executing zero-branching greedy descents down to target base depth.
/// Incorporates immediate recapture tracking. Spines are never abandoned early; agents prune them later.
/// </summary>
public static class ZapProbe
{
    /// <summary>
    /// Traces a single linear greedy trajectory from the given scratch board.
    /// Records steps and eval deltas into trajectory buffer, and returns the terminal score from the engine's perspective.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Trace(
        ref Board board,
        int targetDepth,
        ZapPolicy policy,
        IEvaluator evaluator,
        Color engineColor,
        ref ZapTrajectory trajectory,
        Move initialLastMove = default,
        ZapParams? parameters = null,
        int leaderScore = int.MinValue,
        ZapSharedState? sharedState = null,
        int rootCandidateIndex = -1,
        ITablebaseProber? tablebase = null,
        int searchPly = 1,
        ZapTranspositionTable? tt = null,
        int maxPlies = ZapTrajectory.MaxCapacity,
        SearchTraceWriter? searchTrace = null)
    {
        trajectory.Clear();
        ZapParams zapParams = parameters ?? ZapParams.Default;

        // Terminal draw check before making any probe moves
        if ((board.HalfmoveClock >= 4 && (board.IsFiftyMoveRule() || board.IsThreefoldRepetition() || board.IsRepetition(searchPly))) ||
            board.HasInsufficientMaterial())
        {
            return 0;
        }

        // Incremental NNUE: seed the accumulator stack at the probe start, then push every move played.
        // (No-op if the evaluator is not the multi-tier NNUE or no root expert has been fixed.)
        ZanakNnueEvaluator? nnue = evaluator as ZanakNnueEvaluator;
        IIncrementalEvaluator? incremental = evaluator as IIncrementalEvaluator;
        if (nnue != null) nnue.ResetAccumulatorStack(in board);
        else incremental?.InitSearch(in board);

        // Initial evaluation at the start of the probe
        int initialRaw = (int)evaluator.Evaluate(in board);
        int prevEngineEval = board.SideToMove == engineColor ? initialRaw : -initialRaw;
        int probeStartEngineEval = prevEngineEval;

        int pliesTraced = 0;
        Move lastMove = initialLastMove;
        bool isTablebaseHit = false;

        while (pliesTraced < targetDepth && trajectory.Count < maxPlies)
        {
            // Early abort only when the search stops; demoted or refuted spines are still traced in full.
            if (sharedState != null && (pliesTraced & 3) == 0)
            {
                if (sharedState.IsStopped)
                {
                    break;
                }
            }

            Color moverColor = board.SideToMove;
            ulong zobrist = board.ZobristKey;

            Move move = Move.Null;
            // Check Transposition Table first: if a verified refutation move exists, use it!
            if (tt != null && tt.TryGet(zobrist, out var ttEntry) && ttEntry.Flags >= 2 && !ttEntry.BestMove.IsNull && board.IsLegal(ttEntry.BestMove))
            {
                move = ttEntry.BestMove;
            }
            // Otherwise, select greedy move using ZapPolicy, passing lastMove for immediate recaptures
            else if (!policy.TryGetQuickMove(in board, pliesTraced, lastMove, out move) || move.IsNull)
            {
                // Terminal position (checkmate or stalemate)
                if (board.IsInCheck())
                {
                    // Checkmate: loss for the side to move
                    // If moverColor == engineColor, engine got checkmated (-30000 + pliesTraced).
                    // If moverColor != engineColor, opponent got checkmated in a greedy line:
                    // Cap at decisive win (e.g. +2000 cp) rather than +30000 to prevent false UCI mate reports
                    // and avoid corrupting root candidate pruning before workers verify the line!
                    int mateScore = moverColor == engineColor
                        ? -30000 + pliesTraced
                        : Math.Min(2500, 1500 + Math.Max(0, 32 - pliesTraced) * 10);
                    return mateScore;
                }
                // Stalemate
                return 0;
            }

            // Apply move
            Piece movedPiece = board.GetPieceAt(move.From);
            if (!board.MakeMove(move, out BoardState state, movedPiece))
            {
                break;
            }
            searchTrace?.WriteEdge(zobrist, board.ZobristKey, move);
            incremental?.PushMove(move, movedPiece, state.CapturedPiece, in board);

            pliesTraced++;

            // Terminal Repetition and Draw Detection along probe:
            // Treat 50-move, 3-fold repetition, and insufficient material as 0 draws.
            // A 2-fold repetition on the heuristic spine stops the rollout at static eval rather than forcing 0.
            bool isDraw = (board.HalfmoveClock >= 4 &&
                (board.IsFiftyMoveRule() || board.IsThreefoldRepetition())) ||
                board.HasInsufficientMaterial();
            bool isTwofoldLoop = !isDraw && board.HalfmoveClock >= 4 && board.IsRepetition(searchPly + pliesTraced);

            // Evaluate board post-move:
            // Check Syzygy Tablebase Ground Truth if piece count <= MaxPieces and no castling
            int currentEngineEval;
            if (isDraw)
            {
                currentEngineEval = 0;
            }
            else if (tablebase != null &&
                board.Castling == CastlingRights.None &&
                System.Numerics.BitOperations.PopCount(board.Occupied) <= tablebase.MaxPieces &&
                tablebase.TryProbeWdl(in board, out int wdl))
            {
                isTablebaseHit = true;
                int moverWdlScore = wdl switch
                {
                    2 => 29000 - pliesTraced,
                    1 => 100,
                    0 => 0,
                    -1 => -100,
                    -2 => -29000 + pliesTraced,
                    _ => 0
                };
                currentEngineEval = board.SideToMove == engineColor ? moverWdlScore : -moverWdlScore;
            }
            else
            {
                int postRaw = (int)evaluator.Evaluate(in board);
                currentEngineEval = board.SideToMove == engineColor ? postRaw : -postRaw;
            }

            // EvalDelta: change in evaluation from the perspective of the engine
            int delta = currentEngineEval - prevEngineEval;

            // Record trajectory step
            trajectory.Add(new ZapTrajectoryStep(
                state: state,
                zobristKey: zobrist,
                move: move,
                sideToMove: moverColor,
                parentEval: (short)Math.Clamp(prevEngineEval, short.MinValue + 1, short.MaxValue - 1),
                staticEval: (short)Math.Clamp(currentEngineEval, short.MinValue + 1, short.MaxValue - 1),
                evalDelta: (short)Math.Clamp(delta, short.MinValue + 1, short.MaxValue - 1)
            ));
            if (isDraw) return 0;
            if (isTwofoldLoop)
            {
                prevEngineEval = currentEngineEval;
                break;
            }

            if (tt != null)
            {
                // Store greedy spine step in TT if not already verified by an agent
                if (!tt.TryGet(zobrist, out var existing) || existing.Flags < 2)
                {
                    tt.Store(zobrist, move, (byte)Math.Clamp(targetDepth - pliesTraced, 1, 255), (short)Math.Clamp(currentEngineEval, short.MinValue + 1, short.MaxValue - 1), flags: 1);
                }
            }

            prevEngineEval = currentEngineEval;
            lastMove = move;

            // In-Spine Tablebase Terminal Cutoff:
            // If the position reached mathematical ground truth (WDL), stop tracing immediately!
            if (isTablebaseHit)
            {
                break;
            }

            // NOTE: Spines are never abandoned by the probe. Weak lines are only discarded later by the agents
            // (Refutation / Promotion squads), so every spine always reaches its full horizon.
        }

        // Quiescence Resolution: resolve active exchanges at probe horizon to avoid horizon effect
        // (Skipped if spine already truncated upon reaching tablebase ground truth)
        if (!isTablebaseHit)
        {
            int qPlies = 0;
            int maxQPlies = zapParams.QuiescenceMaxPlies;
            while (qPlies < maxQPlies && trajectory.Count < maxPlies)
            {
                Move qMove = Move.Null;
                // If the last move was a capture, resolve the immediate recapture first (including equal trades like Queen for Queen)!
                if (board.IsInCheck())
                {
                    if (!policy.TryGetQuickMove(in board, pliesTraced + qPlies, lastMove, out qMove) || qMove.IsNull) break;
                }
                else if (!ZapPolicy.TryGetLvaRecapture(in board, lastMove, out qMove) || qMove.IsNull)
                {
                    if (!policy.TryGetWinningCapture(in board, lastMove, out qMove) || qMove.IsNull)
                    {
                        break;
                    }
                }

                Color moverColor = board.SideToMove;
                ulong zobrist = board.ZobristKey;

                Piece qMovedPiece = board.GetPieceAt(qMove.From);
                if (!board.MakeMove(qMove, out BoardState qState, qMovedPiece))
                {
                    break;
                }
                searchTrace?.WriteEdge(zobrist, board.ZobristKey, qMove);
                incremental?.PushMove(qMove, qMovedPiece, qState.CapturedPiece, in board);

                qPlies++;

                bool isDraw = (board.HalfmoveClock >= 4 &&
                    (board.IsFiftyMoveRule() || board.IsRepetition(searchPly + pliesTraced + qPlies) || board.IsThreefoldRepetition())) ||
                    board.HasInsufficientMaterial();

                int postRaw = isDraw ? 0 : (int)evaluator.Evaluate(in board);
                int currentEngineEval = board.SideToMove == engineColor ? postRaw : -postRaw;
                int delta = currentEngineEval - prevEngineEval;

                trajectory.Add(new ZapTrajectoryStep(
                    state: qState,
                    zobristKey: zobrist,
                    move: qMove,
                    sideToMove: moverColor,
                    parentEval: (short)Math.Clamp(prevEngineEval, short.MinValue + 1, short.MaxValue - 1),
                    staticEval: (short)Math.Clamp(currentEngineEval, short.MinValue + 1, short.MaxValue - 1),
                    evalDelta: (short)Math.Clamp(delta, short.MinValue + 1, short.MaxValue - 1)
                ));
                if (isDraw) return 0;

                prevEngineEval = currentEngineEval;
                lastMove = qMove;
            }
        }

        return prevEngineEval;
    }
}
