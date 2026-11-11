using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Data.Nnue;
using Zanak.Time;

namespace Zanak.Search;

/// <summary>
/// Telemetry record emitted during iterative ZAP search.
/// </summary>
public readonly record struct ZapSearchStats(
    int Depth,
    int ScoreCp,
    long Nodes,
    long ElapsedMs,
    long Nps,
    Move BestMove,
    Move[] Pv
);

/// <summary>
/// Orchestrator and controller for ZAP (Zero-branching Accelerated Probe) search architecture.
/// Combines ultra-fast anytime multi-spine seeding with multi-threaded retrospective discrepancy branching.
/// </summary>
public sealed class ZapSearch : IDisposable
{
    private readonly ZapParams _params;
    private readonly TimeManager _timeManager;
    private readonly ZapSharedState _sharedState = new();
    private readonly ZapWorkQueue _workQueue = new();
    private readonly ZapTranspositionTable _tt;
    private readonly ZapWorkerPool _workerPool;
    private readonly IEvaluator _evaluator;
    private readonly ITablebaseProber? _tablebase;

    public ZapParams Parameters => _params;
    public TimeManager TimeManager => _timeManager;
    public ZapTranspositionTable TranspositionTable => _tt;
    public ZapSharedState SharedState => _sharedState;
    public ZapWorkQueue WorkQueue => _workQueue;
    public ITablebaseProber? Tablebase => _tablebase;

    public ZapSearch(ZapParams? parameters = null, TimeManager? timeManager = null, IEvaluator? evaluator = null, ITablebaseProber? tablebase = null)
    {
        _params = parameters ?? ZapParams.Default;
        _timeManager = timeManager ?? new TimeManager();
        _tt = new ZapTranspositionTable(_params.DefaultHashMb);

        _evaluator = evaluator ?? CreateDefaultEvaluator();
        _tablebase = tablebase ?? CreateDefaultTablebase();
        _workerPool = new ZapWorkerPool(
            _params.DefaultThreads,
            _workQueue,
            _sharedState,
            _params,
            _evaluator,
            _tt,
            _tablebase);
    }

    private static ITablebaseProber? CreateDefaultTablebase()
    {
        try
        {
            var tb = new Anak.Data.Syzygy.SyzygyTablebase();
            if (tb.MaxPieces > 0) return tb;
        }
        catch
        {
        }
        return null;
    }

    public void Clear()
    {
        _tt.Clear();
        _sharedState.Reset();
        _workQueue.Clear();
    }

    private static IEvaluator CreateDefaultEvaluator()
    {
        try
        {
            var multiTier = new MultiTierNnueEvaluator();
            if (multiTier.IsActive) return multiTier;
        }
        catch
        {
            // Fall back to heuristic evaluator if NNUE files cannot be loaded
        }
        return new DefaultEvaluator();
    }

    /// <summary>
    /// Executes anytime ZAP search on the given position within the provided budget.
    /// </summary>
    public Move Search(
        in Board rootBoard,
        in SearchBudget budget,
        Action<ZapSearchStats>? infoCallback = null,
        CancellationToken cancellationToken = default,
        Move lastMove = default)
    {
        // 1. Root move generation
        MoveList legalMoves = default;
        MoveGenerator.GenerateLegalMoves(in rootBoard, ref legalMoves);

        if (legalMoves.Count == 0)
        {
            return Move.Null;
        }

        // Fast-path: only 1 legal move -> return immediately
        if (legalMoves.Count == 1)
        {
            return legalMoves[0];
        }

        // Level 1: Root Syzygy Tablebase Ground-Truth (Instant DTZ Resolution)
        if (_tablebase != null &&
            rootBoard.Castling == CastlingRights.None &&
            System.Numerics.BitOperations.PopCount(rootBoard.Occupied) <= _tablebase.MaxPieces)
        {
            if (_tablebase.TryProbeDtz(in rootBoard, out Move tbMove, out int dtz) && !tbMove.IsNull)
            {
                if (rootBoard.IsLegal(in tbMove))
                {
                    int tbScore = dtz > 0 ? 30000 - dtz : (dtz < 0 ? -30000 - dtz : 0);
                    _tt.Store(rootBoard.ZobristKey, tbMove, 255, (short)Math.Clamp(tbScore, short.MinValue + 1, short.MaxValue - 1));
                    _sharedState.Reset([tbMove]);
                    _sharedState.UpdateCandidateBaseline(0, tbScore, Move.Null);
                    EmitInfo(infoCallback, 255);
                    return tbMove;
                }
            }
        }

        // Level 1.5: Root Draw Verification (Threefold Repetition, 50-Move Rule, Insufficient Material)
        if ((rootBoard.HalfmoveClock >= 4 && (rootBoard.IsFiftyMoveRule() || rootBoard.IsThreefoldRepetition())) ||
            rootBoard.HasInsufficientMaterial())
        {
            _tt.Store(rootBoard.ZobristKey, legalMoves[0], 255, 0);
            _sharedState.Reset(legalMoves.AsSpan().ToArray());
            _sharedState.UpdateCandidateBaseline(0, 0, Move.Null);
            EmitInfo(infoCallback, 1);
            return legalMoves[0];
        }

        _timeManager.Start();
        _tt.NewSearch();

        Color engineColor = rootBoard.SideToMove;
        var rootPolicy = new ZapPolicy(_params, _tt);

        // Pre-order root moves: Recaptures and Winning Captures first (Queen > Rook > Minor > Pawn), then Quiets
        Span<Move> orderedMoves = stackalloc Move[legalMoves.Count];
        Span<int> moveScores = stackalloc int[legalMoves.Count];

        for (int i = 0; i < legalMoves.Count; i++)
        {
            Move m = legalMoves[i];
            orderedMoves[i] = m;
            moveScores[i] = rootPolicy.ScoreMove(in rootBoard, m, 0, lastMove);
        }

        for (int i = 0; i < legalMoves.Count - 1; i++)
        {
            for (int j = i + 1; j < legalMoves.Count; j++)
            {
                if (moveScores[j] > moveScores[i])
                {
                    (moveScores[i], moveScores[j]) = (moveScores[j], moveScores[i]);
                    (orderedMoves[i], orderedMoves[j]) = (orderedMoves[j], orderedMoves[i]);
                }
            }
        }

        Move[] movesArray = orderedMoves.ToArray();
        _sharedState.Reset(movesArray);
        uint generation = _sharedState.NextGeneration();

        // -------------------------------------------------------------
        // Phase 1: Root Multi-Spine Seeding (The Anytime Guarantee)
        // -------------------------------------------------------------
        int baseDepth = budget.TargetBaseDepth;
        _sharedState.SetCompletedDepth(baseDepth);

        // Phase 1 Time Gating: Reserve at least 60% of the soft time budget for the Worker Pool!
        long maxPhase1Ms = Math.Max(5, (long)(budget.SoftLimitMs * 0.40));

        for (int i = 0; i < movesArray.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            Move rootMove = movesArray[i];
            Board scratchBoard = rootBoard;
            if (!scratchBoard.MakeMove(rootMove, out _)) continue;

            _sharedState.IncrementNodes(1);

            MoveList opponentReplies = default;
            MoveGenerator.GenerateLegalMoves(in scratchBoard, ref opponentReplies);

            // Immediate Checkmate or Stalemate Check
            if (opponentReplies.Count == 0)
            {
                if (scratchBoard.IsInCheck())
                {
                    _sharedState.UpdateCandidateBaseline(i, 30000, Move.Null);
                    _sharedState.UpdateCandidateTrajectory(i, ReadOnlySpan<ZapTrajectoryStep>.Empty);
                    _sharedState.SortCandidatesAndPrune(_params.DeadStemPruneMarginCp);
                    _timeManager.Stop();
                    return rootMove;
                }
                else
                {
                    // Stalemate draw
                    _sharedState.UpdateCandidateBaseline(i, 0, Move.Null);
                    _sharedState.UpdateCandidateTrajectory(i, ReadOnlySpan<ZapTrajectoryStep>.Empty);
                    continue;
                }
            }

            // Immediate 3-fold repetition, 50-move rule, or Insufficient Material draw on rootMove
            if ((scratchBoard.HalfmoveClock >= 4 && (scratchBoard.IsFiftyMoveRule() || scratchBoard.IsThreefoldRepetition() || scratchBoard.IsRepetition(1))) ||
                scratchBoard.HasInsufficientMaterial())
            {
                _sharedState.UpdateCandidateBaseline(i, 0, Move.Null);
                _sharedState.UpdateCandidateTrajectory(i, ReadOnlySpan<ZapTrajectoryStep>.Empty);
                continue;
            }

            // 2-Ply Blunder Shield:
            // Check if rootMove allows an immediate opponent checkmate on reply 1, hangs major/minor pieces,
            // or allows the opponent to claim an immediate threefold repetition draw!
            bool allowsImmediateMate = false;
            bool allowsImmediateDraw = false;
            int maxOpponentTacticalLoss = 0;
            for (int r = 0; r < opponentReplies.Count; r++)
            {
                Move reply = opponentReplies[r];
                Board replyBoard = scratchBoard;
                if (replyBoard.MakeMove(reply, out _))
                {
                    if (replyBoard.HalfmoveClock >= 4 && (replyBoard.IsThreefoldRepetition() || replyBoard.IsFiftyMoveRule()) ||
                        replyBoard.HasInsufficientMaterial())
                    {
                        allowsImmediateDraw = true;
                    }
                }

                if (scratchBoard.GivesCheck(reply))
                {
                    Board mateCheckBoard = scratchBoard;
                    if (mateCheckBoard.MakeMove(reply, out _))
                    {
                        MoveList evasions = default;
                        MoveGenerator.GenerateLegalMoves(in mateCheckBoard, ref evasions);
                        if (evasions.Count == 0)
                        {
                            allowsImmediateMate = true;
                            break;
                        }
                        else if (evasions.Count <= 3)
                        {
                            // Forced Mate in 2 check: if every evasion allows an immediate checkmate by opponent
                            bool allEvasionsMated = true;
                            for (int e = 0; e < evasions.Count; e++)
                            {
                                Board afterEvasion = mateCheckBoard;
                                if (afterEvasion.MakeMove(evasions[e], out _))
                                {
                                    MoveList replies2 = default;
                                    MoveGenerator.GenerateLegalMoves(in afterEvasion, ref replies2);
                                    bool hasMateReply = false;
                                    for (int m2 = 0; m2 < replies2.Count; m2++)
                                    {
                                        if (afterEvasion.GivesCheck(replies2[m2]))
                                        {
                                            Board mate2 = afterEvasion;
                                            if (mate2.MakeMove(replies2[m2], out _))
                                            {
                                                MoveList evasions2 = default;
                                                MoveGenerator.GenerateLegalMoves(in mate2, ref evasions2);
                                                if (evasions2.Count == 0)
                                                {
                                                    hasMateReply = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                    if (!hasMateReply)
                                    {
                                        allEvasionsMated = false;
                                        break;
                                    }
                                }
                            }
                            if (allEvasionsMated)
                            {
                                allowsImmediateMate = true;
                                break;
                            }
                        }
                    }
                }

                if (reply.IsCapture)
                {
                    // Compute exchange balance from opponent's perspective:
                    // Notice that if the initial exchange trades equal material (e.g. Knight for Knight or Pawn for Knight where our piece also captures back),
                    // the net tactical loss for us is: piece lost - piece gained in rootMove (if rootMove was a capture on that square!).
                    int see = StaticExchangeEvaluator.Evaluate(in scratchBoard, reply);

                    // If rootMove captured a piece on that square, subtract the material we just gained!
                    if (rootMove.IsCapture && rootMove.To == reply.To)
                    {
                        Piece ourCapturedVictim = rootBoard.GetPieceAt(rootMove.To);
                        if (ourCapturedVictim != Piece.None)
                        {
                            int rootGain = StaticExchangeEvaluator.GetPieceValue(ourCapturedVictim.Type());
                            see -= rootGain;
                        }
                    }
                    
                    // If SEE suggests defending piece can recapture, verify if the defender is pinned to our King:
                    Piece targetPiece = scratchBoard.GetPieceAt(reply.To);
                    if (targetPiece != Piece.None && see <= 100)
                    {
                        // Check if any legal replies by us can recapture on reply.To:
                        Board afterReply = scratchBoard;
                        if (afterReply.MakeMove(reply, out _))
                        {
                            MoveList ourReplies = default;
                            MoveGenerator.GenerateLegalMoves(in afterReply, ref ourReplies);
                            bool canRecapture = false;
                            for (int o = 0; o < ourReplies.Count; o++)
                            {
                                if (ourReplies[o].To == reply.To) { canRecapture = true; break; }
                            }
                            if (!canRecapture)
                            {
                                // The capture was completely free!
                                see = StaticExchangeEvaluator.GetPieceValue(targetPiece.Type());
                            }
                        }
                    }

                    if (see > maxOpponentTacticalLoss)
                    {
                        maxOpponentTacticalLoss = see;
                    }
                }
                else
                {
                    // Opponent quiet tactical threat detection:
                    // Check if opponent reply is an immediate pawn fork or knight fork on our major/minor pieces
                    Piece replyPiece = scratchBoard.GetPieceAt(reply.From);
                    PieceType replyType = replyPiece.Type();
                    Color replyColor = replyPiece.Color();
                    byte toByte = (byte)reply.To;

                    if (replyType == PieceType.Pawn)
                    {
                        ulong pAttacks = replyColor == Color.White
                            ? Bitboard.WhitePawnAttacks[toByte]
                            : Bitboard.BlackPawnAttacks[toByte];

                        ulong ourPiecesMask = engineColor == Color.White ? scratchBoard.White : scratchBoard.Black;
                        ulong ourPawnsMask = ourPiecesMask & scratchBoard.Pawns;
                        ulong attackedFriendly = pAttacks & ourPiecesMask & ~ourPawnsMask;

                        if (attackedFriendly != 0)
                        {
                            int forkCount = BitOperations.PopCount(attackedFriendly);
                            if (forkCount >= 2)
                            {
                                // Severe pawn fork on multiple pieces!
                                if (300 > maxOpponentTacticalLoss) maxOpponentTacticalLoss = 300;
                            }
                        }
                    }
                    else if (replyType == PieceType.Knight)
                    {
                        ulong nAttacks = Bitboard.KnightAttacks[toByte];
                        ulong ourPiecesMask = engineColor == Color.White ? scratchBoard.White : scratchBoard.Black;
                        ulong attackedHighVal = nAttacks & ourPiecesMask & (scratchBoard.Queens | scratchBoard.Rooks | scratchBoard.Kings);
                        if (BitOperations.PopCount(attackedHighVal) >= 2)
                        {
                            // Knight royal/major fork!
                            if (350 > maxOpponentTacticalLoss) maxOpponentTacticalLoss = 350;
                        }
                    }
                }
            }

            if (allowsImmediateMate)
            {
                _sharedState.UpdateCandidateBaseline(i, -30000, Move.Null);
                _sharedState.UpdateCandidateTrajectory(i, ReadOnlySpan<ZapTrajectoryStep>.Empty);
                continue;
            }

            int probeDepth = _timeManager.ElapsedMilliseconds >= maxPhase1Ms
                ? Math.Min(3, baseDepth - 1)
                : (baseDepth - 1);

            ZapTrajectory trajectory = default;
            int terminalScore = ZapProbe.Trace(
                ref scratchBoard,
                probeDepth,
                rootPolicy,
                _evaluator,
                engineColor,
                ref trajectory,
                initialLastMove: rootMove,
                parameters: _params,
                tablebase: _tablebase,
                searchPly: 1);

            // Immediate Opponent Draw Resolution:
            // If the opponent can immediately force a 3-fold repetition draw or 50-move draw on reply 1,
            // cap the score at draw (0 cp) so an advantageous position is not thrown away.
            if (allowsImmediateDraw)
            {
                terminalScore = Math.Min(terminalScore, 0);
            }

            // 2-Fold Repetition Penalty with game history:
            // If playing rootMove steps into a 2-fold repetition with past game history and engine is ahead,
            // penalize returning to repeated game positions to prevent repetition cycles.
            if (scratchBoard.HalfmoveClock >= 4 && scratchBoard.RepetitionCount() == 1)
            {
                if (terminalScore > 0)
                {
                    terminalScore = Math.Max(0, terminalScore - 200);
                }
            }

            // Severe immediate blunder penalty (hanging Queen, Rook, Minor on move 1)
            if (maxOpponentTacticalLoss >= 200)
            {
                terminalScore -= maxOpponentTacticalLoss * 10;
            }

            _sharedState.IncrementNodes(trajectory.Count);

            Move followup = trajectory.Count > 0 ? trajectory[0].Move : Move.Null;
            _sharedState.UpdateCandidateBaseline(i, terminalScore, followup);
            _sharedState.UpdateCandidateTrajectory(i, trajectory.AsSpan());
        }

        // Print Phase 1 candidates:
        // System.Diagnostics.Debug.WriteLine or test inspect

        // Sort candidates descending by baseline eval and prune dead stems
        _sharedState.SortCandidatesAndPrune(_params.HardAspirationMarginCp);

        // Emit initial anytime telemetry
        EmitInfo(infoCallback, baseDepth);

        // Anytime Contract: valid BestMove is now guaranteed at all subsequent moments!
        if (cancellationToken.IsCancellationRequested || _sharedState.IsStopped)
        {
            _timeManager.Stop();
            return _sharedState.GetBestMove();
        }

        // -------------------------------------------------------------
        // Phase 2 & 3: Gated Spine Qualification & Dual-Role Exploration
        // -------------------------------------------------------------
        _workQueue.Clear();

        // 1. Refutation Squad: Targets the Leader spine only on OPPONENT plies
        ZapRootCandidate leader = _sharedState.GetBestCandidate();
        int initialLeaderIdx = _sharedState.BestCandidateIndex;
        if (!leader.IsDeadStem && leader.Trajectory.Length > 0)
        {
            ExtractCrosspoints(
                in rootBoard,
                leader.Move,
                initialLeaderIdx,
                leader.Trajectory,
                baseDepth,
                in budget,
                engineColor,
                generation,
                ZapAgentRole.Refutation);
        }

        // 2. Promotion Squad: Targets qualified Contenders within ContenderPromotionMarginCp on ENGINE plies
        int promotionCutoff = leader.Score - _params.ContenderPromotionMarginCp;
        for (int i = 1; i < _sharedState.CandidateCount; i++)
        {
            ZapRootCandidate contender = _sharedState.GetCandidate(i);
            if (contender.IsDeadStem || contender.Score < promotionCutoff || contender.Trajectory.Length == 0)
            {
                continue;
            }

            ExtractCrosspoints(
                in rootBoard,
                contender.Move,
                i,
                contender.Trajectory,
                baseDepth,
                in budget,
                engineColor,
                generation,
                ZapAgentRole.Promotion);
        }

        // Start worker pool to explore queued crosspoints
        _workerPool.StartExploration(engineColor, budget);

        // Iterative deepening & monitoring loop
        int currentIterDepth = baseDepth + 1;
        int prevLeaderIdx = _sharedState.BestCandidateIndex;
        while (currentIterDepth <= budget.MaxDepthHorizon && !_sharedState.IsStopped && !cancellationToken.IsCancellationRequested)
        {
            _sharedState.SetCompletedDepth(currentIterDepth);

            // Periodic polling
            if (_timeManager.ShouldStop(_sharedState.TotalNodes, in budget, cancellationToken))
            {
                _sharedState.RequestStop();
                break;
            }

            ZapRootCandidate currentLeader = _sharedState.GetBestCandidate();
            int leaderIdx = _sharedState.BestCandidateIndex;

            // If a new Leader was promoted, immediately dispatch Refutation Squad on the new Leader!
            if (leaderIdx != prevLeaderIdx && !_sharedState.IsRootMoveRefutedOrDead(leaderIdx))
            {
                Board newLeaderBoard = rootBoard;
                if (newLeaderBoard.MakeMove(currentLeader.Move, out _))
                {
                    if ((newLeaderBoard.HalfmoveClock >= 4 && (newLeaderBoard.IsFiftyMoveRule() || newLeaderBoard.IsThreefoldRepetition() || newLeaderBoard.IsRepetition(1))) ||
                        newLeaderBoard.HasInsufficientMaterial())
                    {
                        // Leader is an immediate terminal draw: score remains 0, cannot be refuted or deepened
                        continue;
                    }

                    ZapTrajectory newLeaderTraj = default;
                    int newLeaderScore = ZapProbe.Trace(
                        ref newLeaderBoard,
                        currentIterDepth - 1,
                        rootPolicy,
                        _evaluator,
                        engineColor,
                        ref newLeaderTraj,
                        initialLastMove: currentLeader.Move,
                        parameters: _params,
                        tablebase: _tablebase);

                    _sharedState.IncrementNodes(newLeaderTraj.Count);
                    _sharedState.AppendCandidateScoreHistory(leaderIdx, $"LeaderPromoteDeepen(d={currentIterDepth})->{newLeaderScore} ");
                    _sharedState.TryUpdateScore(leaderIdx, newLeaderScore, currentIterDepth);
                    _sharedState.UpdateCandidateTrajectory(leaderIdx, newLeaderTraj.AsSpan());

                    // Disapproval Agent immediately stress-tests opponent junctions of the new Leader
                    TestLeaderRefutations(
                        in rootBoard,
                        leaderIdx,
                        currentLeader.Move,
                        newLeaderTraj.AsSpan(),
                        currentIterDepth,
                        in budget,
                        engineColor,
                        generation,
                        rootPolicy,
                        _evaluator);

                    if (!_sharedState.IsRootMoveRefutedOrDead(leaderIdx))
                    {
                        ExtractCrosspoints(
                            in rootBoard,
                            currentLeader.Move,
                            leaderIdx,
                            newLeaderTraj.AsSpan(),
                            currentIterDepth,
                            in budget,
                            engineColor,
                            generation,
                            ZapAgentRole.Refutation);
                        _workerPool.WakeWorkers();
                    }
                }
                prevLeaderIdx = _sharedState.BestCandidateIndex;
            }
            ZapRootCandidate contender = _sharedState.CandidateCount > 1 ? _sharedState.GetCandidate(1) : default;

            // Check Stable Dominance early break
            if (_timeManager.CheckStableDominance(currentIterDepth, currentLeader.Score, contender.Score, currentLeader.Move.RawValue))
            {
                break;
            }

            // Check Soft time limit exit (with panic extension check)
            if (_timeManager.HasReachedSoftLimit(in budget, currentLeader.Score, contender.Score, currentLeader.Move.RawValue))
            {
                if (_workQueue.IsEmpty || _timeManager.ElapsedMilliseconds >= budget.HardLimitMs)
                {
                    break;
                }
            }

            // If work queue is emptying and time remains, deepen the top candidates and extract deeper crosspoints!
            if (_workQueue.Count < 4)
            {
                _tt.Store(rootBoard.ZobristKey, currentLeader.Move, (byte)Math.Min(255, currentIterDepth), (short)currentLeader.Score);

                // Deepen Leader (Refutation Squad)
                leaderIdx = _sharedState.BestCandidateIndex;
                if (!_sharedState.IsRootMoveRefutedOrDead(leaderIdx))
                {
                    Board leaderBoard = rootBoard;
                    if (leaderBoard.MakeMove(currentLeader.Move, out _))
                    {
                        if ((leaderBoard.HalfmoveClock >= 4 && (leaderBoard.IsFiftyMoveRule() || leaderBoard.IsThreefoldRepetition() || leaderBoard.IsRepetition(1))) ||
                            leaderBoard.HasInsufficientMaterial())
                        {
                            // Leader is an immediate terminal draw: score remains 0
                            continue;
                        }

                        ZapTrajectory deeperTraj = default;
                        int newScore = ZapProbe.Trace(
                            ref leaderBoard,
                            currentIterDepth - 1,
                            rootPolicy,
                            _evaluator,
                            engineColor,
                            ref deeperTraj,
                            initialLastMove: currentLeader.Move,
                            parameters: _params,
                            leaderScore: currentLeader.Score,
                            sharedState: _sharedState,
                            rootCandidateIndex: leaderIdx,
                            tablebase: _tablebase);

                        _sharedState.IncrementNodes(deeperTraj.Count);
                        _sharedState.AppendCandidateScoreHistory(leaderIdx, $"LeaderEmptyQueueDeepen(d={currentIterDepth})->{newScore} ");
                        _sharedState.TryUpdateScore(leaderIdx, newScore, currentIterDepth);
                        _sharedState.UpdateCandidateTrajectory(leaderIdx, deeperTraj.AsSpan());

                        ExtractCrosspoints(
                            in rootBoard,
                            currentLeader.Move,
                            leaderIdx,
                            deeperTraj.AsSpan(),
                            currentIterDepth,
                            in budget,
                            engineColor,
                            generation,
                            ZapAgentRole.Refutation);
                        _workerPool.WakeWorkers();
                    }
                }

                // Deepen Qualified Contenders (Promotion Squad)
                int promoThreshold = currentLeader.Score - _params.ContenderPromotionMarginCp;
                int maxCandidatesToCheck = Math.Min(4, _sharedState.CandidateCount);
                for (int c = 0; c < maxCandidatesToCheck; c++)
                {
                    if (c == leaderIdx) continue;
                    ZapRootCandidate cand = _sharedState.GetCandidate(c);
                    if (cand.IsDeadStem || cand.Score < promoThreshold || _sharedState.IsRootMoveRefutedOrDead(c)) continue;

                    Board candBoard = rootBoard;
                    if (!candBoard.MakeMove(cand.Move, out _)) continue;

                    ZapTrajectory deeperTraj = default;
                    int newScore = ZapProbe.Trace(
                        ref candBoard,
                        currentIterDepth - 1,
                        rootPolicy,
                        _evaluator,
                        engineColor,
                        ref deeperTraj,
                        initialLastMove: cand.Move,
                        parameters: _params,
                        leaderScore: currentLeader.Score,
                        sharedState: _sharedState,
                        rootCandidateIndex: c,
                        tablebase: _tablebase);

                    _sharedState.IncrementNodes(deeperTraj.Count);
                    _sharedState.TryUpdateScore(c, newScore, currentIterDepth);
                    _sharedState.UpdateCandidateTrajectory(c, deeperTraj.AsSpan());

                    ExtractCrosspoints(
                        in rootBoard,
                        cand.Move,
                        c,
                        deeperTraj.AsSpan(),
                        currentIterDepth,
                        in budget,
                        engineColor,
                        generation,
                        ZapAgentRole.Promotion);
                    _workerPool.WakeWorkers();
                }
            }

            EmitInfo(infoCallback, currentIterDepth);
            if (_timeManager.ElapsedMilliseconds >= budget.HardLimitMs - 15)
            {
                break;
            }
            // Cooperative worker processing: search thread helps drain high-priority crosspoints
            int processed = 0;
            while (processed < 8 && _workQueue.TryDequeue(out ZapCrosspoint cp, _sharedState, generation))
            {
                _workerPool.ProcessCrosspoint(in cp, rootPolicy, _evaluator);
                processed++;
                if (_sharedState.IsStopped || _timeManager.ElapsedMilliseconds >= budget.HardLimitMs - 15)
                {
                    break;
                }
            }

            currentIterDepth++;
        }

        // Spine exploration draining: if time remains below soft limit, drain pending crosspoints
        while (!_workQueue.IsEmpty && !_sharedState.IsStopped && !_timeManager.HasReachedSoftLimit(in budget, _sharedState.GetBestCandidate().Score, _sharedState.CandidateCount > 1 ? _sharedState.GetCandidate(1).Score : 0, _sharedState.GetBestMove().RawValue))
        {
            if (_workQueue.TryDequeue(out ZapCrosspoint cp, _sharedState, generation))
            {
                _workerPool.ProcessCrosspoint(in cp, rootPolicy, _evaluator);
            }
            else
            {
                break;
            }
        }

        _workerPool.StopExploration();
        _timeManager.Stop();

        // Re-sort non-refuted, non-dead candidates to ensure the highest score candidate is chosen
        _sharedState.SortCandidatesAndPrune(_params.HardAspirationMarginCp);

        ZapRootCandidate finalBest = _sharedState.GetBestCandidate();
        _tt.Store(rootBoard.ZobristKey, finalBest.Move, (byte)Math.Min(255, _sharedState.CompletedDepth), (short)finalBest.Score);

        EmitInfo(infoCallback, _sharedState.CompletedDepth);

        return finalBest.Move;
    }

    /// <summary>
    /// Disapproval Agent: Immediately stress-tests opponent blunder junctions of a newly promoted Leader.
    /// If the Leader's high evaluation relies on an opponent concession/blunder down the spine,
    /// alternative opponent replies at that junction are explored immediately.
    /// </summary>
    private void TestLeaderRefutations(
        in Board rootBoard,
        int leaderIdx,
        Move leaderMove,
        ReadOnlySpan<ZapTrajectoryStep> steps,
        int currentDepth,
        in SearchBudget budget,
        Color engineColor,
        uint generation,
        ZapPolicy policy,
        IEvaluator evaluator)
    {
        return; // Background worker pool processes crosspoints; synchronous TestLeaderRefutations caused leader index race
    }

    private void ExtractCrosspoints(
        in Board rootBoard,
        Move rootMove,
        int candidateIndex,
        ReadOnlySpan<ZapTrajectoryStep> steps,
        int currentDepth,
        in SearchBudget budget,
        Color engineColor,
        uint generation,
        ZapAgentRole role)
    {
        Board spineBoard = rootBoard;
        if (!spineBoard.MakeMove(rootMove, out _)) return;

        for (int ply = 0; ply < steps.Length; ply++)
        {
            ZapTrajectoryStep step = steps[ply];

            // Asymmetric Squad Filter:
            // Refutation Squad only investigates OPPONENT moves (step.SideToMove != engineColor)
            // Promotion Squad only investigates ENGINE moves (step.SideToMove == engineColor)
            bool matchesSquad = role == ZapAgentRole.Refutation
                ? step.SideToMove != engineColor
                : step.SideToMove == engineColor;

            if (matchesSquad)
            {
                int absDelta = Math.Abs((int)step.EvalDelta);

                // Depth-adaptive shock threshold: sensitive near root, noise-filtering near leaves
                int dynamicThreshold = _params.AdaptiveShockBaseCp + (ply * _params.AdaptiveShockSlopeCp);

                // Priority Crosspoints:
                // For Refutation Squad, ALWAYS investigate ply 0 (opponent's immediate reply to Leader's move)
                bool isImmediateRefutation = role == ZapAgentRole.Refutation && ply == 0;

                // Opponent Blunder Junction Detection:
                // If this is an opponent ply, look ahead to see if the opponent's move conceded a large eval gain
                // to the engine on the subsequent reply (indicating an opponent blunder down the spine).
                int opponentConcededGain = 0;
                if (role == ZapAgentRole.Refutation && ply + 1 < steps.Length)
                {
                    ZapTrajectoryStep nextStep = steps[ply + 1];
                    if (nextStep.SideToMove == engineColor && nextStep.EvalDelta > 0)
                    {
                        opponentConcededGain = nextStep.EvalDelta;
                    }
                }

                bool isOpponentBlunderJunction = opponentConcededGain >= dynamicThreshold;

                if (absDelta >= dynamicThreshold || isImmediateRefutation || isOpponentBlunderJunction)
                {
                    CrosspointKind kind = step.SideToMove == engineColor
                        ? CrosspointKind.Opportunity
                        : CrosspointKind.AdversarialRefutation;

                    int remainingHorizon = Math.Max(2, budget.MaxDepthHorizon - (ply + 1));

                    // Tablebase shock boost, immediate refutation boost, or opponent blunder junction boost
                    int tbBoost = Math.Abs((int)step.StaticEval) >= 28000 ? 500 : 0;
                    int refutationBoost = isImmediateRefutation ? 400 : 0;
                    int blunderBoost = isOpponentBlunderJunction ? Math.Min(2000, opponentConcededGain * 2) : 0;
                    short effectiveShock = (short)Math.Min(absDelta + tbBoost + refutationBoost + blunderBoost, short.MaxValue);

                    _workQueue.Enqueue(new ZapCrosspoint(
                        board: spineBoard,
                        rootCandidateIndex: candidateIndex,
                        ply: ply + 1,
                        baselineScore: step.StaticEval,
                        shockMagnitude: effectiveShock,
                        kind: kind,
                        remainingHorizon: remainingHorizon,
                        originalMove: step.Move,
                        rootBiasWeight: _params.CrosspointRootBiasWeight,
                        generation: generation,
                        role: role
                    ));
                }
            }

            if (!spineBoard.MakeMove(step.Move, out _))
            {
                break;
            }
        }
    }

    private void EmitInfo(Action<ZapSearchStats>? infoCallback, int depth)
    {
        if (infoCallback == null) return;

        ZapRootCandidate best = _sharedState.GetBestCandidate();
        long elapsed = Math.Max(1, _timeManager.ElapsedMilliseconds);
        long nodes = _sharedState.TotalNodes;
        long nps = (nodes * 1000) / elapsed;

        Move[] pv = best.BestFollowup.IsNull ? [best.Move] : [best.Move, best.BestFollowup];

        var stats = new ZapSearchStats(
            Depth: depth,
            ScoreCp: best.Score,
            Nodes: nodes,
            ElapsedMs: elapsed,
            Nps: nps,
            BestMove: best.Move,
            Pv: pv
        );

        infoCallback(stats);
    }

    public void Stop()
    {
        _sharedState.RequestStop();
        _workerPool.StopExploration();
    }

    public void Dispose()
    {
        _sharedState.RequestStop();
        _workerPool.Dispose();
        _tt.Dispose();
        if (_evaluator is IDisposable disp)
        {
            disp.Dispose();
        }
    }
}
