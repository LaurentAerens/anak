using System.Numerics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Data.Nnue;
using SearchTraceWriter = Anak.Search.SearchTraceWriter;
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
    /// <summary>One-line summary of why the last search stopped and what happened to its crosspoints.</summary>
    public string LastSearchDiagnostics { get; private set; } = string.Empty;

    private readonly ZapParams _params;
    private readonly TimeManager _timeManager;
    private readonly ZapSharedState _sharedState = new();
    private readonly ZapWorkQueue _workQueue;
    private readonly ZapTranspositionTable _tt;
    private ZapWorkerPool _workerPool;
    private IEvaluator _evaluator;
    private readonly ITablebaseProber? _tablebase;

    public ZapParams Parameters => _params;
    public TimeManager TimeManager => _timeManager;
    public ZapTranspositionTable TranspositionTable => _tt;
    public ZapSharedState SharedState => _sharedState;
    public ZapWorkQueue WorkQueue => _workQueue;
    public ITablebaseProber? Tablebase => _tablebase;
    public ZanakNnueEvaluator? NnueEvaluator => _evaluator as ZanakNnueEvaluator;
    public string SearchTraceFile { get; set; } = string.Empty;
    public string PickupTraceFile { get; set; } = string.Empty;
    public double RootFilterMs { get; private set; }
    public long SeedingMs { get; private set; }
    public long FirstBatchMs => _workerPool.FirstBatchMs;
    public int SeededRoots { get; private set; }
    public bool IsWarmedUp => _isWarmedUp;
    private SearchTraceWriter? _searchTrace;
    private readonly object _warmupLock = new();
    private volatile bool _isWarmedUp;

    public ZapSearch(ZapParams? parameters = null, TimeManager? timeManager = null, IEvaluator? evaluator = null, ITablebaseProber? tablebase = null)
    {
        _params = parameters ?? ZapParams.Default;
        _workQueue = new ZapWorkQueue(_params);
        _timeManager = timeManager ?? new TimeManager();
        _tt = new ZapTranspositionTable(_params.DefaultHashMb);

        _evaluator = evaluator is not null && evaluator is not IIncrementalEvaluator
            ? new SynchronizedEvaluator(evaluator)
            : evaluator ?? CreateDefaultEvaluator();
        _tablebase = tablebase ?? CreateDefaultTablebase();
        _workerPool = new ZapWorkerPool(
            _params.DefaultThreads,
            _workQueue,
            _sharedState,
            _params,
            _evaluator,
            _tt,
            _tablebase,
            _timeManager);
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

    public void SetWorkerThreads(int threads)
    {
        threads = Math.Max(1, threads);
        _params.DefaultThreads = threads;
        _workerPool.Dispose();
        _workerPool = new ZapWorkerPool(
            threads,
            _workQueue,
            _sharedState,
            _params,
            _evaluator,
            _tt,
            _tablebase,
            _timeManager);
        _isWarmedUp = false;
    }

    private sealed class SynchronizedEvaluator(IEvaluator inner) : IEvaluator, IDisposable
    {
        private readonly object _lock = new();

        public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves)
        {
            lock (_lock) return inner.Evaluate(in board, legalMoves);
        }

        public void Dispose()
        {
            if (inner is IDisposable disposable) disposable.Dispose();
        }
    }

    private static ZanakNnueEvaluator CreateDefaultEvaluator(string? directory = null)
    {
        if (directory != null)
        {
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"NNUE directory does not exist: {directory}");
            }

            string anakPath = Path.Combine(directory, "anak.nnue");
            using var weights = NnueWeights.LoadFromFile(anakPath);
            if (weights == null || !weights.IsV2)
            {
                throw new InvalidDataException($"Explicit NnuePath requires a valid ArchV2 anak.nnue model: {anakPath}");
            }
        }

        var nnue = new ZanakNnueEvaluator(directory);
        if (nnue.IsActive) return nnue;
        nnue.Dispose();
        throw new InvalidOperationException("Zanak requires an active NNUE. Heuristic evaluation must be explicitly injected for diagnostics.");
    }

    public void SetNnuePath(string directory)
    {
        var replacement = CreateDefaultEvaluator(directory == "<embedded>" ? null : directory);
        Stop();
        _workerPool.Dispose();
        if (_evaluator is IDisposable disposable) disposable.Dispose();
        _evaluator = replacement;
        _workerPool = new ZapWorkerPool(_params.DefaultThreads, _workQueue, _sharedState, _params, _evaluator, _tt, _tablebase, _timeManager);
        Clear();
        _isWarmedUp = false;
    }

    /// <summary>
    /// Ensures static tables, NNUE evaluation, policy, probe, and worker pool paths are JIT-compiled
    /// before search clocks start. Safe to call repeatedly; no-op when already warmed up.
    /// </summary>
    public void EnsureWarmedUp()
    {
        if (_isWarmedUp) return;
        lock (_warmupLock)
        {
            if (_isWarmedUp) return;
            Warmup();
        }
    }

    /// <summary>
    /// Explicitly warms up static bitboard tables, NNUE evaluators, greedy policy, probe tracing,
    /// work queue, and worker threads while preserving diagnostic trace files and leaving search state clean.
    /// </summary>
    public void Warmup()
    {
        lock (_warmupLock)
        {
            string prevTrace = SearchTraceFile;
            string prevPickupTrace = PickupTraceFile;
            SearchTraceFile = string.Empty;
            PickupTraceFile = string.Empty;

            try
            {
                ZapTables.EnsureInitialized();
                RuntimeHelpers.RunClassConstructor(typeof(Bitboard).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(MagicBitboards).TypeHandle);

                Board startBoard = Fen.Parse(Fen.StartPosition);
                _evaluator.Evaluate(in startBoard, ReadOnlySpan<Move>.Empty);

                var warmupBudget = new SearchBudget(
                    SoftLimitMs: 30,
                    HardLimitMs: 250,
                    TargetBaseDepth: 2,
                    MaxDepthHorizon: 3);
                Search(in startBoard, in warmupBudget, cancellationToken: default);

                var warmupPolicy = new ZapPolicy(_params);
                ReadOnlySpan<string> warmupFens =
                [
                    "4k3/2b5/8/8/4Q3/8/8/4K3 b - - 0 1",
                    "3r3k/4P3/8/8/8/8/8/4K3 w - - 0 1",
                    "8/8/8/8/4k3/8/4P3/4K3 w - - 0 1"
                ];

                foreach (string fen in warmupFens)
                {
                    Board board = Fen.Parse(fen);
                    if (_evaluator is ZanakNnueEvaluator nnue)
                    {
                        nnue.InitSearch(in board);
                    }
                    else if (_evaluator is IIncrementalEvaluator incremental)
                    {
                        incremental.InitSearch(in board);
                    }

                    _evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);

                    if (warmupPolicy.TryGetQuickMove(in board, 0, out Move quickMove) && !quickMove.IsNull)
                    {
                        Piece movedPiece = board.GetPieceAt(quickMove.From);
                        if (board.MakeMove(quickMove, out BoardState state, movedPiece))
                        {
                            if (_evaluator is IIncrementalEvaluator incrementalAfter)
                            {
                                incrementalAfter.PushMove(quickMove, movedPiece, state.CapturedPiece, in board);
                            }

                            _evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
                        }
                    }
                }
            }
            finally
            {
                SearchTraceFile = prevTrace;
                PickupTraceFile = prevPickupTrace;
                Clear();
                _workerPool.ResetDiagnostics();
                _workerPool.SpineTree.Reset(0);
                RootFilterMs = 0;
                SeedingMs = 0;
                SeededRoots = 0;
                LastSearchDiagnostics = string.Empty;
                _isWarmedUp = true;
            }
        }
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
        using var trace = SearchTraceWriter.Open(SearchTraceFile, rootBoard.ZobristKey);
        using var diagnostics = string.IsNullOrWhiteSpace(PickupTraceFile) ? null : new ZapDiagnosticsWriter(PickupTraceFile);

        _workerPool.StopExploration();
        _searchTrace = trace;
        _workerPool.SearchTrace = trace;
        if (diagnostics != null)
        {
            diagnostics.Start(rootBoard.ZobristKey, _params);
            _workQueue.Picked += diagnostics.Pickup;
            _workerPool.BatchCompleted += diagnostics.Batch;
        }
        try
        {
            return SearchCore(in rootBoard, in budget, infoCallback, cancellationToken);
        }
        finally
        {
            // Persistent agents must be idle before the file is synchronously flushed and closed.
            _workerPool.StopExploration();
            _timeManager.Stop();
            _workerPool.SearchTrace = null;
            _searchTrace = null;
            if (diagnostics != null)
            {
                _workQueue.Picked -= diagnostics.Pickup;
                _workerPool.BatchCompleted -= diagnostics.Batch;
                diagnostics.Summary(RootFilterMs, SeedingMs, FirstBatchMs, SeededRoots, LastSearchDiagnostics);
            }
        }
    }

    private bool MakeSearchMove(ref Board board, Move move)
    {
        ulong from = board.ZobristKey;
        if (!board.MakeMove(move, out _)) return false;
        _searchTrace?.WriteEdge(from, board.ZobristKey, move);
        return true;
    }

    private Move SearchCore(
        in Board rootBoard,
        in SearchBudget budget,
        Action<ZapSearchStats>? infoCallback,
        CancellationToken cancellationToken)
    {
        _timeManager.Start();
        _workerPool.StopExploration();
        _workQueue.Clear();
        _workerPool.ResetDiagnostics();
        LastSearchDiagnostics = string.Empty;
        RootFilterMs = 0;
        SeedingMs = 0;
        SeededRoots = 0;
        _sharedState.ResetSpliceDiagnostics();
        _tt.Clear();

        // 1. Root move generation
        MoveList legalMoves = default;
        MoveGenerator.GenerateLegalMoves(in rootBoard, ref legalMoves);

        if (legalMoves.Count == 0)
        {
            return Move.Null;
        }

        // Fast-path: only 1 legal move -> return immediately (still emit valid depth >= 1 info)
        if (legalMoves.Count == 1)
        {
            _sharedState.Reset([legalMoves[0]]);
            EmitInfo(infoCallback, 1);
            return legalMoves[0];
        }
        if (_timeManager.ShouldStop(in budget, cancellationToken))
            return legalMoves[0];

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

        _tt.NewSearch();

        Color engineColor = rootBoard.SideToMove;
        var rootPolicy = new ZapPolicy(_params);

        // NNUE expert is chosen ONCE at the root and fixed for the whole search (master + all workers),
        // so eval deltas along spines and across agents are always measured with the same network.
        if (_evaluator is ZanakNnueEvaluator rootNnue)
        {
            rootNnue.SetRootTopology(in rootBoard);
        }
        _workerPool.SetRootBoard(in rootBoard);

        // Quick removal of clearly bad 1-ply moves (null-move baseline) before any spine is generated,
        // then sort surviving root moves so time-gated Phase 1 seeding never truncates strong candidates.
        Move[] movesArray = legalMoves.AsSpan().ToArray();
        Span<bool> keepRoot = stackalloc bool[movesArray.Length];
        Span<int> rootLoss = stackalloc int[movesArray.Length];
        long filterStart = Stopwatch.GetTimestamp();
        int rejectedRoots = ZapRootFilter.Filter(in rootBoard, movesArray, keepRoot, _params.RootFilterMarginCp, rootLoss, out int rootNullLoss);
        RootFilterMs = Stopwatch.GetElapsedTime(filterStart).TotalMilliseconds;
        int survivingRoots = movesArray.Length - rejectedRoots;

        Span<int> rootOrderScore = stackalloc int[movesArray.Length];
        for (int i = 0; i < movesArray.Length; i++)
        {
            Move m = movesArray[i];
            bool keep = keepRoot[i];
            int s = keep ? 1_000_000 : 0;
            if (m.IsPromotion) s += 20_000;
            if (m.IsCapture)
            {
                int see = StaticExchangeEvaluator.Evaluate(in rootBoard, m);
                s += see >= 0 ? 10_000 + see : see;
            }
            else
            {
                if (rootNullLoss >= 100) s += Math.Max(0, rootNullLoss - rootLoss[i]) * 10;
                s += ZapPolicy.ScoreQuietCentrality(m);
            }
            if (rootBoard.GivesCheck(m)) s += 5_000;

            int pos = i;
            while (pos > 0 && rootOrderScore[pos - 1] < s)
            {
                rootOrderScore[pos] = rootOrderScore[pos - 1];
                movesArray[pos] = movesArray[pos - 1];
                keepRoot[pos] = keepRoot[pos - 1];
                pos--;
            }
            rootOrderScore[pos] = s;
            movesArray[pos] = m;
            keepRoot[pos] = keep;
        }

        _sharedState.Reset(movesArray);
        _workerPool.SpineTree.Reset(movesArray.Length);
        uint generation = _sharedState.NextGeneration();
        if (_timeManager.ShouldStop(in budget, cancellationToken))
            return _sharedState.GetBestMove();

        // -------------------------------------------------------------
        // Phase 1: Root Multi-Spine Seeding (The Anytime Guarantee)
        // -------------------------------------------------------------
        int baseDepth = budget.TargetBaseDepth;
        _sharedState.SetCompletedDepth(baseDepth);

        // Phase 1 Time Gating: Reserve at least 60% of the soft time budget for the Worker Pool!
        long maxPhase1Ms = Math.Max(5, (long)(budget.SoftLimitMs * 0.40));

        SearchBudget effectiveBudget = budget;
        if (budget.ExtendHorizonWithTime && survivingRoots is >= 2 and <= 4)
        {
            double scale = survivingRoots switch
            {
                2 => _timeManager.Parameters.SurvivorSoftScale2,
                3 => _timeManager.Parameters.SurvivorSoftScale3,
                _ => _timeManager.Parameters.SurvivorSoftScale4
            };
            int scaledSoft = Math.Clamp((int)(budget.SoftLimitMs * scale), 1, budget.HardLimitMs);
            effectiveBudget = budget with { SoftLimitMs = scaledSoft };
            maxPhase1Ms = Math.Max(5, (long)(effectiveBudget.SoftLimitMs * 0.40));
        }

        if (_params.InterleaveRootSeeding)
            _workerPool.StartExploration(engineColor, effectiveBudget, runOnCaller: true);

        for (int i = 0; i < movesArray.Length; i++)
        {
            if (_timeManager.ShouldStop(in budget, cancellationToken))
            {
                _sharedState.RequestStop();
                break;
            }

            if (!keepRoot[i])
            {
                _sharedState.UpdateCandidateBaseline(i, -30000, Move.Null);
                _sharedState.UpdateCandidateTrajectory(i, ReadOnlySpan<ZapTrajectoryStep>.Empty);
                _sharedState.SetRootMoveStatus(i, RootMoveStatus.Dead);
                continue;
            }

            Move rootMove = movesArray[i];
            Board scratchBoard = rootBoard;
            if (!MakeSearchMove(ref scratchBoard, rootMove)) continue;
            Board preTraceScratchBoard = scratchBoard;

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
                searchPly: 1,
                tt: _tt,
                searchTrace: _searchTrace);

            // Opponent immediate draw resolution: if opponent has any legal reply triggering 3-fold repetition, 50-move draw, or insufficient material,
            // cap an advantageous score at 0 cp so winning positions are not thrown away into an opponent-forced draw.
            bool allowsImmediateDraw = AllowsOpponentImmediateDraw(in preTraceScratchBoard, in opponentReplies);

            // 2-Fold Repetition Penalty with game history:
            // If playing rootMove steps into a 2-fold repetition with past game history and engine is ahead,
            // cap score to 0 (draw) to ensure winning positions are not thrown into a repetition cycle.
            if (preTraceScratchBoard.HalfmoveClock >= 4 && preTraceScratchBoard.RepetitionCount() == 1)
            {
                if (terminalScore > 0)
                {
                    terminalScore = 0;
                }
            }

            _sharedState.IncrementNodes(trajectory.Count);

            Move followup = trajectory.Count > 0 ? trajectory[0].Move : Move.Null;
            if (allowsImmediateDraw)
            {
                terminalScore = Math.Min(terminalScore, 0);
            }

            _sharedState.UpdateCandidateBaseline(i, terminalScore, followup);
            if (allowsImmediateDraw)
            {
                _sharedState.ApplyAgentRefutation(i, terminalScore, 1);
            }
            _workerPool.SpineTree.SetRootSpine(i, trajectory.AsSpan(), terminalScore);
            SeededRoots++;

            // Extract immediate opponent reply crosspoint (Ply 1) for every surviving root move!
            // If the root move captured or jumped in evaluation, any opponent reply is queued for refutation.
            if (trajectory.Count > 0)
            {
                ZapTrajectoryStep firstStep = trajectory[0];
                if (firstStep.SideToMove != engineColor)
                {
                    int shock = Math.Abs((int)firstStep.EvalDelta);
                    // If root move caused an eval jump or was a capture, give high shock to force immediate refutation check
                    if (rootMove.IsCapture || Math.Abs(terminalScore) > 100)
                    {
                        shock = Math.Max(shock, 200);
                    }

                    _workQueue.Enqueue(new ZapCrosspoint(
                        board: preTraceScratchBoard,
                        rootCandidateIndex: i,
                        ply: 1,
                        baselineScore: (short)Math.Clamp(terminalScore, short.MinValue + 1, short.MaxValue - 1),
                        shockMagnitude: (short)Math.Min(shock, short.MaxValue),
                        kind: CrosspointKind.AdversarialRefutation,
                        remainingHorizon: Math.Max(1, trajectory.Count),
                        originalMove: firstStep.Move,
                        rootBiasWeight: _params.CrosspointRootBiasWeight,
                        generation: generation,
                        role: ZapAgentRole.Refutation,
                        parentEval: firstStep.ParentEval,
                        prevWasCapture: rootMove.IsCapture,
                        parameters: _params
                    ));
                }
            }
            // Seed one root, then spend at most one batch challenging already published roots.
            if (_params.InterleaveRootSeeding &&
                _workQueue.TryDequeue(out ZapCrosspoint seedCrosspoint, _sharedState, generation))
            {
                _workerPool.ProcessCrosspoint(in seedCrosspoint, rootPolicy, _evaluator);
            }
        }
        SeedingMs = _timeManager.ElapsedMilliseconds;
        if (_params.InterleaveRootSeeding) _workerPool.StopExploration();

        // Sort candidates descending by baseline eval and prune dead stems
        _sharedState.SortCandidatesAndPrune(_params.DeadStemPruneMarginCp);

        // Emit initial anytime telemetry
        EmitInfo(infoCallback, baseDepth);

        // Anytime Contract: valid BestMove is now guaranteed at all subsequent moments!
        if (cancellationToken.IsCancellationRequested || _sharedState.IsStopped)
        {
            _timeManager.Stop();
            return _sharedState.GetBestMove();
        }

        // Fast-path when ply-1 pruning leaves only 1 surviving move on a clock budget
        if (effectiveBudget.ExtendHorizonWithTime && survivingRoots == 1)
        {
            _timeManager.Stop();
            ZapRootCandidate sole = _sharedState.GetBestCandidate();
            _tt.Store(rootBoard.ZobristKey, sole.Move, (byte)Math.Min(255, _sharedState.CompletedDepth), (short)sole.Score);
            LastSearchDiagnostics =
                $"zap exit single-survivor drain skipped depth {_sharedState.CompletedDepth} time {_timeManager.ElapsedMilliseconds}/{effectiveBudget.SoftLimitMs}ms " +
                $"seeded {SeededRoots}/{movesArray.Length} seed-ms {SeedingMs} root-filter-ms {RootFilterMs:F2}";
            return sole.Move;
        }

        int liveStems = 0;
        for (int i = 0; i < _sharedState.CandidateCount; i++)
        {
            ZapRootCandidate c = _sharedState.GetCandidate(i);
            if (!c.IsDeadStem && c.Status == RootMoveStatus.Active) liveStems++;
        }

        // -------------------------------------------------------------
        // Phase 2 & 3: Gated Spine Qualification & Dual-Role Exploration
        // -------------------------------------------------------------
        // Every position is eligible; role and tactical features determine which agent picks it up.
        for (int i = 0; i < _sharedState.CandidateCount; i++)
        {
            ZapRootCandidate contender = _sharedState.GetCandidate(i);
            // Dead stems keep their crosspoints; their low spine score ranks them low for pickup.
            if (contender.Trajectory.Length == 0)
            {
                continue;
            }

            Board startBoard = rootBoard;
            if (startBoard.MakeMove(contender.Move, out _))
                _workQueue.EnqueueTrajectory(in startBoard, i, contender.Trajectory, engineColor, generation,
                    prevWasCapture: contender.Move.IsCapture);
        }

        // Start worker pool to explore queued crosspoints
        // A traced single-thread search must not interleave agent probes with the caller's probes.
        _workerPool.StartExploration(engineColor, effectiveBudget, runOnCaller: _searchTrace != null && _params.DefaultThreads == 1);

        // Iterative deepening & monitoring loop. With a clock budget the horizon keeps growing while time
        // remains; deeper spines re-open their crosspoints with longer agent horizons.
        int currentIterDepth = baseDepth + 1;
        int threadScaledCap = Math.Min(
            _timeManager.Parameters.ExtendedHorizonMaxPlies,
            18 + Math.Min(14, Math.Max(1, _params.DefaultThreads) * 3));
        int depthCap = effectiveBudget.ExtendHorizonWithTime
            ? Math.Max(effectiveBudget.MaxDepthHorizon, threadScaledCap)
            : effectiveBudget.MaxDepthHorizon;
        int prevLeaderIdx = _sharedState.BestCandidateIndex;
        int leaderBaseCrosspoints = _sharedState.GetRootCompletedCrosspoints(prevLeaderIdx);
        bool forceDeepen = false;
        bool earlyExit = false;
        long lastInfoMs = 0;
        long lastDeepenMs = _timeManager.ElapsedMilliseconds;
        string exitReason = "depth-cap";
        Span<int> contenderIdx = stackalloc int[256];
        Span<int> contenderScore = stackalloc int[256];
        while (currentIterDepth <= depthCap && !_sharedState.IsStopped && !cancellationToken.IsCancellationRequested)
        {
            // Periodic polling
            if (_timeManager.ShouldStop(in effectiveBudget, cancellationToken))
            {
                exitReason = "hard-limit";
                _sharedState.RequestStop();
                break;
            }

            ZapRootCandidate currentLeader = _sharedState.GetBestCandidate(out int leaderIdx);
            _sharedState.SetCompletedDepth(currentLeader.CompletedDepth);

            // If a new Leader was promoted, immediately dispatch Refutation Squad on the new Leader!
            if (leaderIdx != prevLeaderIdx && !_sharedState.IsRootMoveRefutedOrDead(leaderIdx))
            {
                Board newLeaderBoard = rootBoard;
                if (MakeSearchMove(ref newLeaderBoard, currentLeader.Move))
                {
                    if ((newLeaderBoard.HalfmoveClock >= 4 && (newLeaderBoard.IsFiftyMoveRule() || newLeaderBoard.IsThreefoldRepetition() || newLeaderBoard.IsRepetition(1))) ||
                        newLeaderBoard.HasInsufficientMaterial())
                    {
                        // Leader is an immediate terminal draw: score remains 0, cannot be refuted or deepened
                        prevLeaderIdx = leaderIdx;
                        continue;
                    }

                    Board newLeaderStart = newLeaderBoard;
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
                        tablebase: _tablebase,
                        searchTrace: _searchTrace);

                    // Check if new leader allows immediate 3-fold draw by opponent on reply 1
                    MoveList newLeaderReplies = default;
                    MoveGenerator.GenerateLegalMoves(in newLeaderStart, ref newLeaderReplies);
                    if (AllowsOpponentImmediateDraw(in newLeaderStart, in newLeaderReplies))
                    {
                        newLeaderScore = Math.Min(newLeaderScore, 0);
                    }

                    _sharedState.IncrementNodes(newLeaderTraj.Count);
                    _sharedState.AppendCandidateScoreHistory(leaderIdx, $"LeaderPromoteDeepen(d={currentIterDepth})->{newLeaderScore} ");
                    _workerPool.SpineTree.SetRootSpine(leaderIdx, newLeaderTraj.AsSpan(), newLeaderScore);

                    if (!_sharedState.IsRootMoveRefutedOrDead(leaderIdx))
                    {
                        ExtractCrosspoints(
                            in rootBoard,
                            currentLeader.Move,
                            leaderIdx,
                            newLeaderTraj.AsSpan(),
                            engineColor,
                            generation,
                            ZapAgentRole.Refutation);
                        _workerPool.WakeWorkers();
                    }
                }
                prevLeaderIdx = _sharedState.BestCandidateIndex;
                leaderBaseCrosspoints = _sharedState.GetRootCompletedCrosspoints(prevLeaderIdx);
            }
            ZapRootCandidate contender = _sharedState.GetSecondBestCandidate();

            bool isSmallSurvivorSet = survivingRoots <= 4 || liveStems <= 4;
            double minSoftFraction = isSmallSurvivorSet
                ? _timeManager.Parameters.SmallSurvivorMinSoftFraction
                : _timeManager.Parameters.StableDominanceMinSoftFraction;

            // Check Stable Dominance early break (only after a minimum share of the soft budget is spent)
            if (_timeManager.ElapsedMilliseconds >= (long)(effectiveBudget.SoftLimitMs * minSoftFraction) &&
                _timeManager.CheckStableDominance(currentLeader.CompletedDepth, currentLeader.Score, contender.Score, currentLeader.Move.RawValue))
            {
                exitReason = "stable-dominance";
                earlyExit = true;
                break;
            }

            // Dominant-Leader Verification + Contender Quick-Check:
            // When one spine leads by >= DominantLeaderLeadCp and survives N refutation crosspoints without being dethroned,
            // briefly check non-leader surviving heaps; if none closes the gap, bank the remaining time.
            if (effectiveBudget.ExtendHorizonWithTime &&
                _timeManager.ElapsedMilliseconds >= (long)(effectiveBudget.SoftLimitMs * minSoftFraction))
            {
                int leaderVerifiedCount = _sharedState.GetRootCompletedCrosspoints(leaderIdx) - leaderBaseCrosspoints;
                int verifyThreshold = isSmallSurvivorSet
                    ? _timeManager.Parameters.SmallSurvivorVerifyCrosspoints
                    : _timeManager.Parameters.LeaderVerifyCrosspoints;

                if (leaderVerifiedCount >= verifyThreshold &&
                    currentLeader.Score - contender.Score >= _timeManager.Parameters.DominantLeaderLeadCp)
                {
                    int quickCheckBudget = Math.Clamp(
                        Math.Max(1, survivingRoots - 1) * _timeManager.Parameters.ContenderQuickCheckPerRoot,
                        3,
                        12);
                    bool dethronedOrThreatened = false;

                    for (int q = 0; q < quickCheckBudget; q++)
                    {
                        if (_timeManager.ShouldStop(in effectiveBudget, cancellationToken) ||
                            _sharedState.IsStopped)
                        {
                            break;
                        }
                        if (!_workQueue.TryDequeue(out ZapCrosspoint qcp, _sharedState, generation, excludedRoot: leaderIdx))
                        {
                            break;
                        }
                        _workerPool.ProcessCrosspoint(in qcp, rootPolicy, _evaluator);

                        ZapRootCandidate afterLeader = _sharedState.GetBestCandidate(out int afterLeaderIdx);
                        ZapRootCandidate afterContender = _sharedState.GetSecondBestCandidate();
                        if (afterLeaderIdx != leaderIdx ||
                            afterLeader.Score - afterContender.Score < _timeManager.Parameters.DominantLeaderLeadCp)
                        {
                            dethronedOrThreatened = true;
                            prevLeaderIdx = afterLeaderIdx;
                            leaderBaseCrosspoints = _sharedState.GetRootCompletedCrosspoints(afterLeaderIdx);
                            break;
                        }
                    }

                    if (!dethronedOrThreatened && !_sharedState.IsStopped && !cancellationToken.IsCancellationRequested)
                    {
                        exitReason = "dominant-leader-verified";
                        earlyExit = true;
                        break;
                    }
                }
            }

            // Check Soft time limit exit (with panic extension check). A clock budget stops at the soft limit;
            // otherwise remaining queued work may run on to the hard limit.
            if (_timeManager.HasReachedSoftLimit(in effectiveBudget, currentLeader.Score, contender.Score))
            {
                if (effectiveBudget.ExtendHorizonWithTime || _workQueue.IsEmpty || _timeManager.ElapsedMilliseconds >= effectiveBudget.HardLimitMs)
                {
                    exitReason = "soft-limit";
                    break;
                }
            }

            // If work queue is emptying and time remains, deepen the top candidates and extract deeper crosspoints!
            // Subspines keep the queue full, so with a clock budget the horizon also grows on a time schedule.
            bool deepened = false;
            bool deepenDue = effectiveBudget.ExtendHorizonWithTime &&
                _timeManager.ElapsedMilliseconds - lastDeepenMs >= Math.Max(10, effectiveBudget.SoftLimitMs / Math.Max(1, depthCap - baseDepth));
            if (_workQueue.Count < 4 || forceDeepen || deepenDue)
            {
                deepened = true;
                lastDeepenMs = _timeManager.ElapsedMilliseconds;
                _tt.Store(rootBoard.ZobristKey, currentLeader.Move, (byte)Math.Min(255, currentIterDepth), (short)currentLeader.Score);

                // Deepen Leader (Refutation Squad)
                currentLeader = _sharedState.GetBestCandidate(out leaderIdx);
                if (!_sharedState.IsRootMoveRefutedOrDead(leaderIdx))
                {
                    Board leaderBoard = rootBoard;
                    if (MakeSearchMove(ref leaderBoard, currentLeader.Move) &&
                        // Leader that is an immediate terminal draw keeps score 0 and is not deepened
                        !((leaderBoard.HalfmoveClock >= 4 && (leaderBoard.IsFiftyMoveRule() || leaderBoard.IsThreefoldRepetition() || leaderBoard.IsRepetition(1))) ||
                          leaderBoard.HasInsufficientMaterial()))
                    {
                        Board leaderStart = leaderBoard;
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
                            sharedState: _sharedState,
                            tablebase: _tablebase,
                            searchTrace: _searchTrace);

                        MoveList leaderReplies = default;
                        MoveGenerator.GenerateLegalMoves(in leaderStart, ref leaderReplies);
                        if (AllowsOpponentImmediateDraw(in leaderStart, in leaderReplies))
                        {
                            newScore = Math.Min(newScore, 0);
                        }

                        _sharedState.IncrementNodes(deeperTraj.Count);
                        _sharedState.AppendCandidateScoreHistory(leaderIdx, $"LeaderEmptyQueueDeepen(d={currentIterDepth})->{newScore} ");
                        _workerPool.SpineTree.SetRootSpine(leaderIdx, deeperTraj.AsSpan(), newScore);

                        ExtractCrosspoints(
                            in rootBoard,
                            currentLeader.Move,
                            leaderIdx,
                            deeperTraj.AsSpan(),
                            engineColor,
                            generation,
                            ZapAgentRole.Refutation);
                        _workerPool.WakeWorkers();
                    }
                }

                // Deepen Qualified Contenders (Promotion Squad). Candidate indices are stable (not sorted),
                // so pick the best-scoring live contenders; at the horizon cap widen to every live candidate.
                bool widen = budget.ExtendHorizonWithTime && currentIterDepth >= depthCap;
                int candidateCount = _sharedState.CandidateCount;
                int contenderCount = 0;
                for (int c = 0; c < candidateCount; c++)
                {
                    if (c == leaderIdx) continue;
                    ZapRootCandidate cand = _sharedState.GetCandidate(c);
                    if (cand.IsDeadStem || cand.Status != RootMoveStatus.Active) continue;
                    int pos = contenderCount++;
                    while (pos > 0 && contenderScore[pos - 1] < cand.Score)
                    {
                        contenderIdx[pos] = contenderIdx[pos - 1];
                        contenderScore[pos] = contenderScore[pos - 1];
                        pos--;
                    }
                    contenderIdx[pos] = c;
                    contenderScore[pos] = cand.Score;
                }
                int maxCandidatesToCheck = widen ? contenderCount : Math.Min(3, contenderCount);
                for (int k = 0; k < maxCandidatesToCheck; k++)
                {
                    int c = contenderIdx[k];
                    ZapRootCandidate cand = _sharedState.GetCandidate(c);

                    Board candBoard = rootBoard;
                    if (!MakeSearchMove(ref candBoard, cand.Move)) continue;

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
                        sharedState: _sharedState,
                        tablebase: _tablebase,
                        searchTrace: _searchTrace);

                    _sharedState.IncrementNodes(deeperTraj.Count);
                    _workerPool.SpineTree.SetRootSpine(c, deeperTraj.AsSpan(), newScore);

                    ExtractCrosspoints(
                        in rootBoard,
                        cand.Move,
                        c,
                        deeperTraj.AsSpan(),
                        engineColor,
                        generation,
                        ZapAgentRole.Promotion);
                    _workerPool.WakeWorkers();
                }
            }

            if (deepened || !budget.ExtendHorizonWithTime || _timeManager.ElapsedMilliseconds - lastInfoMs >= 250)
            {
                EmitInfo(infoCallback, currentIterDepth);
                lastInfoMs = _timeManager.ElapsedMilliseconds;
            }
            if (_timeManager.ElapsedMilliseconds >= budget.HardLimitMs - 15)
            {
                exitReason = "hard-limit";
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

            if (!budget.ExtendHorizonWithTime || deepened)
            {
                currentIterDepth++;
            }
            // Unpicked but ineligible entries (stale generation, refuted root) keep Count high; deepen anyway.
            forceDeepen = processed == 0 && !deepened;
        }

        // Spine exploration draining: if time remains below soft limit, drain pending crosspoints
        // unless we exited early to bank clock time.
        if (exitReason == "depth-cap" && (_sharedState.IsStopped || cancellationToken.IsCancellationRequested)) exitReason = "stopped";
        string drainExit = earlyExit ? "skipped" : "soft-limit";
        while (!earlyExit && !_sharedState.IsStopped && !_timeManager.HasReachedSoftLimit(in effectiveBudget, _sharedState.GetBestCandidate().Score, _sharedState.GetSecondBestCandidate().Score))
        {
            if (_workQueue.TryDequeue(out ZapCrosspoint cp, _sharedState, generation))
            {
                _workerPool.ProcessCrosspoint(in cp, rootPolicy, _evaluator);
            }
            else
            {
                _workerPool.WaitForIdle(5);
                if (_workQueue.IsEmpty && !_workerPool.HasInFlightWork)
                {
                    drainExit = "queue-empty";
                    break;
                }
            }
        }

        _workerPool.StopExploration();
        _timeManager.Stop();
        LastSearchDiagnostics =
            $"zap exit {exitReason} drain {drainExit} depth {currentIterDepth - 1} time {_timeManager.ElapsedMilliseconds}/{effectiveBudget.SoftLimitMs}ms " +
            $"agents {_workQueue.DequeuedCount} improved {_workerPool.AgentsImproved} spliced {_workerPool.AgentsSpliced} " +
            $"entries new {_workQueue.NewEntryCount} merged {_workQueue.MergedEntryCount} reopened {_workQueue.ReopenedEntryCount} " +
            $"subspine-positions {_workerPool.SubspinePositions} tree-nodes {_workerPool.SpineTree.NodeCount} " +
            $"entries {_workQueue.TotalEntries} capped {_workQueue.CapacityRejectedCount} " +
            $"evicted-stale {_workQueue.EvictedStaleCount} pending {_workQueue.Count}";
        LastSearchDiagnostics +=
            $" seeded {SeededRoots}/{movesArray.Length} seed-ms {SeedingMs} root-filter-ms {RootFilterMs:F2}" +
            $" first-batch-ms {FirstBatchMs} completed {_workerPool.CompletedCrosspoints} siblings {_workerPool.SiblingsTraced}";

        // Re-sort non-refuted, non-dead candidates to ensure the highest score candidate is chosen
        _sharedState.SortCandidatesAndPrune(_params.RootKillMarginCp);

        ZapRootCandidate finalBest = _sharedState.GetBestCandidate();
        _tt.Store(rootBoard.ZobristKey, finalBest.Move, (byte)Math.Min(255, _sharedState.CompletedDepth), (short)finalBest.Score);

        EmitInfo(infoCallback, _sharedState.CompletedDepth);

        return finalBest.Move;
    }

    private static bool AllowsOpponentImmediateDraw(in Board positionAfterMove, in MoveList opponentReplies)
    {
        for (int r = 0; r < opponentReplies.Count; r++)
        {
            Board replyBoard = positionAfterMove;
            if (replyBoard.MakeMove(opponentReplies[r], out _) &&
                ((replyBoard.HalfmoveClock >= 4 && (replyBoard.IsThreefoldRepetition() || replyBoard.IsFiftyMoveRule())) ||
                 replyBoard.HasInsufficientMaterial()))
            {
                return true;
            }
        }
        return false;
    }

    internal void ExtractCrosspoints(
        in Board rootBoard,
        Move rootMove,
        int candidateIndex,
        ReadOnlySpan<ZapTrajectoryStep> steps,
        Color engineColor,
        uint generation,
        ZapAgentRole role)
    {
        Board spineBoard = rootBoard;
        if (!spineBoard.MakeMove(rootMove, out _)) return;

        _workQueue.EnqueueTrajectory(in spineBoard, candidateIndex, steps, engineColor, generation,
            role: role, immediateOnly: false, prevWasCapture: rootMove.IsCapture);
    }

    private void EmitInfo(Action<ZapSearchStats>? infoCallback, int depth)
    {
        if (infoCallback == null) return;

        ZapRootCandidate best = _sharedState.GetBestCandidate();
        long elapsed = Math.Max(1, _timeManager.ElapsedMilliseconds);
        long nodes = _sharedState.TotalNodes;
        long nps = (nodes * 1000) / elapsed;

        var pv = new Move[1 + best.Trajectory.Length];
        pv[0] = best.Move;
        for (int i = 0; i < best.Trajectory.Length; i++)
        {
            pv[1 + i] = best.Trajectory[i].Move;
        }
        depth = Math.Max(1, best.CompletedDepth > 0 ? best.CompletedDepth : depth);

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

    /// <summary>
    /// Requests the active search and worker pool to stop as soon as possible.
    /// </summary>
    public void Stop()
    {
        _sharedState.RequestStop();
        _workerPool.StopExploration();
    }

    /// <summary>
    /// Stops any active search and disposes worker threads, transposition table, and evaluator resources.
    /// </summary>
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
