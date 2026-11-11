using System.Runtime.CompilerServices;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Data.Nnue;
using Zanak.Time;

namespace Zanak.Search;

/// <summary>
/// Pre-spawned, long-running pool of background worker threads dedicated to exploring shock crosspoints.
/// </summary>
public sealed class ZapWorkerPool : IDisposable
{
    private readonly int _workerCount;
    private readonly Thread[] _threads;
    private readonly AutoResetEvent[] _wakeEvents;
    private readonly AutoResetEvent _allIdleEvent = new(false);
    private readonly ZapWorkQueue _workQueue;
    private readonly ZapSharedState _sharedState;
    private readonly ZapParams _params;
    private readonly ZapTranspositionTable? _tt;
    private readonly IEvaluator _masterEvaluator;
    private readonly ITablebaseProber? _tablebase;

    private volatile bool _disposed;
    private volatile bool _isRunning;
    private int _activeWorkers;
    private Color _engineColor;
    private SearchBudget _currentBudget;

    public int WorkerCount => _workerCount;

    public ZapWorkerPool(
        int workerCount,
        ZapWorkQueue workQueue,
        ZapSharedState sharedState,
        ZapParams parameters,
        IEvaluator masterEvaluator,
        ZapTranspositionTable? tt = null,
        ITablebaseProber? tablebase = null)
    {
        _workerCount = Math.Max(1, workerCount);
        _workQueue = workQueue;
        _sharedState = sharedState;
        _params = parameters;
        _masterEvaluator = masterEvaluator;
        _tt = tt;
        _tablebase = tablebase;

        _threads = new Thread[_workerCount];
        _wakeEvents = new AutoResetEvent[_workerCount];

        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i] = new AutoResetEvent(false);
            int workerId = i;
            _threads[i] = new Thread(() => WorkerLoop(workerId))
            {
                IsBackground = true,
                Name = $"Zanak-Worker-{workerId}",
                Priority = ThreadPriority.AboveNormal
            };
            _threads[i].Start();
        }
    }

    /// <summary>
    /// Signals the worker pool to begin exploring queued crosspoints.
    /// </summary>
    public void StartExploration(Color engineColor, in SearchBudget budget)
    {
        _engineColor = engineColor;
        _currentBudget = budget;
        _isRunning = true;
        _activeWorkers = _workerCount;

        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i].Set();
        }
    }

    /// <summary>
    /// Halts worker exploration and waits for workers to transition to idle.
    /// </summary>
    public void StopExploration()
    {
        _isRunning = false;
        _workQueue.Clear();

        // Wake all threads so they exit work loops if blocked
        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i].Set();
        }
    }

    /// <summary>
    /// Waits until all queued crosspoints have been processed by workers.
    /// </summary>
    public void WaitForCompletion(int timeoutMs = 500)
    {
        while (_isRunning && !_sharedState.IsStopped && !_workQueue.IsEmpty)
        {
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Signals all workers to check for newly queued work.
    /// </summary>
    public void WakeWorkers()
    {
        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i].Set();
        }
    }

    private void WorkerLoop(int workerId)
    {
        // Each worker gets an independent policy and evaluator clone
        var workerPolicy = new ZapPolicy(_params, _tt);
        IEvaluator workerEvaluator = _masterEvaluator is MultiTierNnueEvaluator multiTier
            ? (IEvaluator)multiTier.CreateWorkerInstance()
            : _masterEvaluator;

        while (!_disposed)
        {
            if (!_isRunning)
            {
                _wakeEvents[workerId].WaitOne();
                if (_disposed) break;
            }

            while (_isRunning && !_sharedState.IsStopped)
            {
                if (_workQueue.TryDequeue(out ZapCrosspoint cp, _sharedState, _sharedState.Generation))
                {
                    ProcessCrosspoint(in cp, workerPolicy, workerEvaluator);
                }
                else
                {
                    // No work right now: wait briefly for main thread to enqueue more, or exit if stopped
                    _wakeEvents[workerId].WaitOne(2);
                    if (!_isRunning || _sharedState.IsStopped)
                    {
                        break;
                    }
                }
            }

            if (Interlocked.Decrement(ref _activeWorkers) <= 0)
            {
                _allIdleEvent.Set();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void ProcessCrosspoint(in ZapCrosspoint cp, ZapPolicy policy, IEvaluator evaluator)
    {
        int rootIndex = cp.RootCandidateIndex;
        if (_sharedState.IsRootMoveRefutedOrDead(rootIndex)) return;

        Board board = cp.Board;
        MoveList legalMoves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);

        if (legalMoves.Count == 0) return;

        ZapRootCandidate candidate = _sharedState.GetCandidate(rootIndex);
        int currentScore = candidate.Score;

        ZapTrajectory trajectory = default;

        Span<Move> movesSpan = stackalloc Move[legalMoves.Count];
        Span<int> scoresSpan = stackalloc int[legalMoves.Count];

        for (int i = 0; i < legalMoves.Count; i++)
        {
            Move m = legalMoves[i];
            movesSpan[i] = m;
            scoresSpan[i] = m == cp.OriginalMove ? int.MinValue : policy.ScoreMove(in board, m, cp.Ply);
        }

        // Partial selection sort for top siblings
        int maxToExplore = Math.Min(_params.MaxSiblingExploration, legalMoves.Count);
        for (int i = 0; i < maxToExplore; i++)
        {
            int bestIdx = i;
            for (int j = i + 1; j < legalMoves.Count; j++)
            {
                if (scoresSpan[j] > scoresSpan[bestIdx])
                {
                    bestIdx = j;
                }
            }

            if (bestIdx != i)
            {
                (scoresSpan[i], scoresSpan[bestIdx]) = (scoresSpan[bestIdx], scoresSpan[i]);
                (movesSpan[i], movesSpan[bestIdx]) = (movesSpan[bestIdx], movesSpan[i]);
            }

            if (scoresSpan[i] == int.MinValue) break;

            Move sibling = movesSpan[i];
            if (_sharedState.IsStopped || !_isRunning || _sharedState.IsRootMoveRefutedOrDead(rootIndex)) break;

            Board scratchBoard = board;
            if (!scratchBoard.MakeMove(sibling, out _)) continue;

            _sharedState.IncrementNodes(1);

            int remainingHorizon = Math.Max(2, cp.RemainingHorizon);
            int siblingScore = ZapProbe.Trace(
                ref scratchBoard,
                remainingHorizon,
                policy,
                evaluator,
                _engineColor,
                ref trajectory,
                initialLastMove: sibling,
                parameters: _params,
                leaderScore: currentScore,
                sharedState: _sharedState,
                rootCandidateIndex: rootIndex,
                tablebase: _tablebase,
                searchPly: cp.Ply + 1);

            _sharedState.IncrementNodes(trajectory.Count);

            // Dual-Role Asymmetric Handling:
            if (cp.Role == ZapAgentRole.Refutation)
            {
                // Role A: Refutation Squad (Target: Leader on Opponent's turn)
                // Goal: Find opponent reply that reduces Leader's score below its baseline
                if (siblingScore < currentScore)
                {
                    currentScore = siblingScore;
                    _sharedState.AppendCandidateScoreHistory(rootIndex, $"RefutedBy:{sibling.ToUci()}@ply{cp.Ply}->{siblingScore} ");

                    // If this refutation occurred at ply > 1 (e.g. ply 3 deep down the spine),
                    // but the root candidate still has positive/active status, do not prematurely demote or mark dead!
                    // Only apply full leader refutation if cp.Ply == 1 or if score drops severely:
                    if (_sharedState.TryApplyLeaderRefutation(rootIndex, currentScore, cp.Ply + remainingHorizon, out bool demoted))
                    {
                        // Leader Demoted! RootMoveStatus[rootIndex] is now Refuted.
                        // Break immediately - all further crosspoints for this root move are now evicted!
                        break;
                    }

                    // Cutoff if line drops well below worker prune margin
                    if (currentScore < candidate.Score - _params.WorkerPruneMarginCp)
                    {
                        break;
                    }
                }
            }
            else // ZapAgentRole.Promotion
            {
                // Role B: Promotion Squad (Target: Contenders on Engine's turn)
                // Goal: Find engine alternative tactic beating the baseline and potentially surpassing the Leader
                if (siblingScore > currentScore)
                {
                    currentScore = siblingScore;
                    _sharedState.AppendCandidateScoreHistory(rootIndex, $"PromotedBy:{sibling.ToUci()}@ply{cp.Ply}->{siblingScore} ");
                    if (_sharedState.TryApplyContenderPromotion(rootIndex, currentScore, cp.Ply + remainingHorizon, out bool promotedToLeader))
                    {
                        // Contender surpassed the Leader and took the top spot!
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isRunning = false;

        for (int i = 0; i < _workerCount; i++)
        {
            _wakeEvents[i].Set();
        }

        for (int i = 0; i < _workerCount; i++)
        {
            _threads[i].Join(100);
        }
    }
}
