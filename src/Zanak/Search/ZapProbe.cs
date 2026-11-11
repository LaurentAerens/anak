using System.Runtime.CompilerServices;
using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// Linear trajectory tracer executing zero-branching greedy descents down to target base depth.
/// Incorporates immediate recapture tracking and early spine abandonment for ruined trajectories.
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
        int searchPly = 1)
    {
        trajectory.Clear();
        ZapParams zapParams = parameters ?? ZapParams.Default;

        // Terminal draw check before making any probe moves
        if ((board.HalfmoveClock >= 4 && (board.IsFiftyMoveRule() || board.IsThreefoldRepetition() || board.IsRepetition(searchPly))) ||
            board.HasInsufficientMaterial())
        {
            return 0;
        }

        // Initial evaluation at the start of the probe
        int initialRaw = (int)evaluator.Evaluate(in board);
        int prevEngineEval = board.SideToMove == engineColor ? initialRaw : -initialRaw;
        int probeStartEngineEval = prevEngineEval;

        int pliesTraced = 0;
        Move lastMove = initialLastMove;
        bool isTablebaseHit = false;
        bool isSpineAbandoned = false;

        while (pliesTraced < targetDepth)
        {
            // Early branch abort check: if shared state signals stop or root candidate was refuted/dead, exit immediately
            if (sharedState != null && (pliesTraced & 3) == 0)
            {
                if (sharedState.IsStopped || (rootCandidateIndex >= 0 && sharedState.IsRootMoveRefutedOrDead(rootCandidateIndex)))
                {
                    break;
                }
            }

            Color moverColor = board.SideToMove;
            ulong zobrist = board.ZobristKey;

            // Select greedy move using ZapPolicy, passing lastMove for immediate recaptures
            if (!policy.TryGetQuickMove(in board, pliesTraced, lastMove, out Move move) || move.IsNull)
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
            if (!board.MakeMove(move, out BoardState state))
            {
                break;
            }

            pliesTraced++;

            // Terminal Repetition and Draw Detection along probe
            if (board.HalfmoveClock >= 4 && (board.IsFiftyMoveRule() || board.IsRepetition(searchPly + pliesTraced) || board.IsThreefoldRepetition()))
            {
                return 0;
            }

            if (board.HasInsufficientMaterial())
            {
                return 0;
            }

            // Evaluate board post-move:
            // Check Syzygy Tablebase Ground Truth if piece count <= MaxPieces and no castling
            int currentEngineEval;
            if (tablebase != null &&
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
                staticEval: (short)Math.Clamp(currentEngineEval, short.MinValue + 1, short.MaxValue - 1),
                evalDelta: (short)Math.Clamp(delta, short.MinValue + 1, short.MaxValue - 1)
            ));

            prevEngineEval = currentEngineEval;
            lastMove = move;

            // In-Spine Tablebase Terminal Cutoff:
            // If the position reached mathematical ground truth (WDL), stop tracing immediately!
            if (isTablebaseHit)
            {
                break;
            }

            // Early Spine Abandonment:
            // 1. Catastrophic eval collapse near the root (e.g. blundered material along root reply)
            // Only prune near the root (pliesTraced <= 4) where root move blunders occur.
            // Deep down the spine, tracing to the horizon is computationally negligible (< 1 microsecond)
            // and avoiding mid-spine truncation prevents horizon distortion during tactical exchanges.
            if (pliesTraced <= 4 && currentEngineEval - probeStartEngineEval <= -zapParams.SpineAbandonThresholdCp)
            {
                // If the opponent just captured and we have an immediate legal recapture with SEE >= 0 on that square,
                // do not abandon yet - resolve the exchange first!
                bool hasPendingRecapture = moverColor != engineColor &&
                    lastMove.IsCapture &&
                    ZapPolicy.TryGetLvaRecapture(in board, lastMove, out Move pendingRecap) &&
                    !pendingRecap.IsNull;

                if (!hasPendingRecapture)
                {
                    isSpineAbandoned = true;
                    break;
                }
            }

            // 2. Relative Dead Line Truncation:
            // On engine plies (pliesTraced >= 4), if position collapses >= 300 cp below the leader, truncate the doomed spine
            if (moverColor == engineColor && pliesTraced >= 4 && leaderScore > -20000 && currentEngineEval <= leaderScore - 300)
            {
                isSpineAbandoned = true;
                break;
            }
        }

        // Quiescence Resolution: resolve active exchanges at probe horizon to avoid horizon effect
        // (Skipped if spine already truncated upon reaching tablebase ground truth or early abandonment)
        if (!isTablebaseHit && !isSpineAbandoned)
        {
            int qPlies = 0;
            int maxQPlies = zapParams.QuiescenceMaxPlies;
            while (qPlies < maxQPlies)
            {
                Move qMove = Move.Null;
                // If the last move was a capture, resolve the immediate recapture first (including equal trades like Queen for Queen)!
                if (!ZapPolicy.TryGetLvaRecapture(in board, lastMove, out qMove) || qMove.IsNull)
                {
                    if (!policy.TryGetWinningCapture(in board, lastMove, out qMove) || qMove.IsNull)
                    {
                        break;
                    }
                }

                Color moverColor = board.SideToMove;
                ulong zobrist = board.ZobristKey;

                if (!board.MakeMove(qMove, out BoardState qState))
                {
                    break;
                }

                qPlies++;

                if (board.HalfmoveClock >= 4 && (board.IsFiftyMoveRule() || board.IsRepetition(searchPly + pliesTraced + qPlies) || board.IsThreefoldRepetition()))
                {
                    return 0;
                }

                if (board.HasInsufficientMaterial())
                {
                    return 0;
                }

                int postRaw = (int)evaluator.Evaluate(in board);
                int currentEngineEval = board.SideToMove == engineColor ? postRaw : -postRaw;
                int delta = currentEngineEval - prevEngineEval;

                trajectory.Add(new ZapTrajectoryStep(
                    state: qState,
                    zobristKey: zobrist,
                    move: qMove,
                    sideToMove: moverColor,
                    staticEval: (short)Math.Clamp(currentEngineEval, short.MinValue + 1, short.MaxValue - 1),
                    evalDelta: (short)Math.Clamp(delta, short.MinValue + 1, short.MaxValue - 1)
                ));

                prevEngineEval = currentEngineEval;
                lastMove = qMove;
            }
        }

        return prevEngineEval;
    }
}
