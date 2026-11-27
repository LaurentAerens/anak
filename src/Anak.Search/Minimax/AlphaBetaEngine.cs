using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Ultra-high-performance, zero-allocation Alpha-Beta (Negamax) chess search engine.
/// Features:
/// - Principal Variation Search (PVS) with fail-soft negamax
/// - Iterative Deepening from depth 1 to MaxSearchDepth (128)
/// - Aspiration Windows for depth >= 4 with adaptive widening
/// - 64-byte cacheline Transposition Table probing and recording with mate-distance score conversion
/// - Pruning & Selectivity:
///   - Mate distance pruning
///   - Threefold repetition & 50-move rule draw detection
///   - QuiescenceSearch at leaf nodes (depth <= 0)
///   - Reverse Futility Pruning (Static Null Move) for depth <= 4
///   - Adaptive Null Move Pruning (NMP) with zugzwang verification
///   - Singular Extensions (+1 or +2 plies)
///   - Check Extensions (+1 ply)
///   - Topology-adaptive Late Move Reductions (LMR) with history/killer adjustments
/// - Move ordering via staged MovePicker (TT move, good captures, killers, countermoves, continuation history, butterfly quiets, bad captures)
/// - Complete history updates (butterfly, 1-ply & 2-ply continuation, killers, capture history, correction history)
/// - Dynamic time management via TimeManager and cancellation token polling
/// - Lock-free Lazy SMP multi-threaded search sharing the Transposition Table
/// </summary>
[SkipLocalsInit]
public sealed class AlphaBetaEngine : ISearchEngine, IDisposable
{
    public const int MaxSearchDepth = 128;
    public const int MaxStackSize = 256;

    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;
    private readonly TimeManager _timeManager;

    private int _threads = 1;
    internal volatile bool _stopped;
    private SearchWorker[] _workers;
    private Board _sharedSearchBoard;
    private SearchLimits _sharedSearchLimits;
    private CancellationToken _sharedSearchToken;
    private int _activeHelpers;
    private readonly ManualResetEventSlim _helpersDoneEvent = new(true);

    private int _lmrBase = SearchHeuristics.TunedLmrBase;
    private int _lmrDivision = SearchHeuristics.TunedLmrDivision;
    private int _rfpMargin = SearchHeuristics.TunedRfpMargin;
    private int _nmpBase = SearchHeuristics.TunedNmpBase;
    private int _aspirationDelta = SearchHeuristics.TunedAspirationDelta;
    private int _conthistReward = SearchHeuristics.TunedConthistReward;
    private int _conthistPenalty = SearchHeuristics.TunedConthistPenalty;
    private int _historyThreshold = SearchHeuristics.TunedHistoryThreshold;
    private int _rfpImprovingMultiplier = SearchHeuristics.TunedRfpImprovingMultiplier;
    private int _singularMargin = SearchHeuristics.TunedSingularMargin;
    private int _qsDeltaMargin = SearchHeuristics.TunedQsDeltaMargin;
    private int _futilityMarginD1D2 = SearchHeuristics.TunedFutilityMarginD1D2;
    private int _futilityMarginD3 = SearchHeuristics.TunedFutilityMarginD3;
    private int _lmpBase = SearchHeuristics.TunedLmpBase;
    private int _lmpMultiplier = SearchHeuristics.TunedLmpMultiplier;
    private int _seeCaptureMargin = SearchHeuristics.TunedSeeCaptureMargin;
    private int _seeQuietMargin = SearchHeuristics.TunedSeeQuietMargin;
    private readonly int[] _lmrTable = new int[MaxSearchDepth * 128];
    private readonly Move[] _rootAllowedMoves = new Move[MoveList.MaxMoves];
    private int _rootAllowedCount;
    private bool _hasRootAllowedMoves;
    private NnueExpertType? _lastRootExpert;
    private ulong _nodeLimit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsRootMoveAllowed(Move m)
    {
        if (!_hasRootAllowedMoves) return true;
        int count = _rootAllowedCount;
        Move[] allowed = _rootAllowedMoves;
        for (int i = 0; i < count; i++)
        {
            if (allowed[i] == m) return true;
        }
        return false;
    }

    public TranspositionTable TranspositionTable => _tt;
    public TimeManager TimeManager => _timeManager;
    public IEvaluator Evaluator => _evaluator;
    public ITablebaseProber? Tablebase { get; set; }
    public int LastScoreCp { get; internal set; }

    private bool _enableQsChecks = true;
    public bool EnableQsChecks
    {
        get => _enableQsChecks;
        set
        {
            _enableQsChecks = value;
            if (_workers != null)
            {
                for (int i = 0; i < _workers.Length; i++)
                {
                    _workers[i]._qsearch.EnableQsChecks = value;
                }
            }
        }
    }

    public bool IsStopped
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stopped;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsTimeUp()
    {
        if (_stopped) return true;
        if (_timeManager.IsHardTimeUp())
        {
            _stopped = true;
            return true;
        }
        return false;
    }

    public void PonderHit() => _timeManager.PonderHit();

    public int LmrBase
    {
        get => _lmrBase;
        set
        {
            _lmrBase = value;
            InitLmrTable();
        }
    }

    public int LmrDivision
    {
        get => _lmrDivision;
        set
        {
            _lmrDivision = Math.Max(1, value);
            InitLmrTable();
        }
    }

    public int RfpMargin
    {
        get => _rfpMargin;
        set => _rfpMargin = value;
    }

    public int NmpBase
    {
        get => _nmpBase;
        set => _nmpBase = value;
    }

    public int AspirationDelta
    {
        get => _aspirationDelta;
        set => _aspirationDelta = value;
    }

    public int ConthistReward
    {
        get => _conthistReward;
        set => _conthistReward = value;
    }

    public int ConthistPenalty
    {
        get => _conthistPenalty;
        set => _conthistPenalty = value;
    }

    public int HistoryThreshold
    {
        get => _historyThreshold;
        set => _historyThreshold = value;
    }

    public int ScoreDropThreshold
    {
        get => _timeManager.ScoreDropThreshold;
        set => _timeManager.ScoreDropThreshold = value;
    }

    public int RfpImprovingMultiplier
    {
        get => _rfpImprovingMultiplier;
        set => _rfpImprovingMultiplier = value;
    }

    public int SingularMargin
    {
        get => _singularMargin;
        set => _singularMargin = value;
    }

    public int QsDeltaMargin
    {
        get => _qsDeltaMargin;
        set
        {
            _qsDeltaMargin = value;
            if (_workers != null)
            {
                foreach (var w in _workers)
                {
                    w.SetQsDeltaMargin(value);
                }
            }
        }
    }

    public int FutilityMarginD1D2
    {
        get => _futilityMarginD1D2;
        set => _futilityMarginD1D2 = value;
    }

    public int FutilityMarginD3
    {
        get => _futilityMarginD3;
        set => _futilityMarginD3 = value;
    }

    public int LmpBase
    {
        get => _lmpBase;
        set => _lmpBase = value;
    }

    public int LmpMultiplier
    {
        get => _lmpMultiplier;
        set => _lmpMultiplier = value;
    }

    public int SeeCaptureMargin
    {
        get => _seeCaptureMargin;
        set => _seeCaptureMargin = value;
    }

    public int SeeQuietMargin
    {
        get => _seeQuietMargin;
        set => _seeQuietMargin = value;
    }

    public int MoveOverheadMs
    {
        get => _timeManager.MoveOverheadMs;
        set => _timeManager.MoveOverheadMs = value;
    }

    public int TimeMovesToGo
    {
        get => _timeManager.TimeMovesToGo;
        set => _timeManager.TimeMovesToGo = value;
    }

    public int TimeIncFactor
    {
        get => _timeManager.TimeIncFactor;
        set => _timeManager.TimeIncFactor = value;
    }

    public int TimeHardMultiplier
    {
        get => _timeManager.TimeHardMultiplier;
        set => _timeManager.TimeHardMultiplier = value;
    }

    public int TimeSoftHardRatio
    {
        get => _timeManager.TimeSoftHardRatio;
        set => _timeManager.TimeSoftHardRatio = value;
    }

    public int TimeFlipScale
    {
        get => _timeManager.TimeFlipScale;
        set => _timeManager.TimeFlipScale = value;
    }

    public int TimeStableScale
    {
        get => _timeManager.TimeStableScale;
        set => _timeManager.TimeStableScale = value;
    }

    public int TimeScoreDropScale
    {
        get => _timeManager.TimeScoreDropScale;
        set => _timeManager.TimeScoreDropScale = value;
    }

    public int TimeFailLowScale
    {
        get => _timeManager.TimeFailLowScale;
        set => _timeManager.TimeFailLowScale = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetLmrReduction(int depth, int moveIndex)
    {
        int d = Math.Clamp(depth, 0, MaxSearchDepth - 1);
        int m = Math.Clamp(moveIndex, 0, 127);
        return _lmrTable[(d << 7) | m];
    }

    private void InitLmrTable()
    {
        double baseVal = _lmrBase / 100.0;
        double divVal = _lmrDivision / 100.0;
        for (int depth = 1; depth < MaxSearchDepth; depth++)
        {
            for (int moves = 1; moves < 128; moves++)
            {
                double reduction = baseVal + (Math.Log(depth) * Math.Log(moves) / divVal);
                _lmrTable[(depth << 7) | moves] = (int)reduction;
            }
        }
    }

    public int Threads
    {
        get => _threads;
        set
        {
            int t = Math.Max(1, value);
            if (_threads != t)
            {
                _threads = t;
                _tt.ClearThreads = t;
                InitWorkers(t);
            }
        }
    }

    /// <summary>
    /// Constructs an Alpha-Beta search engine instance with the specified evaluation engine and hash size.
    /// </summary>
    public AlphaBetaEngine(IEvaluator evaluator, int defaultHashMb = TranspositionTable.DefaultSizeMb)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _tt = new TranspositionTable(defaultHashMb);
        _timeManager = new TimeManager();
        _workers = [];
        InitLmrTable();
        InitWorkers(1);
    }

    /// <summary>
    /// Constructs an Alpha-Beta search engine instance with an existing Transposition Table.
    /// </summary>
    public AlphaBetaEngine(IEvaluator evaluator, TranspositionTable tt)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _tt = tt ?? new TranspositionTable();
        _timeManager = new TimeManager();
        _workers = [];
        InitLmrTable();
        InitWorkers(1);
    }

    private void InitWorkers(int count)
    {
        if (_workers != null)
        {
            foreach (var oldWorker in _workers)
            {
                oldWorker.Dispose();
            }
        }

        var workers = new SearchWorker[count];
        for (int i = 0; i < count; i++)
        {
            workers[i] = new SearchWorker(i, this, _evaluator, _tt);
            if (i > 0)
            {
                workers[i].StartHelperThread();
            }
        }
        _workers = workers;
    }

    public void Dispose()
    {
        if (_workers != null)
        {
            foreach (var worker in _workers)
            {
                worker.Dispose();
            }
            _workers = [];
        }
        _helpersDoneEvent.Dispose();
    }

    /// <summary>
    /// Clears all search history and heuristic tables across all thread workers.
    /// </summary>
    public void ClearHistory()
    {
        foreach (var worker in _workers)
        {
            worker.ClearHistories();
        }
    }

    /// <summary>
    /// Resets the remembered neural expert type across game boundaries.
    /// </summary>
    public void ResetLastRootExpert()
    {
        _lastRootExpert = null;
    }

    /// <summary>
    /// Executes Alpha-Beta / PVS search for the given board position within the specified limits.
    /// </summary>
    public string SearchTraceFile { get; set; } = string.Empty;
    private SearchTraceWriter? _searchTrace;

    public Move Search(
        in Board board,
        in SearchLimits limits,
        Action<SearchStatistics>? infoCallback = null,
        CancellationToken cancellationToken = default)
    {
        using var trace = SearchTraceWriter.Open(SearchTraceFile, board.ZobristKey);
        if (trace == null)
            return SearchCore(in board, in limits, infoCallback, cancellationToken);
        _searchTrace = trace;
        try
        {
            return SearchCore(in board, in limits, infoCallback, cancellationToken);
        }
        finally
        {
            _searchTrace = null;
            foreach (var worker in _workers) worker._qsearch.SearchTrace = null;
        }
    }

    private Move SearchCore(
        in Board board,
        in SearchLimits limits,
        Action<SearchStatistics>? infoCallback,
        CancellationToken cancellationToken)
    {
        // 1. Advance TT generation age
        _tt.NewSearch();

        // 2. Classify and pin root neural expert if multi-tier evaluator
        byte activeExpertId = 0;
        if (_evaluator is IMultiTierEvaluator multiTier)
        {
            multiTier.SetRootTopology(in board);
            if (!multiTier.IsUnifiedMoE)
            {
                activeExpertId = (byte)multiTier.ActiveRootExpert;

                // Legacy 6-model mode: clear history tables on expert transition and isolate TT scores by ExpertId.
                // Unified ArchMoE mode shares one calibrated FT & WDL scale, so history and TT remain 100% intact.
                if (_lastRootExpert.HasValue && _lastRootExpert.Value != multiTier.ActiveRootExpert)
                {
                    ClearHistory();
                }
            }
            _lastRootExpert = multiTier.ActiveRootExpert;
        }

        // 3. Initialize time manager & node limit
        _timeManager.Init(in limits, board.SideToMove);
        _nodeLimit = limits.HasNodeLimit ? limits.Nodes : 0UL;
        _stopped = false;

        // 4. Ensure workers array matches thread count
        if (_workers.Length != _threads)
        {
            InitWorkers(_threads);
        }

        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i].ResetForSearch(activeExpertId);
            _workers[i]._qsearch.SearchTrace = _searchTrace;
        }

        // 5. Check if position has zero legal moves
        MoveList legalMoves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);
        if (legalMoves.Count == 0)
        {
            return Move.Null;
        }

        // 5b. Root Syzygy WDL Draw Filter (isolated in NoInlining helper to keep SearchCore stack frame tiny)
        _hasRootAllowedMoves = false;
        _rootAllowedCount = 0;
        if (legalMoves.Count > 1 &&
            board.Castling == CastlingRights.None &&
            Tablebase is { MaxPieces: > 0 } tb &&
            BitOperations.PopCount(board.Occupied) <= tb.MaxPieces)
        {
            TryFilterRootSyzygyDraws(in board, in legalMoves, tb);
        }

        int effectiveLegalCount = _hasRootAllowedMoves ? _rootAllowedCount : legalMoves.Count;
        if (effectiveLegalCount == 1)
        {
            // Only one legal (or WDL-optimal) move: if time limit or shallow search requested, we can still report depth 1
            Move singleMove = _hasRootAllowedMoves ? _rootAllowedMoves[0] : legalMoves[0];
            if (limits.HasDepthLimit && limits.Depth == 1)
            {
                infoCallback?.Invoke(new SearchStatistics
                {
                    Depth = 1,
                    SelDepth = 1,
                    Nodes = 1,
                    TbHits = _workers[0].TbHits,
                    BestMove = singleMove,
                    Pv = [singleMove],
                    ScoreCp = 0,
                    RootScore = 0.0f
                });
                return singleMove;
            }

            if (_timeManager.HasTimeLimit && !limits.Infinite && !limits.Ponder && !limits.HasDepthLimit && !limits.HasNodeLimit)
            {
                _timeManager.CapForSingleMove(maxSoftMs: 5, maxHardMs: 10);
            }
        }

        // 6. Wake persistent Lazy SMP helper threads if Threads > 1
        bool hasHelpers = _threads > 1;
        if (hasHelpers)
        {
            _sharedSearchBoard = board;
            _sharedSearchLimits = limits;
            _sharedSearchToken = cancellationToken;
            Volatile.Write(ref _activeHelpers, _threads - 1);
            _helpersDoneEvent.Reset();
            for (int i = 1; i < _threads; i++)
            {
                _workers[i].Wake();
            }
        }

        // 7. Run main search on worker 0
        Move mainBestMove = Move.Null;
        try
        {
            mainBestMove = _workers[0].RunMainSearch(in board, in limits, infoCallback, cancellationToken);
        }
        finally
        {
            // Wait unconditionally for all helpers to finish so neither traces nor TT/worker state
            // can be mutated or freed while a helper is still unwinding.
            _stopped = true;
            if (hasHelpers)
            {
                _helpersDoneEvent.Wait();
            }
        }

        return _threads > 1 ? SelectBestThread(mainBestMove) : mainBestMove;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TryFilterRootSyzygyDraws(in Board board, in MoveList legalMoves, ITablebaseProber tb)
    {
        int count = legalMoves.Count;
        Span<sbyte> moveWdls = stackalloc sbyte[count];
        int bestWdl = int.MinValue;
        Board work = board;
        ulong rootTbHits = 0;

        for (int i = 0; i < count; i++)
        {
            Move m = legalMoves[i];
            Piece moved = work.GetPieceAt(m.From);
            work.MakeMove(m, out BoardState st, moved);

            int ourWdl;
            MoveList replies = default;
            MoveGenerator.GenerateLegalMoves(in work, ref replies);
            if (replies.Count == 0)
            {
                ourWdl = work.IsInCheck() ? 2 : 0;
            }
            else if (work.IsFiftyMoveRule() || work.IsThreefoldRepetition() || work.HasInsufficientMaterial())
            {
                ourWdl = 0;
            }
            else if (tb.TryProbeWdlNoCount(in work, out int oppWdl))
            {
                rootTbHits++;
                ourWdl = -oppWdl;
            }
            else
            {
                work.UnmakeMove(m, in st, moved);
                return;
            }

            work.UnmakeMove(m, in st, moved);
            moveWdls[i] = (sbyte)ourWdl;
            if (ourWdl > bestWdl)
            {
                bestWdl = ourWdl;
            }
        }

        if (bestWdl <= 0)
        {
            _workers[0].TbHits += rootTbHits;
            int allowedIdx = 0;
            for (int i = 0; i < count; i++)
            {
                if (moveWdls[i] == bestWdl)
                {
                    _rootAllowedMoves[allowedIdx++] = legalMoves[i];
                }
            }
            _rootAllowedCount = allowedIdx;
            if (allowedIdx > 0 && allowedIdx < count)
            {
                _hasRootAllowedMoves = true;
            }
        }
    }

    private Move SelectBestThread(Move mainBestMove)
    {
        if (mainBestMove.IsNull) return mainBestMove;

        int minScore = int.MaxValue;
        int validCount = 0;
        for (int i = 0; i < _workers.Length; i++)
        {
            if (_workers[i].CompletedDepth > 0 && !_workers[i].CompletedBestMove.IsNull)
            {
                validCount++;
                if (_workers[i].CompletedBestScore < minScore)
                {
                    minScore = _workers[i].CompletedBestScore;
                }
            }
        }

        if (validCount <= 1) return mainBestMove;

        // Check for proven mate scores first (prefer shortest mate win)
        int bestIdx = 0;
        int bestMateScore = _workers[0].CompletedDepth > 0 ? _workers[0].CompletedBestScore : -Value.InfiniteValue;
        for (int i = 1; i < _workers.Length; i++)
        {
            if (_workers[i].CompletedDepth > 0 && !_workers[i].CompletedBestMove.IsNull &&
                _workers[i].CompletedBestScore >= TTEntry.MateBound &&
                _workers[i].CompletedBestScore > bestMateScore)
            {
                bestMateScore = _workers[i].CompletedBestScore;
                bestIdx = i;
            }
        }
        if (bestMateScore >= TTEntry.MateBound)
        {
            return _workers[bestIdx].CompletedBestMove;
        }

        // Stockfish-style weighted voting across all completed workers
        Span<Move> uniqueMoves = stackalloc Move[validCount];
        Span<long> moveVotes = stackalloc long[validCount];
        int uniqueCount = 0;

        for (int i = 0; i < _workers.Length; i++)
        {
            int depth = _workers[i].CompletedDepth;
            Move m = _workers[i].CompletedBestMove;
            if (depth <= 0 || m.IsNull) continue;

            long weight = (long)(_workers[i].CompletedBestScore - minScore + 14) * depth;
            int found = -1;
            for (int u = 0; u < uniqueCount; u++)
            {
                if (uniqueMoves[u] == m)
                {
                    found = u;
                    break;
                }
            }
            if (found >= 0)
            {
                moveVotes[found] += weight;
            }
            else
            {
                uniqueMoves[uniqueCount] = m;
                moveVotes[uniqueCount] = weight;
                uniqueCount++;
            }
        }

        int winner = 0;
        for (int u = 1; u < uniqueCount; u++)
        {
            if (moveVotes[u] > moveVotes[winner])
            {
                winner = u;
            }
        }

        return uniqueCount > 0 ? uniqueMoves[winner] : mainBestMove;
    }

    /// <summary>
    /// Gets the aggregate nodes (main + QS) searched across all workers.
    /// </summary>
    public ulong GetTotalNodes()
    {
        ulong total = 0;
        for (int i = 0; i < _workers.Length; i++)
        {
            total += _workers[i].Nodes + _workers[i]._qsearch.Nodes;
        }
        return total;
    }

    /// <summary>
    /// Gets the aggregate tablebase hits across all workers.
    /// </summary>
    public ulong GetTotalTbHits()
    {
        ulong total = 0;
        for (int i = 0; i < _workers.Length; i++)
        {
            total += _workers[i].TbHits;
        }
        return total;
    }

    /// <summary>
    /// Internal search worker containing per-thread state, stacks, and history tables.
    /// Pre-allocated once to guarantee zero heap allocations during search.
    /// </summary>
    private sealed class SearchWorker : IDisposable
    {
        public readonly int Id;
        private readonly AlphaBetaEngine _engine;
        private readonly IEvaluator _evaluator;
        private readonly IIncrementalEvaluator? _incEval;
        private readonly TranspositionTable _tt;
        internal readonly QuiescenceSearch _qsearch;
        private byte _activeExpertId;
        private Thread? _thread;
        private AutoResetEvent? _wakeEvent;
        private volatile bool _disposing;

        public ulong Nodes;
        public ulong TbHits;
        public int SelDepth;
        public int CompletedDepth;
        public Move CompletedBestMove;
        public int CompletedBestScore;
        public ulong BetaCutoffs;
        public ulong FirstMoveCutoffs;

        private readonly SearchStack[] _stack;
        private readonly HistoryTable _history;
        private readonly CaptureHistory _captureHistory;
        private readonly ContinuationHistory _continuationHistory;
        private readonly CorrectionHistory _correctionHistory;
        private readonly Move[] _counterMoves;
        private readonly List<RootMoveCandidate> _rootCandidates = [];
        private readonly ulong[] _rootMoveNodes = new ulong[4096];
        private ulong _totalRootNodesThisSearch;
        private ulong _lastPawnKey;
        private float _lastOpenness = -1.0f;
        private int _rootDepth;

        public SearchWorker(int id, AlphaBetaEngine engine, IEvaluator evaluator, TranspositionTable tt)
        {
            Id = id;
            _engine = engine;
            if (id > 0 && evaluator is IIncrementalEvaluator inc)
            {
                _evaluator = inc.CreateWorkerInstance();
            }
            else
            {
                _evaluator = evaluator;
            }
            _incEval = _evaluator as IIncrementalEvaluator;
            _tt = tt;

            _stack = new SearchStack[MaxStackSize];
            for (int i = 0; i < MaxStackSize; i++)
            {
                _stack[i].Reset();
            }

            _history = new HistoryTable();
            _captureHistory = new CaptureHistory();
            _continuationHistory = new ContinuationHistory();
            _correctionHistory = new CorrectionHistory();
            _counterMoves = new Move[16 * 64];
            _qsearch = new QuiescenceSearch(_evaluator, tt, _correctionHistory, _incEval, engine.IsTimeUp, engine);
            _qsearch.DeltaSafetyMargin = engine.QsDeltaMargin;
            _qsearch.EnableQsChecks = engine.EnableQsChecks;
        }

        public void StartHelperThread()
        {
            if (Id == 0 || _thread != null) return;
            _wakeEvent = new AutoResetEvent(false);
            _thread = new Thread(HelperThreadLoop)
            {
                Name = $"AnakWorker-{Id}",
                IsBackground = true
            };
            _thread.Start();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Wake() => _wakeEvent?.Set();

        private void HelperThreadLoop()
        {
            while (true)
            {
                _wakeEvent!.WaitOne();
                if (_disposing) break;

                try
                {
                    RunHelperSearch(in _engine._sharedSearchBoard, in _engine._sharedSearchLimits, _engine._sharedSearchToken);
                }
                catch
                {
                    // Helper worker exceptions or cancellations are safely swallowed.
                }
                finally
                {
                    if (Interlocked.Decrement(ref _engine._activeHelpers) == 0)
                    {
                        _engine._helpersDoneEvent.Set();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposing) return;
            _disposing = true;
            _wakeEvent?.Set();
            _thread?.Join();
            _wakeEvent?.Dispose();
            _wakeEvent = null;
            _thread = null;
        }

        public void SetQsDeltaMargin(int margin)
        {
            _qsearch.DeltaSafetyMargin = margin;
        }

        public void ResetForSearch(byte expertId)
        {
            _activeExpertId = expertId;
            _qsearch.ActiveExpertId = expertId;
            _qsearch.Nodes = 0;
            Nodes = 0;
            TbHits = 0;
            SelDepth = 0;
            CompletedDepth = 0;
            CompletedBestMove = Move.Null;
            CompletedBestScore = -Value.InfiniteValue;
            BetaCutoffs = 0;
            FirstMoveCutoffs = 0;
            _rootCandidates.Clear();
            for (int i = 0; i < MaxStackSize; i++)
            {
                _stack[i].Reset();
            }
        }

        public void ClearHistories()
        {
            _history.Clear();
            _captureHistory.Clear();
            _continuationHistory.Clear();
            _correctionHistory.Clear();
            Array.Clear(_counterMoves);
        }

        /// <summary>
        /// Main search loop on Worker 0: Iterative Deepening with Aspiration Windows.
        /// </summary>
        public Move RunMainSearch(
            in Board rootBoard,
            in SearchLimits limits,
            Action<SearchStatistics>? infoCallback,
            CancellationToken cancellationToken)
        {
            Board board = rootBoard;
            int maxDepth = limits.HasDepthLimit ? Math.Min(limits.Depth, MaxSearchDepth) : MaxSearchDepth;

            Move bestMove = Move.Null;
            int bestScore = 0;
            int prevScore = 0;

            // Probe TT for root move initially (must pass root WDL filter if active)
            if (_tt.TryGet(board.ZobristKey, out TTEntry rootEntry) && !rootEntry.BestMove.IsNull && _engine.IsRootMoveAllowed(rootEntry.BestMove))
            {
                bestMove = rootEntry.BestMove;
            }

            // Fallback move if TT has no entry yet
            if (bestMove.IsNull)
            {
                if (_engine._hasRootAllowedMoves && _engine._rootAllowedCount > 0)
                {
                    bestMove = _engine._rootAllowedMoves[0];
                }
                else
                {
                    MoveList legal = default;
                    MoveGenerator.GenerateLegalMoves(in board, ref legal);
                    if (legal.Count > 0)
                    {
                        bestMove = legal[0];
                    }
                }
            }

            long lastIterElapsed = 0;
            _incEval?.InitSearch(in board);
            IMultiTierEvaluator? multiTier = _evaluator as IMultiTierEvaluator;
            int expectedDepth = _engine._timeManager.EstimateTargetDepth();
            int completedDepth = 0;
            Array.Clear(_rootMoveNodes);
            _totalRootNodesThisSearch = 0;

            // --- Iterative Deepening ---
            for (int currentDepth = 1; currentDepth <= maxDepth; currentDepth++)
            {
                _rootDepth = currentDepth;
                long currentElapsed = _engine._timeManager.ElapsedMilliseconds;
                long iterDuration = currentElapsed - lastIterElapsed;
                lastIterElapsed = currentElapsed;

                if (_engine._stopped ||
                    cancellationToken.IsCancellationRequested ||
                    (limits.HasNodeLimit && _engine.GetTotalNodes() >= limits.Nodes))
                {
                    break;
                }

                if (currentDepth > 1 && _engine._timeManager.HasTimeLimit)
                {
                    long targetTime = GetTargetSearchTime(bestScore, _engine._timeManager.SoftLimitMs, currentDepth, expectedDepth);
                    if (currentDepth >= 6 && _totalRootNodesThisSearch > 0 && !bestMove.IsNull)
                    {
                        double bestMoveFraction = (double)_rootMoveNodes[((int)bestMove.From << 6) | (int)bestMove.To] / _totalRootNodesThisSearch;
                        double nodeScale = Math.Clamp((1.5 - bestMoveFraction) * 1.35, 0.55, 1.65);
                        targetTime = Math.Min(Math.Max(1, _engine._timeManager.HardLimitMs - 1), (long)(targetTime * nodeScale));
                    }
                    if (currentElapsed >= targetTime || currentElapsed + (iterDuration * 2) >= _engine._timeManager.HardLimitMs)
                    {
                        break;
                    }
                }

                int score;
                Move iterBestMove = Move.Null;

                // Aspiration Windows for depth >= 6
                if (currentDepth >= 6)
                {
                    int delta = _engine.AspirationDelta;
                    int alpha = Math.Max(-Value.InfiniteValue, prevScore - delta);
                    int beta = Math.Min(Value.InfiniteValue, prevScore + delta);
                    int failHighCount = 0;

                    while (true)
                    {
                        int searchDepth = Math.Max(1, currentDepth - failHighCount);
                        score = SearchRoot(ref board, searchDepth, alpha, beta, out iterBestMove, cancellationToken);

                        if (_engine._stopped || cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (score <= alpha)
                        {
                            // Fail-low: widen alpha, avoid narrowing beta to prevent oscillations
                            alpha = Math.Max(-Value.InfiniteValue, alpha - delta);
                            failHighCount = 0;
                            delta += delta + delta / 2;

                            // Technique 5.1: Extend time allocation on root fail-low to complete re-search
                            _engine._timeManager.ExtendForFailLow(currentDepth);
                        }
                        else if (score >= beta)
                        {
                            // Fail-high: preserve the better move immediately in case re-search times out, and widen beta
                            if (!iterBestMove.IsNull)
                            {
                                bestMove = iterBestMove;
                            }
                            beta = Math.Min(Value.InfiniteValue, beta + delta);
                            failHighCount++;
                            delta += delta + delta / 2;
                        }
                        else
                        {
                            // Score is within window
                            break;
                        }

                        if (delta > 500)
                        {
                            alpha = -Value.InfiniteValue;
                            beta = Value.InfiniteValue;
                        }
                    }
                }
                else
                {
                    score = SearchRoot(ref board, currentDepth, -Value.InfiniteValue, Value.InfiniteValue, out iterBestMove, cancellationToken);
                }

                // If search was interrupted before completing this depth, discard incomplete iteration
                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    if (!iterBestMove.IsNull && bestMove.IsNull)
                    {
                        bestMove = iterBestMove;
                    }
                    break;
                }

                if (!iterBestMove.IsNull)
                {
                    bestMove = iterBestMove;
                }
                bestScore = score;
                completedDepth = currentDepth;
                CompletedDepth = currentDepth;
                CompletedBestMove = bestMove;
                CompletedBestScore = bestScore;
                _engine._timeManager.UpdateMoveStability(bestMove, currentDepth);
                _engine._timeManager.HandleScoreDrop(score, prevScore, currentDepth);
                prevScore = score;

                // Extract PV from Transposition Table
                List<Move> pv = ExtractPv(in board, bestMove);

                // Send UCI info callback
                long elapsedMs = _engine._timeManager.ElapsedMilliseconds;
                ulong totalNodes = _engine.GetTotalNodes();
                ulong nps = elapsedMs > 0 ? (totalNodes * 1000UL) / (ulong)elapsedMs : 0;

                float? cutoffEff = BetaCutoffs > 0
                    ? (float)(FirstMoveCutoffs * 100.0 / BetaCutoffs)
                    : null;

                var stats = new SearchStatistics
                {
                    Depth = currentDepth,
                    SelDepth = Math.Max(currentDepth, SelDepth),
                    Nodes = totalNodes,
                    TimeMs = elapsedMs,
                    Nps = nps,
                    BestMove = bestMove,
                    Pv = pv,
                    HashfullPermille = _tt.HashfullPermille(),
                    BetaCutoffEfficiency = cutoffEff,
                    TbHits = _engine.GetTotalTbHits(),
                };

                if (score >= TTEntry.MateBound)
                {
                    int matePlies = Value.MateValue - score;
                    int mateMoves = (matePlies + 1) / 2;
                    stats.MateIn = mateMoves;
                    stats.RootScore = 0.999f;
                }
                else if (score <= -TTEntry.MateBound)
                {
                    int matePlies = -Value.MateValue - score;
                    int mateMoves = (matePlies - 1) / 2;
                    stats.MateIn = mateMoves;
                    stats.RootScore = -0.999f;
                }
                else
                {
                    stats.ScoreCp = score;
                    _engine.LastScoreCp = score;
                    stats.RootScore = MathF.Tanh(score / 400.0f);
                }

                // Populate top candidate moves
                if (infoCallback != null && _rootCandidates.Count > 0)
                {
                    var byMove = new Dictionary<Move, RootMoveCandidate>();
                    foreach (var c in _rootCandidates)
                    {
                        byMove[c.Move] = c;
                    }

                    var sorted = byMove.Values
                        .OrderByDescending(c => c.MateIn > 0 ? 100000 - c.MateIn : (c.MateIn < 0 ? -100000 - c.MateIn : c.ScoreCp))
                        .ToList();

                    int bestIdx = sorted.FindIndex(c => c.Move == bestMove);
                    if (bestIdx > 0)
                    {
                        var bm = sorted[bestIdx];
                        sorted.RemoveAt(bestIdx);
                        sorted.Insert(0, bm);
                    }

                    List<RootMoveCandidate> top5 = [];
                    for (int i = 0; i < Math.Min(5, sorted.Count); i++)
                    {
                        var cand = sorted[i];
                        List<Move> candPv = cand.Move == bestMove ? pv : ExtractPv(in board, cand.Move);
                        top5.Add(cand with { Pv = candPv });
                    }
                    stats.TopMoves = top5;
                }

                infoCallback?.Invoke(stats);

                // Stop if mate found within current depth
                if (Math.Abs(score) >= Value.MateValue - currentDepth)
                {
                    break;
                }
            }

            return bestMove;
        }

        private long GetTargetSearchTime(int score, long softLimitMs, int depth, int expectedDepth)
        {
            // If in time crunch, use 60% of soft limit to guarantee zero timeouts
            if (_engine._timeManager.IsTimeCrunch)
            {
                return (long)(softLimitMs * 0.60f);
            }

            // Depth guard: never terminate early before depth reaches expected target depth.
            // Early cutoff at shallow depths truncates basic tactical calculation.
            if (depth < expectedDepth)
            {
                return softLimitMs;
            }

            // Case 1: Pos is bad (score <= -50 cp)
            // No reason to look for blunders; keep going with the main models using 100% of time.
            if (score <= -50)
            {
                return softLimitMs;
            }

            // Case 3: Pos is looking great (score >= 150 cp, >= +1.5 pawns)
            // When winning, target depth is reached, and move has been confirmed stable for >= 3 plies,
            // comfortably conclude main search at 85% soft limit (saving clock for future moves).
            if (score >= 150 && _engine._timeManager.StableMoveDepths >= 3)
            {
                return (long)(softLimitMs * 0.85f);
            }

            // In all other cases: main search gets 100% of soft limit!
            return softLimitMs;
        }

        /// <summary>
        /// Helper search loop for Lazy SMP workers.
        /// Helper threads run iterative deepening independently, populating the shared TT.
        /// </summary>
        public void RunHelperSearch(in Board rootBoard, in SearchLimits limits, CancellationToken cancellationToken)
        {
            Board board = rootBoard;
            int maxDepth = limits.HasDepthLimit ? Math.Min(limits.Depth, MaxSearchDepth) : MaxSearchDepth;

            // Offset helper start depth and apply Stockfish-style depth-skip diversity
            int startDepth = 1 + (Id & 1);
            int prevScore = 0;
            _incEval?.InitSearch(in board);

            for (int currentDepth = startDepth; currentDepth <= maxDepth; currentDepth++)
            {
                // Skip selected depths on helper threads >= 4 depth to spread threads across the tree
                if (currentDepth >= 4 && ((currentDepth + Id) & 3) == 0)
                {
                    continue;
                }

                _rootDepth = currentDepth;
                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                int score;
                Move iterBestMove = Move.Null;

                if (currentDepth >= 6)
                {
                    int delta = _engine.AspirationDelta + ((Id % 3) * 5);
                    int alpha = Math.Max(-Value.InfiniteValue, prevScore - delta);
                    int beta = Math.Min(Value.InfiniteValue, prevScore + delta);
                    int failHighCount = 0;

                    while (true)
                    {
                        int searchDepth = Math.Max(1, currentDepth - failHighCount);
                        score = SearchRoot(ref board, searchDepth, alpha, beta, out iterBestMove, cancellationToken);

                        if (_engine._stopped || cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (score <= alpha)
                        {
                            alpha = Math.Max(-Value.InfiniteValue, alpha - delta);
                            failHighCount = 0;
                            delta += delta + (delta / 2);
                        }
                        else if (score >= beta)
                        {
                            if (!iterBestMove.IsNull)
                            {
                                CompletedBestMove = iterBestMove;
                            }
                            beta = Math.Min(Value.InfiniteValue, beta + delta);
                            failHighCount++;
                            delta += delta + (delta / 2);
                        }
                        else
                        {
                            break;
                        }

                        if (delta > 500)
                        {
                            alpha = -Value.InfiniteValue;
                            beta = Value.InfiniteValue;
                            failHighCount = 0;
                        }
                    }
                }
                else
                {
                    score = SearchRoot(ref board, currentDepth, -Value.InfiniteValue, Value.InfiniteValue, out iterBestMove, cancellationToken);
                }

                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!iterBestMove.IsNull)
                {
                    CompletedDepth = currentDepth;
                    CompletedBestMove = iterBestMove;
                    CompletedBestScore = score;
                }
                prevScore = score;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetOpenness(in Board board)
        {
            ulong pKey = board.PawnKey;
            if (pKey == _lastPawnKey && _lastOpenness >= 0.0f)
            {
                return _lastOpenness;
            }

            float val = PawnTopology.CalculateOpenness(in board);
            _lastPawnKey = pKey;
            _lastOpenness = val;
            return val;
        }

        /// <summary>
        /// Root search with PVS move ordering and TT update.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int SearchRoot(
            ref Board board,
            int depth,
            int alpha,
            int beta,
            out Move rootBestMove,
            CancellationToken cancellationToken)
        {
            rootBestMove = Move.Null;
            int alphaOrig = alpha;
            ulong key = board.ZobristKey;

            bool ttHit = _tt.TryGet(key, out TTEntry entry);
            Move ttMove = ttHit ? entry.BestMove : Move.Null;

            _stack[0].Reset();
            _stack[0].InCheck = board.IsInCheck();
            if (!_stack[0].InCheck)
            {
                int rawEval = (ttHit && entry.ExpertId == _activeExpertId && entry.StaticEval != short.MinValue)
                    ? entry.StaticEval
                    : _evaluator.Evaluate(in board).Raw;
                _stack[0].StaticEval = _correctionHistory.GetAdjustedEval(in board, Piece.None, Square.None, rawEval);
            }

            // Root move picker
            var picker = new MovePicker(
                in board,
                ttMove,
                killer0: Move.Null,
                killer1: Move.Null,
                counterMove: Move.Null,
                _history,
                _captureHistory,
                _continuationHistory,
                inCheck: _stack[0].InCheck);

            int bestScore = -Value.InfiniteValue;
            int movesSearched = 0;

            Span<Move> quietsSearched = stackalloc Move[64];
            Span<Piece> quietsSearchedPiece = stackalloc Piece[64];
            int quietsCount = 0;
            Span<Move> capturesSearched = stackalloc Move[32];
            Span<Piece> capturesSearchedPiece = stackalloc Piece[32];
            Span<Piece> capturesSearchedVictim = stackalloc Piece[32];
            int capturesCount = 0;
            float rootOpenness = depth >= 3 ? GetOpenness(in board) : 0.5f;

            if (Id == 0)
            {
                _rootCandidates.Clear();
            }

            while (picker.Next(out Move move))
            {
                if (_engine._hasRootAllowedMoves && !_engine.IsRootMoveAllowed(move))
                {
                    continue;
                }

                ulong nodesBefore = Nodes;
                Piece movedPiece = board.GetPieceAt(move.From);
                board.MakeMove(move, out BoardState state, movedPiece);
                _engine._searchTrace?.WriteEdge(key, board.ZobristKey, move);
                _tt.Prefetch(board.ZobristKey);
                Nodes++;
                _incEval?.PushMove(move, movedPiece, state.CapturedPiece, in board);

                _stack[0].CurrentMove = move;
                _stack[0].MovedPiece = movedPiece;

                int score;
                if (movesSearched == 0)
                {
                    // PV move searched with full window
                    score = -AlphaBeta(ref board, depth - 1, -beta, -alpha, ply: 1, isPv: true, cancellationToken);
                }
                else
                {
                    // LMR / null-window search
                    int r = 0;
                    if (depth >= 3 && movesSearched > 1 && !move.IsCapture && !move.IsPromotion && !_stack[0].InCheck)
                    {
                        r = _engine.GetLmrReduction(depth, movesSearched);
                        r = SearchHeuristics.AdjustLmrForTopology(r, rootOpenness, isPv: true, isTactical: false, inCheck: false);

                        int hist = _history.GetScore(movedPiece.Color(), move);
                        if (hist < -_engine.HistoryThreshold) r++;
                        else if (hist > _engine.HistoryThreshold) r--;
                        r = Math.Clamp(r, 0, depth - 2);
                    }

                    int nextDepth = depth - 1;
                    int searchDepth = Math.Max(0, nextDepth - r);
                    score = -AlphaBeta(ref board, searchDepth, -alpha - 1, -alpha, ply: 1, isPv: false, cancellationToken);

                    if (!_engine._stopped && score > alpha && searchDepth < nextDepth)
                    {
                        score = -AlphaBeta(ref board, nextDepth, -alpha - 1, -alpha, ply: 1, isPv: false, cancellationToken);
                    }

                    if (!_engine._stopped && score > alpha)
                    {
                        // Full window re-search on PVS fail-high
                        score = -AlphaBeta(ref board, nextDepth, -beta, -alpha, ply: 1, isPv: true, cancellationToken);
                    }
                }

                board.UnmakeMove(move, in state, movedPiece);
                _incEval?.RollbackTo(0);

                if (Id == 0)
                {
                    ulong moveNodes = Nodes - nodesBefore;
                    _rootMoveNodes[((int)move.From << 6) | (int)move.To] += moveNodes;
                    _totalRootNodesThisSearch += moveNodes;
                }

                if (Id == 0)
                {
                    int moveCp = 0;
                    int moveMate = 0;
                    if (score >= TTEntry.MateBound)
                    {
                        moveMate = (Value.MateValue - score + 1) / 2;
                    }
                    else if (score <= -TTEntry.MateBound)
                    {
                        moveMate = (-Value.MateValue - score - 1) / 2;
                    }
                    else
                    {
                        moveCp = score;
                    }

                    _rootCandidates.Add(new RootMoveCandidate(move, moveCp, moveMate));
                }

                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }

                movesSearched++;

                if (!move.IsCapture && !move.IsPromotion && quietsCount < quietsSearched.Length)
                {
                    quietsSearched[quietsCount] = move;
                    quietsSearchedPiece[quietsCount] = movedPiece;
                    quietsCount++;
                }
                else if (move.IsCapture && capturesCount < capturesSearched.Length)
                {
                    capturesSearched[capturesCount] = move;
                    capturesSearchedPiece[capturesCount] = movedPiece;
                    capturesSearchedVictim[capturesCount] = state.CapturedPiece;
                    capturesCount++;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    rootBestMove = move;

                    if (score > alpha)
                    {
                        alpha = score;

                        if (score >= beta)
                        {
                            // Beta cutoff at root
                            BetaCutoffs++;
                            if (movesSearched == 1)
                            {
                                FirstMoveCutoffs++;
                            }
                            UpdateHistoriesOnCutoff(in board, move, movedPiece, depth,
                                quietsSearched[..quietsCount], quietsSearchedPiece[..quietsCount],
                                capturesSearched[..capturesCount], capturesSearchedPiece[..capturesCount], capturesSearchedVictim[..capturesCount],
                                prev1Piece: Piece.None, prev1To: Square.None, prev2Piece: Piece.None, prev2To: Square.None);
                            break;
                        }
                    }
                }
            }

            if (movesSearched == 0)
            {
                return _stack[0].InCheck ? -Value.MateValue : 0;
            }

            // Store in TT
            TTFlags flag = (bestScore >= beta) ? TTFlags.LowerBound : (bestScore > alphaOrig ? TTFlags.Exact : TTFlags.UpperBound);
            Move storeMove = !rootBestMove.IsNull ? rootBestMove : ttMove;
            _tt.Store(key, storeMove, (byte)Math.Clamp(depth, 0, 255), TTEntry.ScoreToTt(bestScore, 0), _tt.CurrentAge, flag, _activeExpertId);

            return bestScore;
        }

        /// <summary>
        /// Negamax search with Principal Variation Search, Transposition Table, and dynamic pruning.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int AlphaBeta(
            ref Board board,
            int depth,
            int alpha,
            int beta,
            int ply,
            bool isPv,
            CancellationToken cancellationToken,
            int knownInCheck = -1)
        {
            if (_engine._stopped)
            {
                return 0;
            }

            // Update selective depth
            if (ply > SelDepth)
            {
                SelDepth = ply;
            }

            // 1. Repetition & Fifty-move draw detection
            // Must run before Quiescence Search so leaf moves (depth == 0) that repeat are scored as 0.
            bool hasPriorOccurrence = false;
            if (ply > 0 && board.HalfmoveClock >= 4)
            {
                if (board.IsFiftyMoveRule())
                {
                    if (!board.IsInCheck() || board.GenerateLegalMoves().Count > 0)
                    {
                        return 0;
                    }
                    return -Value.MateValue + ply;
                }
                if (board.IsRepetition(ply, out hasPriorOccurrence))
                {
                    return 0;
                }
            }

            // Check detection & check extension
            bool inCheck = knownInCheck >= 0 ? knownInCheck != 0 : board.IsInCheck();
            _stack[ply].InCheck = inCheck;

            if (inCheck && depth < MaxSearchDepth)
            {
                depth++;
            }

            // 2. Quiescence Search at leaf nodes (inCheck is guaranteed false here)
            if (depth <= 0)
            {
                Piece qPrev1Piece = ply >= 1 ? _stack[ply - 1].MovedPiece : Piece.None;
                Square qPrev1To = ply >= 1 && !_stack[ply - 1].CurrentMove.IsNull ? _stack[ply - 1].CurrentMove.To : Square.None;
                return _qsearch.Search(ref board, alpha, beta, ply, qsDepth: 0, knownInCheck: 0, qPrev1Piece, qPrev1To);
            }

            // Stack limit safety
            if (ply >= MaxSearchDepth)
            {
                return _evaluator.Evaluate(in board).Raw;
            }

            // Clear child killers so stale siblings don't pollute move ordering
            if (ply + 1 < MaxStackSize)
            {
                _stack[ply + 1].Killer0 = Move.Null;
                _stack[ply + 1].Killer1 = Move.Null;
            }

            // Periodic time management, node limit & cancellation check (dynamically masked for low-time safety)
            if ((Nodes & _engine._timeManager.CheckMask) == 0)
            {
                if (_engine._stopped ||
                    cancellationToken.IsCancellationRequested ||
                    _engine._timeManager.IsHardTimeUp() ||
                    (_engine._nodeLimit > 0 && (Id == 0 ? _engine.GetTotalNodes() >= _engine._nodeLimit : Nodes >= _engine._nodeLimit)))
                {
                    _engine._stopped = true;
                    return 0;
                }
            }

            int alphaOrig = alpha;
            ulong key = board.ZobristKey;

            // 3. Mate Distance Pruning
            int alphaM = Math.Max(alpha, -Value.MateValue + ply);
            int betaM = Math.Min(beta, Value.MateValue - (ply + 1));
            if (alphaM >= betaM)
            {
                return alphaM;
            }
            alpha = alphaM;
            beta = betaM;

            // 3a. Insufficient Material Draw Detection
            if (ply > 0 && board.HasInsufficientMaterial())
            {
                return 0;
            }

            // 3b. Syzygy Tablebase WDL Probe (register-first check + strictly on zero HalfmoveClock right after captures/pawn pushes
            // so 50-move WDL semantics are exact and non-zeroing shuffling plies never stall on disk I/O)
            if (board.HalfmoveClock == 0 &&
                board.Castling == CastlingRights.None &&
                BitOperations.PopCount(board.Occupied) <= 7 &&
                ply > 0 &&
                _engine.Tablebase is { MaxPieces: > 0 } inTreeTb &&
                depth >= inTreeTb.SyzygyProbeDepth &&
                BitOperations.PopCount(board.Occupied) <= inTreeTb.MaxPieces &&
                _stack[ply].ExcludedMove.IsNull)
            {
                if (inTreeTb.TryProbeWdlNoCount(in board, out int wdl))
                {
                    TbHits++;
                    int tbScore = wdl switch
                    {
                        2 => 20000 - ply,
                        1 => 100,   // Cursed win
                        0 => 0,     // Draw
                        -1 => -100, // Blessed loss
                        -2 => -(20000 - ply),
                        _ => 0
                    };

                    TTFlags flag = TTFlags.Exact;
                    if (tbScore >= beta) flag = TTFlags.LowerBound;
                    else if (tbScore <= alpha) flag = TTFlags.UpperBound;

                    _tt.Store(key, Move.Null, (byte)Math.Clamp(depth, 0, 255), TTEntry.ScoreToTt(tbScore, ply), _tt.CurrentAge, flag, _activeExpertId);

                    return tbScore;
                }
            }

            // 4. Transposition Table Probe
            bool ttHit = _tt.TryGet(key, out TTEntry entry);
            Move ttMove = ttHit ? entry.BestMove : Move.Null;
            int ttScore = ttHit ? TTEntry.ScoreFromTt(entry.Score, ply) : 0;
            bool ttExpertMatch = ttHit && entry.ExpertId == _activeExpertId;

            // TT Cutoff for non-PV nodes (only when evaluation expert matches active root expert and no prior game-history occurrence)
            if (!isPv && !hasPriorOccurrence && ttExpertMatch && entry.Depth >= depth && _stack[ply].ExcludedMove.IsNull)
            {
                if (entry.Flags == TTFlags.Exact) return ttScore;
                if (entry.Flags == TTFlags.LowerBound && ttScore >= beta) return ttScore;
                if (entry.Flags == TTFlags.UpperBound && ttScore <= alpha) return ttScore;
            }

            Piece prev1Piece = ply >= 1 ? _stack[ply - 1].MovedPiece : Piece.None;
            Square prev1To = ply >= 1 && !_stack[ply - 1].CurrentMove.IsNull ? _stack[ply - 1].CurrentMove.To : Square.None;

            // 5. Static Evaluation
            int staticEval = -Value.InfiniteValue;
            int rawEval = -Value.InfiniteValue;
            int eval = -Value.InfiniteValue;
            if (!inCheck)
            {
                if (ttExpertMatch && entry.StaticEval != short.MinValue)
                {
                    rawEval = entry.StaticEval;
                }
                else
                {
                    rawEval = _evaluator.Evaluate(in board).Raw;
                }
                staticEval = _correctionHistory.GetAdjustedEval(in board, prev1Piece, prev1To, rawEval);
                eval = staticEval;

                if (ttExpertMatch)
                {
                    if (entry.Flags == TTFlags.Exact)
                    {
                        eval = ttScore;
                    }
                    else if (entry.Flags == TTFlags.LowerBound && ttScore > eval)
                    {
                        eval = ttScore;
                    }
                    else if (entry.Flags == TTFlags.UpperBound && ttScore < eval)
                    {
                        eval = ttScore;
                    }
                }
            }
            _stack[ply].StaticEval = staticEval;

            // Improving heuristic: position is improving if static eval is higher than 2 plies ago for the same side
            bool improving = false;
            if (!inCheck)
            {
                if (ply >= 2 && !_stack[ply - 2].InCheck && _stack[ply - 2].StaticEval != -Value.InfiniteValue)
                {
                    improving = staticEval > _stack[ply - 2].StaticEval;
                }
                else if (ply >= 4 && !_stack[ply - 4].InCheck && _stack[ply - 4].StaticEval != -Value.InfiniteValue)
                {
                    improving = staticEval > _stack[ply - 4].StaticEval;
                }
            }

            // 5. Internal Iterative Reductions (IIR): depth >= 4 and no TT move -> depth--
            if (depth >= 4 && ttMove.IsNull)
            {
                depth--;
            }

            // 6. Reverse Futility Pruning (Static Null Move Pruning) for depth <= 6
            if (!isPv && !inCheck && depth <= 6 && _stack[ply].ExcludedMove.IsNull &&
                Math.Abs(beta) < TTEntry.MateBound &&
                SearchHeuristics.HasNonPawnMaterial(in board, board.SideToMove))
            {
                int rfpMargin = _engine.RfpMargin * depth;
                if (improving)
                {
                    rfpMargin = (rfpMargin * _engine.RfpImprovingMultiplier) / 100;
                }
                if (eval - rfpMargin >= beta)
                {
                    return eval - rfpMargin;
                }
            }

            float openness = -1.0f;

            // 7. Adaptive Null Move Pruning (NMP)
            if (!isPv && !inCheck && depth >= 2 && eval >= beta &&
                _stack[ply].ExcludedMove.IsNull &&
                (ply >= 1 && _stack[ply - 1].CurrentMove != Move.Null) &&
                SearchHeuristics.HasNonPawnMaterial(in board, board.SideToMove))
            {
                openness = GetOpenness(in board);
                int r = SearchHeuristics.GetNmpReduction(depth, eval, beta, openness, _engine.NmpBase);
                int nullDepth = depth - 1 - r;

                board.MakeNullMove(out BoardState nullState);
                _engine._searchTrace?.WriteEdge(key, board.ZobristKey, Move.Null);
                _tt.Prefetch(board.ZobristKey);
                _incEval?.PushNullMove(in board);
                _stack[ply].CurrentMove = Move.Null;
                _stack[ply].MovedPiece = Piece.None;

                int nullScore = -AlphaBeta(ref board, nullDepth, -beta, -beta + 1, ply + 1, isPv: false, cancellationToken, knownInCheck: 0);
                _incEval?.RollbackTo(ply);
                board.UnmakeNullMove(in nullState);

                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }

                if (nullScore >= beta)
                {
                    // Do not return unproven mate scores from null move
                    return nullScore >= Value.MateValue - 100 ? beta : nullScore;
                }
            }

            // 9. ProbCut: if a shallow tactical search beats beta + margin, prune early
            int probCutBeta = beta + 180;
            if (!isPv && !inCheck && depth >= 5 && _stack[ply].ExcludedMove.IsNull &&
                Math.Abs(beta) < TTEntry.MateBound &&
                (!ttHit || entry.Depth < depth - 3 || ttScore >= probCutBeta))
            {
                int probCutDepth = depth - 4;
                int seeThreshold = probCutBeta - eval;

                Unsafe.SkipInit(out MoveList pcMoves);
                pcMoves.Clear();
                MoveGenerator.GenerateLegalCaptures(in board, ref pcMoves);

                for (int i = 0; i < pcMoves.Count; i++)
                {
                    Move pcMove = pcMoves[i];
                    if (!StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, pcMove, seeThreshold))
                    {
                        continue;
                    }

                    Piece pcMovedPiece = board.GetPieceAt(pcMove.From);
                    board.MakeMove(pcMove, out BoardState pcState, pcMovedPiece);
                    _engine._searchTrace?.WriteEdge(key, board.ZobristKey, pcMove);
                    _tt.Prefetch(board.ZobristKey);
                    Nodes++;
                    _incEval?.PushMove(pcMove, pcMovedPiece, pcState.CapturedPiece, in board);

                    _stack[ply].CurrentMove = pcMove;
                    _stack[ply].MovedPiece = pcMovedPiece;

                    int pcScore = -_qsearch.Search(ref board, -probCutBeta, -probCutBeta + 1, ply + 1);
                    if (pcScore >= probCutBeta && probCutDepth > 0 && !_engine._stopped && !cancellationToken.IsCancellationRequested)
                    {
                        pcScore = -AlphaBeta(ref board, probCutDepth, -probCutBeta, -probCutBeta + 1, ply + 1, isPv: false, cancellationToken);
                    }

                    board.UnmakeMove(pcMove, in pcState, pcMovedPiece);
                    _incEval?.RollbackTo(ply);

                    if (_engine._stopped || cancellationToken.IsCancellationRequested)
                    {
                        return 0;
                    }

                    if (pcScore >= probCutBeta)
                    {
                        _tt.Store(key, pcMove, (byte)Math.Clamp(probCutDepth, 0, 255), TTEntry.ScoreToTt(pcScore, ply), _tt.CurrentAge, TTFlags.LowerBound, _activeExpertId);
                        return pcScore;
                    }
                }
            }

            // 10. Singular Extensions (requires matching expert score)
            int singularExtension = 0;
            if (depth >= 6 && ply > 0 && ply < 2 * _rootDepth && depth < MaxSearchDepth - 2 &&
                ttExpertMatch && !entry.BestMove.IsNull &&
                Math.Abs(ttScore) < TTEntry.MateBound &&
                entry.Depth >= depth - 3 && entry.Flags != TTFlags.UpperBound &&
                _stack[ply].ExcludedMove.IsNull)
            {
                int singularBeta = ttScore - (_engine.SingularMargin * depth);
                _stack[ply].ExcludedMove = entry.BestMove;
                int sDepth = (depth - 1) / 2;

                int sScore = AlphaBeta(ref board, sDepth, singularBeta - 1, singularBeta, ply, isPv: false, cancellationToken);
                _stack[ply].ExcludedMove = Move.Null;

                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }

                if (sScore < singularBeta)
                {
                    singularExtension = (!isPv && ttScore >= beta) ? 2 : 1;
                }
            }

            // 11. Move Ordering with staged MovePicker
            Piece prev2Piece = ply >= 2 ? _stack[ply - 2].MovedPiece : Piece.None;
            Square prev2To = ply >= 2 && !_stack[ply - 2].CurrentMove.IsNull ? _stack[ply - 2].CurrentMove.To : Square.None;

            Move counterMove = (prev1Piece != Piece.None && prev1To != Square.None)
                ? _counterMoves[(ContinuationHistory.PieceToIndex(prev1Piece) << 6) | (int)prev1To]
                : Move.Null;

            int contBase1 = ContinuationHistory.GetBaseOffset(prev1Piece, prev1To);
            int contBase2 = ContinuationHistory.GetBaseOffset(prev2Piece, prev2To);

            var picker = new MovePicker(
                in board,
                ttMove,
                _stack[ply].Killer0,
                _stack[ply].Killer1,
                counterMove,
                _history,
                _captureHistory,
                _continuationHistory,
                contBase1,
                contBase2,
                inCheck);

            int bestScore = -Value.InfiniteValue;
            Move bestMove = Move.Null;
            int movesSearched = 0;

            Span<Move> quietsSearched = stackalloc Move[64];
            Span<Piece> quietsSearchedPiece = stackalloc Piece[64];
            int quietsCount = 0;
            Span<Move> capturesSearched = stackalloc Move[32];
            Span<Piece> capturesSearchedPiece = stackalloc Piece[32];
            Span<Piece> capturesSearchedVictim = stackalloc Piece[32];
            int capturesCount = 0;

            while (picker.Next(out Move move))
            {
                // Skip singular extension excluded move
                if (move == _stack[ply].ExcludedMove)
                {
                    continue;
                }

                bool isQuiet = !move.IsCapture && !move.IsPromotion;
                int givesCheckState = -1;

                // Pre-move pruning (exempt checking moves and positions where a mate threat was seen)
                if (!isPv && !inCheck && bestScore > -TTEntry.MateBound)
                {
                    // Bad Captures Pruning at shallow depth
                    if (depth <= 2 && picker.Stage == MovePickerStage.BadCaptures && !GivesCheckCached(in board, move, ref givesCheckState))
                    {
                        continue;
                    }

                    // Shallow Futility Pruning for non-tactical quiet moves at shallow depth
                    if (isQuiet && movesSearched > 0)
                    {
                        if (depth <= 2 && eval + (_engine.FutilityMarginD1D2 * depth) <= alpha && !GivesCheckCached(in board, move, ref givesCheckState))
                        {
                            continue;
                        }
                        if (depth == 3 && staticEval + (_engine.FutilityMarginD3 * depth) <= alpha && !GivesCheckCached(in board, move, ref givesCheckState))
                        {
                            continue;
                        }
                    }

                    // Late Move Pruning (LMP) for quiet moves
                    if (depth <= 3 && isQuiet)
                    {
                        int lmpThreshold = _engine.LmpBase + ((improving ? _engine.LmpMultiplier : 1) * depth * depth);
                        if (movesSearched >= lmpThreshold && !GivesCheckCached(in board, move, ref givesCheckState))
                        {
                            continue;
                        }
                    }

                    // Static Exchange Evaluation (SEE) Pruning
                    if (movesSearched > 0)
                    {
                        // Bad Captures Pruning: Prune clearly losing exchanges at shallow depths
                        if (depth <= 5 && move.IsCapture && !move.IsPromotion)
                        {
                            if (picker.CurrentSee < -_engine.SeeCaptureMargin * depth && !GivesCheckCached(in board, move, ref givesCheckState))
                            {
                                continue;
                            }
                        }

                        // Quiet SEE Pruning: Prune quiet moves that blunder pieces onto attacked squares
                        if (depth <= 4 && isQuiet)
                        {
                            if (!StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, move, -_engine.SeeQuietMargin * depth * depth) && !GivesCheckCached(in board, move, ref givesCheckState))
                            {
                                continue;
                            }
                        }
                    }
                }

                if (openness < 0.0f && depth >= 3 && movesSearched > (isPv ? 2 : 1) && !inCheck && isQuiet)
                {
                    openness = GetOpenness(in board);
                }

                Piece movedPiece = board.GetPieceAt(move.From);
                board.MakeMove(move, out BoardState state, movedPiece);
                _engine._searchTrace?.WriteEdge(key, board.ZobristKey, move);
                _tt.Prefetch(board.ZobristKey);
                Nodes++;
                _incEval?.PushMove(move, movedPiece, state.CapturedPiece, in board);

                _stack[ply].CurrentMove = move;
                _stack[ply].MovedPiece = movedPiece;

                int extension = (move == ttMove) ? singularExtension : 0;
                int nextDepth = depth - 1 + extension;

                int score;
                if (movesSearched == 0)
                {
                    // PV move searched with full window
                    score = -AlphaBeta(ref board, nextDepth, -beta, -alpha, ply + 1, isPv, cancellationToken, givesCheckState);
                }
                else
                {
                    // Late Move Reductions (LMR) for quiet moves and late non-winning captures
                    int r = 0;
                    if (depth >= 3 && movesSearched > (isPv ? 2 : 1) && !inCheck)
                    {
                        if (!move.IsCapture && !move.IsPromotion &&
                            move != _stack[ply].Killer0 && move != _stack[ply].Killer1)
                        {
                            r = _engine.GetLmrReduction(depth, movesSearched);
                            r = SearchHeuristics.AdjustLmrForTopology(r, openness, isPv, isTactical: false, inCheck: false);

                            if (!improving)
                            {
                                r++;
                            }

                            if (picker.CurrentStage == MovePickerStage.CounterMove)
                            {
                                r--;
                            }

                            int hist = _history.GetScore(movedPiece.Color(), move);
                            int conthist1 = _continuationHistory.Get1PlyAt(contBase1, movedPiece, move.To);
                            int conthist2 = _continuationHistory.Get2PlyAt(contBase2, movedPiece, move.To);
                            int statScore = hist + conthist1 + conthist2;
                            r -= statScore / 8192;

                            r = Math.Clamp(r, 0, depth - 2);
                        }
                        else if (move.IsCapture && !move.IsPromotion && movesSearched > 4)
                        {
                            // Capture LMR: 1-ply reduction for non-winning captures searched late (reusing cached SEE)
                            if (picker.CurrentSee <= 0)
                            {
                                r = 1;
                            }
                        }
                    }

                    int searchDepth = Math.Max(0, nextDepth - r);
                    score = -AlphaBeta(ref board, searchDepth, -alpha - 1, -alpha, ply + 1, isPv: false, cancellationToken, givesCheckState);

                    if (!_engine._stopped && score > alpha && searchDepth < nextDepth)
                    {
                        bool doDeeperSearch = score > bestScore + 50 + 2 * nextDepth;
                        bool doShallowerSearch = score < bestScore + nextDepth;
                        int reSearchDepth = Math.Max(0, nextDepth + (doDeeperSearch ? 1 : 0) - (doShallowerSearch ? 1 : 0));
                        if (reSearchDepth > searchDepth)
                        {
                            score = -AlphaBeta(ref board, reSearchDepth, -alpha - 1, -alpha, ply + 1, isPv: false, cancellationToken, givesCheckState);
                        }
                    }

                    if (!_engine._stopped && isPv && score > alpha)
                    {
                        // PVS re-search with full window on fail-high
                        score = -AlphaBeta(ref board, nextDepth, -beta, -alpha, ply + 1, isPv: true, cancellationToken, givesCheckState);
                    }
                }

                board.UnmakeMove(move, in state, movedPiece);
                _incEval?.RollbackTo(ply);

                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }

                movesSearched++;

                if (!move.IsCapture && !move.IsPromotion && quietsCount < quietsSearched.Length)
                {
                    quietsSearched[quietsCount] = move;
                    quietsSearchedPiece[quietsCount] = movedPiece;
                    quietsCount++;
                }
                else if (move.IsCapture && capturesCount < capturesSearched.Length)
                {
                    capturesSearched[capturesCount] = move;
                    capturesSearchedPiece[capturesCount] = movedPiece;
                    capturesSearchedVictim[capturesCount] = state.CapturedPiece;
                    capturesCount++;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = move;

                    if (score > alpha)
                    {
                        alpha = score;

                        if (score >= beta)
                        {
                            // Beta Cutoff: Update heuristics
                            BetaCutoffs++;
                            if (movesSearched == 1)
                            {
                                FirstMoveCutoffs++;
                            }
                            UpdateHistoriesOnCutoff(in board, move, movedPiece, depth,
                                quietsSearched[..quietsCount], quietsSearchedPiece[..quietsCount],
                                capturesSearched[..capturesCount], capturesSearchedPiece[..capturesCount], capturesSearchedVictim[..capturesCount],
                                prev1Piece, prev1To, prev2Piece, prev2To);

                            // Killer move storage in SearchStack
                            if (!move.IsCapture && !move.IsPromotion)
                            {
                                _stack[ply].AddKiller(move);
                            }

                            // Correction history update on cutoff (LowerBound: score exceeds staticEval; skip tactical cutoffs)
                            if (!inCheck && !move.IsCapture && !move.IsPromotion &&
                                rawEval != -Value.InfiniteValue && score > staticEval && Math.Abs(score) < TTEntry.MateBound)
                            {
                                _correctionHistory.Update(in board, prev1Piece, prev1To, depth, score, rawEval);
                            }

                            // Transposition Table LowerBound store
                            if (_stack[ply].ExcludedMove.IsNull)
                            {
                                short evalShort = TTEntry.ClampEval(rawEval);
                                _tt.Store(key, move, (byte)Math.Clamp(depth, 0, 255), TTEntry.ScoreToTt(score, ply), _tt.CurrentAge, TTFlags.LowerBound, _activeExpertId, evalShort);
                            }

                            return score;
                        }
                    }
                }
            }

            // Checkmate or Stalemate handling
            if (movesSearched == 0)
            {
                if (!_stack[ply].ExcludedMove.IsNull)
                {
                    return alpha;
                }

                return inCheck ? -(Value.MateValue - ply) : 0;
            }

            // Correction history update on non-cutoff nodes (skip tactical best moves)
            // Exact nodes: update in either direction; UpperBound: only update if bestScore < staticEval
            if (!inCheck && (bestMove.IsNull || (!bestMove.IsCapture && !bestMove.IsPromotion)) &&
                rawEval != -Value.InfiniteValue && Math.Abs(bestScore) < TTEntry.MateBound)
            {
                if (bestScore > alphaOrig || bestScore < staticEval)
                {
                    _correctionHistory.Update(in board, prev1Piece, prev1To, depth, bestScore, rawEval);
                }
            }

            // Transposition Table store (Exact or UpperBound)
            if (_stack[ply].ExcludedMove.IsNull)
            {
                TTFlags flag = (bestScore > alphaOrig) ? TTFlags.Exact : TTFlags.UpperBound;
                Move storeMove = !bestMove.IsNull ? bestMove : ttMove;
                short evalShort = TTEntry.ClampEval(rawEval);
                _tt.Store(key, storeMove, (byte)Math.Clamp(depth, 0, 255), TTEntry.ScoreToTt(bestScore, ply), _tt.CurrentAge, flag, _activeExpertId, evalShort);
            }

            return bestScore;
        }

        /// <summary>
        /// Updates butterfly history, continuation history, countermoves, and capture history on a beta cutoff.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateHistoriesOnCutoff(
            in Board board,
            Move move,
            Piece movedPiece,
            int depth,
            ReadOnlySpan<Move> quietsSearched,
            ReadOnlySpan<Piece> quietsSearchedPiece,
            ReadOnlySpan<Move> capturesSearched,
            ReadOnlySpan<Piece> capturesSearchedPiece,
            ReadOnlySpan<Piece> capturesSearchedVictim,
            Piece prev1Piece,
            Square prev1To,
            Piece prev2Piece,
            Square prev2To)
        {
            int bonus = HistoryTable.CalculateBonus(depth);
            int malus = HistoryTable.CalculateMalus(depth);

            if (!move.IsCapture && !move.IsPromotion)
            {
                // 1. Butterfly history bonus for best quiet move
                _history.Update(board.SideToMove, move, bonus);

                // 2. 1-ply and 2-ply continuation history updates
                _continuationHistory.Update1Ply(prev1Piece, prev1To, movedPiece, move.To, bonus);
                _continuationHistory.Update2Ply(prev2Piece, prev2To, movedPiece, move.To, bonus);

                // 3. Countermove table
                if (prev1Piece != Piece.None && prev1To != Square.None)
                {
                    _counterMoves[(ContinuationHistory.PieceToIndex(prev1Piece) << 6) | (int)prev1To] = move;
                }

                // 4. Malus for quiet moves that failed to cause a cutoff (with history malus capping)
                for (int i = 0; i < quietsSearched.Length; i++)
                {
                    Move failed = quietsSearched[i];
                    if (failed != move)
                    {
                        Piece failedPiece = quietsSearchedPiece[i];
                        _history.Update(board.SideToMove, failed, -malus);
                        _continuationHistory.Update1Ply(prev1Piece, prev1To, failedPiece, failed.To, -malus);
                        _continuationHistory.Update2Ply(prev2Piece, prev2To, failedPiece, failed.To, -malus);
                    }
                }
            }
            else if (move.IsCapture)
            {
                // Capture history updates on capture beta cutoff
                Piece captured = move.Type == MoveType.EnPassant
                    ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
                    : board.GetPieceAt(move.To);
                _captureHistory.Update(movedPiece, move.To, captured, bonus);
            }

            // Capture history malus: penalize earlier searched captures that failed to achieve a cutoff
            for (int i = 0; i < capturesSearched.Length; i++)
            {
                Move failed = capturesSearched[i];
                if (failed != move)
                {
                    _captureHistory.Update(capturesSearchedPiece[i], failed.To, capturesSearchedVictim[i], -malus);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool GivesCheckCached(in Board board, Move move, ref int givesCheckState)
        {
            if (givesCheckState < 0)
            {
                givesCheckState = board.GivesCheck(move) ? 1 : 0;
            }
            return givesCheckState != 0;
        }

        /// <summary>
        /// Traces the Principal Variation sequence through the Transposition Table.
        /// </summary>
        private List<Move> ExtractPv(in Board rootBoard, Move bestMove)
        {
            var pv = new List<Move>(MaxSearchDepth);
            if (bestMove.IsNull)
            {
                return pv;
            }

            pv.Add(bestMove);

            Board pvBoard = rootBoard;
            Span<ulong> visitedKeys = stackalloc ulong[MaxSearchDepth];
            int visitedCount = 0;
            visitedKeys[visitedCount++] = rootBoard.ZobristKey;

            pvBoard.MakeMove(bestMove, out _);
            if (pvBoard.IsFiftyMoveRule() || pvBoard.IsThreefoldRepetition())
            {
                return pv;
            }
            visitedKeys[visitedCount++] = pvBoard.ZobristKey;

            for (int i = 1; i < MaxSearchDepth; i++)
            {
                if (!_tt.TryGet(pvBoard.ZobristKey, out TTEntry entry) || entry.BestMove.IsNull)
                {
                    break;
                }

                Move m = entry.BestMove;

                // Validate move legality
                if (!pvBoard.IsLegal(m)) break;

                pv.Add(m);
                pvBoard.MakeMove(m, out _);

                // Stop on repetition or 50-move rule
                if (pvBoard.IsFiftyMoveRule() || pvBoard.IsThreefoldRepetition() || visitedKeys[..visitedCount].Contains(pvBoard.ZobristKey))
                {
                    break;
                }

                if (visitedCount < visitedKeys.Length)
                {
                    visitedKeys[visitedCount++] = pvBoard.ZobristKey;
                }
            }

            return pv;
        }
    }
}
