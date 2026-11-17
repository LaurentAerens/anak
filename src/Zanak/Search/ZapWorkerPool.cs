using System.Runtime.CompilerServices;
using System.Diagnostics;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Data.Nnue;
using SearchTraceWriter = Anak.Search.SearchTraceWriter;
using Zanak.Time;

namespace Zanak.Search;

public readonly record struct ZapAgentTrace(
    uint Generation, ulong ZobristKey, ulong LinkedSpinesMask, int Ply, ZapAgentRole Role,
    int BaselineScore, int BestScore, Move Alternative, bool Completed, int AppliedRoots,
    ZapTrajectoryStep[] Trajectory);

public readonly record struct ZapBatchTrace(
    uint Generation, ulong ZobristKey, int Ply, ZapAgentRole Role, int Horizon,
    int BaselineScore, int BestScore, int ExploredSiblings, int RemainingSiblings,
    bool Completed, int AppliedRoots, double ElapsedMs);

internal sealed class ZapSiblingWork
{
    public Move[] Moves = [];
    public int Next;
    public int Batches;
    public int Baseline;
    public int BestScore;
    public Move BestMove;
    public ZapTrajectoryStep[] BestSteps = [];
}

/// <summary>
/// Pre-spawned, long-running pool of background worker threads dedicated to exploring shock crosspoints
/// using recursive zero-branching probes (pure ZAP).
/// </summary>
public sealed class ZapWorkerPool : IDisposable
{
    private readonly int _workerCount;
    private readonly Thread[] _threads;
    private readonly AutoResetEvent[] _wakeEvents;
    private readonly ManualResetEventSlim _allIdleEvent = new(true);
    private readonly object _runLock = new();
    private readonly ZapWorkQueue _workQueue;
    private readonly ZapSharedState _sharedState;
    private readonly ZapParams _params;
    private readonly ZapTranspositionTable? _tt;
    private readonly IEvaluator _masterEvaluator;
    private readonly ITablebaseProber? _tablebase;
    private readonly TimeManager? _timeManager;

    private volatile bool _disposed;
    private volatile bool _isRunning;
    private bool _runOnCaller;
    private int _activeWorkers;
    private Color _engineColor;
    private SearchBudget _currentBudget;
    private int _inFlight;
    private Board _rootBoard;
    private int _rootEpoch;

    public int WorkerCount => _workerCount;
    internal SearchTraceWriter? SearchTrace { get; set; }

    public ZapWorkerPool(
        int workerCount,
        ZapWorkQueue workQueue,
        ZapSharedState sharedState,
        ZapParams parameters,
        IEvaluator masterEvaluator,
        ZapTranspositionTable? tt = null,
        ITablebaseProber? tablebase = null,
        TimeManager? timeManager = null)
    {
        _workerCount = Math.Max(1, workerCount);
        _workQueue = workQueue;
        _sharedState = sharedState;
        _params = parameters;
        _masterEvaluator = masterEvaluator;
        _tt = tt;
        _tablebase = tablebase;
        _timeManager = timeManager;
        _tree.RootChanged = (rootIndex, value, line) => _sharedState.SetTreeResult(rootIndex, value, line);

        _threads = new Thread[_workerCount];
        _wakeEvents = new AutoResetEvent[_workerCount];

        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i] = new AutoResetEvent(false);
            int workerId = i;
            _threads[i] = new Thread(() => WorkerLoop(workerId))
            {
                Name = $"ZapWorker-{workerId}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _threads[i].Start();
        }
    }

    /// <summary>
    /// Notifies the worker pool of the current search's root board so worker-local evaluators can align topologies.
    /// </summary>
    public void SetRootBoard(in Board rootBoard)
    {
        _rootBoard = rootBoard;
        Interlocked.Increment(ref _rootEpoch);
    }

    public void StartExploration(Color engineColor, in SearchBudget budget, bool runOnCaller = false)
    {
        lock (_runLock)
        {
            _engineColor = engineColor;
            _currentBudget = budget;
            _runOnCaller = runOnCaller;
            _isRunning = true;
        }
        if (!runOnCaller) WakeWorkers();
    }

    public void StopExploration()
    {
        lock (_runLock) _isRunning = false;
        WakeWorkers();
        _allIdleEvent.Wait();
    }

    public void WakeWorkers()
    {
        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i].Set();
        }
    }

    public void WaitForIdle(int timeoutMs = 50)
    {
        _allIdleEvent.Wait(timeoutMs);
    }

    /// <summary>True while any worker is processing a crosspoint (and may still enqueue new work).</summary>
    public bool HasInFlightWork => Volatile.Read(ref _inFlight) > 0;

    private int _agentsImproved;
    private int _agentsSpliced;
    private long _subspinePositions;
    private int _completedCrosspoints;
    private int _siblingsTraced;
    private long _firstBatchMs = -1;
    private readonly ZapSpineTree _tree = new();
    private readonly int[] _killerMoves = new int[64];
    public int AgentsImproved => Volatile.Read(ref _agentsImproved);
    public int AgentsSpliced => Volatile.Read(ref _agentsSpliced);
    public long SubspinePositions => Interlocked.Read(ref _subspinePositions);
    public int CompletedCrosspoints => Volatile.Read(ref _completedCrosspoints);
    public int SiblingsTraced => Volatile.Read(ref _siblingsTraced);
    public long FirstBatchMs => Interlocked.Read(ref _firstBatchMs);
    public event Action<ZapBatchTrace>? BatchCompleted;

    /// <summary>The spine tree shared by the search thread and all agents.</summary>
    internal ZapSpineTree SpineTree => _tree;

    public void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _agentsImproved, 0);
        Interlocked.Exchange(ref _agentsSpliced, 0);
        Interlocked.Exchange(ref _subspinePositions, 0);
        Interlocked.Exchange(ref _completedCrosspoints, 0);
        Interlocked.Exchange(ref _siblingsTraced, 0);
        Interlocked.Exchange(ref _firstBatchMs, -1);
        Array.Clear(_killerMoves, 0, _killerMoves.Length);
    }

    private void WorkerLoop(int workerId)
    {
        var policy = new ZapPolicy(_params);
        IEvaluator evaluator = _masterEvaluator is IIncrementalEvaluator incremental
            ? incremental.CreateWorkerInstance()
            : _masterEvaluator;

        int lastEpoch = 0;

        while (!_disposed)
        {
            _wakeEvents[workerId].WaitOne();

            lock (_runLock)
            {
                if (!_isRunning || _disposed || _runOnCaller) continue;
                _allIdleEvent.Reset();
                _activeWorkers++;
            }

            while (CanContinue())
            {
                // Align root NNUE expert when root changes
                int currentEpoch = Volatile.Read(ref _rootEpoch);
                if (currentEpoch != lastEpoch)
                {
                    if (evaluator is ZanakNnueEvaluator workerNnue)
                    {
                        workerNnue.SetRootTopology(in _rootBoard);
                    }
                    lastEpoch = currentEpoch;
                }

                if (_workQueue.TryDequeue(out ZapCrosspoint cp, _sharedState, _sharedState.Generation))
                {
                    Interlocked.Increment(ref _inFlight);
                    try
                    {
                        ProcessCrosspoint(in cp, policy, evaluator);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }
                }
                else
                {
                    _wakeEvents[workerId].WaitOne(2);
                    if (!_isRunning || _sharedState.IsStopped)
                    {
                        break;
                    }
                }
            }

            lock (_runLock)
            {
                if (--_activeWorkers == 0) _allIdleEvent.Set();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void ProcessCrosspoint(in ZapCrosspoint cp, ZapPolicy policy, IEvaluator evaluator)
    {
        ZapSiblingWork? work = cp.Work;
        try
        {
            ProcessBatch(in cp, policy, evaluator, ref work);
        }
        finally
        {
            _workQueue.FinishBatch(in cp, work, work != null && work.Next < work.Moves.Length);
        }
    }

    private void ProcessBatch(in ZapCrosspoint cp, ZapPolicy policy, IEvaluator evaluator, ref ZapSiblingWork? work)
    {
        long started = Stopwatch.GetTimestamp();
        ulong linkedMask = cp.LinkedSpinesMask;
        if (linkedMask == 0) return;

        Board board;
        if (cp.HasBoard) board = cp.Board;
        else if (cp.TreeEdge == null || !ZapSpineTree.TryRebuildBoard(cp.TreeEdge, cp.TreeStep, out board) ||
            board.ZobristKey != cp.ZobristKey) return;
        ulong zobrist = board.ZobristKey;
        bool isOpponentTurn = board.SideToMove != _engineColor;
        int remainingHorizon = Math.Max(1, cp.RemainingHorizon);
        if (cp.Generation != _sharedState.Generation || !CanContinue() ||
            cp.OriginalMove.IsNull || !board.IsLegal(cp.OriginalMove)) return;

        // Seed the incremental NNUE accumulator once at the crosspoint board (depth 0);
        // each sibling in this batch rolls back to depth 0 instead of recomputing from scratch.
        if (evaluator is ZanakNnueEvaluator nnue) nnue.ResetAccumulatorStack(in board);
        else if (evaluator is IIncrementalEvaluator rootIncremental) rootIncremental.InitSearch(in board);

        var children = new List<ZapSpineTree.Child>();
        if (work == null)
        {
            MoveList legalMoves = default;
            MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);
            if (legalMoves.Count == 0) return;
            Span<bool> keep = stackalloc bool[legalMoves.Count];
            Span<int> siblingLoss = stackalloc int[legalMoves.Count];
            int filterMargin = cp.Ply >= 4
                ? Math.Max(80, _params.RootFilterMarginCp * 3 / 4)
                : _params.RootFilterMarginCp;
            ZapRootFilter.Filter(in board, legalMoves.AsSpan(), keep, filterMargin, siblingLoss, out int nullLoss);
            var siblings = new List<(Move Move, int Score)>(legalMoves.Count);
            Move hint = _tt != null && _tt.TryGet(zobrist, out var entry) ? entry.BestMove : Move.Null;
            int killerRaw = (uint)cp.Ply < (uint)_killerMoves.Length ? Volatile.Read(ref _killerMoves[cp.Ply]) : 0;
            for (int i = 0; i < legalMoves.Count; i++)
            {
                Move move = legalMoves[i];
                if (!keep[i] || move == cp.OriginalMove) continue;
                int score = move == hint ? 100_000 : 0;
                if (move.IsPromotion) score += 20_000;
                if (move.IsCapture)
                {
                    int see = StaticExchangeEvaluator.Evaluate(in board, move);
                    score += see >= 0 ? 10_000 + see : see;
                }
                else
                {
                    if (move.RawValue == killerRaw) score += 8_000;
                    if (nullLoss >= 100) score += Math.Max(0, nullLoss - siblingLoss[i]) * 10;
                    score += ZapPolicy.ScoreQuietCentrality(move);
                }
                if (board.GivesCheck(move)) score += 5_000;
                siblings.Add((move, score));
            }
            // This orders agent siblings only; the greedy spine policy still selects exactly one move.
            work = new ZapSiblingWork { Moves = siblings.OrderByDescending(s => s.Score).Select(s => s.Move).ToArray() };
            ZapTrajectory originalTrajectory = default;
            work.Baseline = TraceContinuation(in board, cp.OriginalMove, remainingHorizon, cp.Ply, policy, evaluator, ref originalTrajectory);
            work.BestScore = work.Baseline;
            work.BestSteps = originalTrajectory.AsSpan().ToArray();
            children.Add(new ZapSpineTree.Child(work.BestSteps, work.Baseline));
        }

        _sharedState.GetBestCandidate(out int currentLeaderIdx);
        bool refutingLeader = isOpponentTurn && (uint)currentLeaderIdx < 64 && (linkedMask & (1UL << currentLeaderIdx)) != 0;
        int secondBestScore = refutingLeader ? _sharedState.GetSecondBestCandidate().Score : int.MinValue;

        int batchLimit = Math.Max(1, _params.MaxSiblingExploration);
        bool improved = false;
        for (int count = 0; count < batchLimit && work.Next < work.Moves.Length; count++)
        {
            if (!CanContinue() || cp.Generation != _sharedState.Generation || !_workQueue.IsCurrent(in cp)) break;
            Move sibling = work.Moves[work.Next++];

            ZapTrajectory traj = default;
            int terminalScore = TraceContinuation(in board, sibling, remainingHorizon, cp.Ply, policy, evaluator, ref traj);
            ZapTrajectoryStep[] steps = traj.AsSpan().ToArray();
            children.Add(new ZapSpineTree.Child(steps, terminalScore));
            Interlocked.Increment(ref _siblingsTraced);

            if (isOpponentTurn ? terminalScore < work.BestScore : terminalScore > work.BestScore)
            {
                work.BestScore = terminalScore;
                work.BestMove = sibling;
                work.BestSteps = steps;
                improved = true;
                if (refutingLeader && secondBestScore > int.MinValue + 1000 && work.BestScore < secondBestScore - 30)
                {
                    break;
                }
            }
        }
        work.Batches++;
        bool completed = work.Next == work.Moves.Length;

        if (cp.Generation != _sharedState.Generation || !_isRunning || !_workQueue.IsCurrent(in cp))
        {
            _sharedState.TraceAgent(in cp, work.Baseline, work.BestScore, work.BestMove, false, 0, work.BestSteps);
            return;
        }

        if (!work.BestMove.IsNull)
        {
            if (improved)
            {
                Interlocked.Increment(ref _agentsImproved);
                if ((uint)cp.Ply < (uint)_killerMoves.Length)
                {
                    Volatile.Write(ref _killerMoves[cp.Ply], work.BestMove.RawValue);
                }
            }
            _tt?.Store(zobrist, work.BestMove, (byte)Math.Clamp(remainingHorizon, 1, 255), (short)Math.Clamp(work.BestScore, short.MinValue + 1, short.MaxValue - 1), flags: 2);
        }

        // 6. Expand the tree node and back the values up to every root spine through it.
        int candidateCount = _sharedState.CandidateCount;
        Span<int> scoresBefore = stackalloc int[candidateCount];
        for (int r = 0; r < candidateCount; r++) scoresBefore[r] = _sharedState.GetScore(r);
        EnsureRootSpines(linkedMask);
        var expanded = _tree.Expand(in cp, !isOpponentTurn, children, out int changedRoots);
        if (changedRoots > 0) Interlocked.Increment(ref _agentsSpliced);

        // 7. Every subspine position is a new crosspoint; distance from the node's best line lowers its rank.
        int offered = 0;
        foreach ((ZapSpineTree.Edge edge, int gap) in expanded)
        {
            offered += _workQueue.EnqueueSubspine(in board, edge, linkedMask, cp.Ply, cp.SpineGap + gap, _engineColor, cp.Generation);
        }
        Interlocked.Add(ref _subspinePositions, offered);

        for (int r = 0; r < candidateCount; r++)
        {
            ZapRootCandidate updated = _sharedState.GetCandidate(r);
            if (updated.Score != scoresBefore[r])
            {
                _workQueue.DecaySpinePickCount(r);
                CreditEarlierDecisions(r, in updated, zobrist, Math.Abs(updated.Score - scoresBefore[r]), cp.Generation);
            }
        }
        if (completed)
        {
            Interlocked.Increment(ref _completedCrosspoints);
            _sharedState.IncrementRootCompletedCrosspoints(cp.RootCandidateIndex);
        }
        Interlocked.CompareExchange(ref _firstBatchMs, _timeManager?.ElapsedMilliseconds ?? 0, -1);
        BatchCompleted?.Invoke(new ZapBatchTrace(cp.Generation, cp.ZobristKey, cp.Ply, cp.Role,
            remainingHorizon, work.Baseline, work.BestScore, work.Next, work.Moves.Length - work.Next,
            completed, changedRoots, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        _sharedState.TraceAgent(in cp, work.Baseline, work.BestScore, work.BestMove, completed, changedRoots, work.BestSteps);
    }

    /// <summary>Root spines published only through the shared state (e.g. by tests) enter the tree on first use.</summary>
    private void EnsureRootSpines(ulong mask)
    {
        while (mask != 0)
        {
            int r = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            if (r >= _sharedState.CandidateCount || _tree.HasRoot(r)) continue;
            ZapRootCandidate candidate = _sharedState.GetCandidate(r);
            _tree.SetRootSpine(r, candidate.Trajectory, candidate.Score);
        }
    }

    private bool CanContinue() => _isRunning && !_sharedState.IsStopped &&
        (_timeManager == null || _timeManager.ElapsedMilliseconds < _currentBudget.HardLimitMs);

    private int TraceContinuation(in Board board, Move move, int horizon, int ply,
        ZapPolicy policy, IEvaluator evaluator, ref ZapTrajectory trajectory)
    {
        Board scratch = board;
        IIncrementalEvaluator? incremental = evaluator as IIncrementalEvaluator;
        incremental?.RollbackTo(0);
        int parentRaw = (int)evaluator.Evaluate(in scratch);
        int parentEval = scratch.SideToMove == _engineColor ? parentRaw : -parentRaw;
        Piece piece = scratch.GetPieceAt(move.From);
        if (!scratch.MakeMove(move, out BoardState state, piece))
        {
            throw new InvalidOperationException($"Illegal crosspoint continuation: {move.ToUci()}");
        }
        SearchTrace?.WriteEdge(board.ZobristKey, scratch.ZobristKey, move);
        incremental?.PushMove(move, piece, state.CapturedPiece, in scratch);
        int childRaw = (int)evaluator.Evaluate(in scratch);
        int childEval = scratch.SideToMove == _engineColor ? childRaw : -childRaw;

        ZapTrajectory tail = default;
        int score = ZapProbe.Trace(ref scratch, Math.Max(0, horizon - 1), policy, evaluator, _engineColor, ref tail,
            initialLastMove: move, parameters: _params, tablebase: _tablebase, searchPly: ply + 1, tt: _tt,
            maxPlies: Math.Max(0, horizon - 1), searchTrace: SearchTrace, initAccumulator: false);

        trajectory.Clear();
        trajectory.Add(new ZapTrajectoryStep(state, board.ZobristKey, move, board.SideToMove,
            (short)Math.Clamp(parentEval, short.MinValue + 1, short.MaxValue - 1),
            (short)Math.Clamp(childEval, short.MinValue + 1, short.MaxValue - 1),
            (short)Math.Clamp(childEval - parentEval, short.MinValue + 1, short.MaxValue - 1)));
        foreach (var step in tail.AsSpan()) trajectory.Add(step);
        _sharedState.IncrementNodes(trajectory.Count);
        return score;
    }


    /// <summary>
    /// When an agent changes a line at a junction, the decisions that led into it (the other side's
    /// moves at junction-1, junction-3, ...) were judged against a stale score. Re-rank them so an
    /// agent looks at them again; the TT now carries the new finding, so the revisit sees it.
    /// </summary>
    private void CreditEarlierDecisions(int rootIdx, in ZapRootCandidate candidate, ulong junctionKey, int delta, uint generation)
    {
        if (delta < _params.BackCreditMinCp || _rootBoard.ZobristKey == 0) return;
        ZapTrajectoryStep[] traj = candidate.Trajectory;
        int junction = -1;
        for (int i = 0; i < traj.Length; i++)
        {
            if (traj[i].ZobristKey == junctionKey) { junction = i; break; }
        }
        if (junction < 1) return;

        Span<int> boosts = stackalloc int[junction];
        BackCreditTargets(junction, delta, _params, boosts);
        Board board = _rootBoard;
        if (!board.IsLegal(candidate.Move) || !board.MakeMove(candidate.Move, out _)) return;
        for (int i = 0; i < junction; i++)
        {
            ZapTrajectoryStep step = traj[i];
            if (step.ZobristKey != board.ZobristKey) return;
            int boost = boosts[i];
            if (boost > 0)
            {
                ZapAgentRole role = step.SideToMove == _engineColor ? ZapAgentRole.Promotion : ZapAgentRole.Refutation;
                bool stepPrevCapture = i == 0 ? candidate.Move.IsCapture : traj[i - 1].Move.IsCapture;
                var crosspoint = new ZapCrosspoint(board, rootIdx, i + 1, candidate.Trajectory[^1].StaticEval,
                    (short)Math.Min(short.MaxValue, boost),
                    role == ZapAgentRole.Refutation ? CrosspointKind.AdversarialRefutation : CrosspointKind.Opportunity,
                    Math.Max(1, traj.Length - i), step.Move, _params.CrosspointRootBiasWeight,
                    generation, role, step.ParentEval, stepPrevCapture, _params);
                _workQueue.Reconsider(in crosspoint, boost);
            }
            if (!board.MakeMove(step.Move, out _)) return;
        }
    }

    /// <summary>
    /// Boost per trajectory index for a change at <paramref name="junction"/>: the other side's
    /// decisions at junction-1, junction-3, ... get the delta, decayed per step back.
    /// </summary>
    internal static void BackCreditTargets(int junction, int delta, ZapParams parameters, Span<int> boosts)
    {
        boosts.Clear();
        int boost = delta;
        for (int step = 0, i = junction - 1; step < parameters.BackCreditMaxSteps && i >= 0 && boost > 0; step++, i -= 2)
        {
            boosts[i] = boost;
            boost = boost * parameters.CrosspointDebtDecayPct / 100;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopExploration();
        _disposed = true;
        _isRunning = false;

        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i].Set();
        }

        for (int i = 0; i < _workerCount; i++)
        {
            _threads[i].Join();
            _wakeEvents[i].Dispose();
        }
        _allIdleEvent.Dispose();
    }
}
