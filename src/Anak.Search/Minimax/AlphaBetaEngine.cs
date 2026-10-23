using System.Diagnostics;
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
///   - Cascading Micro-NNUE fast pre-screen via IMultiTierEvaluator.EvaluateMicro
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
public sealed class AlphaBetaEngine : ISearchEngine
{
    public const int MaxSearchDepth = 128;
    public const int MaxStackSize = 256;

    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;
    private readonly TimeManager _timeManager;

    private int _threads = 1;
    private volatile bool _stopped;
    private SearchWorker[] _workers;

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
    private readonly int[,] _lmrTable = new int[MaxSearchDepth, 128];

    public TranspositionTable TranspositionTable => _tt;
    public TimeManager TimeManager => _timeManager;
    public IEvaluator Evaluator => _evaluator;
    public int LastScoreCp { get; internal set; }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetLmrReduction(int depth, int moveIndex)
    {
        int d = Math.Clamp(depth, 0, MaxSearchDepth - 1);
        int m = Math.Clamp(moveIndex, 0, 127);
        return _lmrTable[d, m];
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
                _lmrTable[depth, moves] = (int)reduction;
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
        var workers = new SearchWorker[count];
        for (int i = 0; i < count; i++)
        {
            workers[i] = new SearchWorker(i, this, _evaluator, _tt);
        }
        _workers = workers;
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
    /// Executes Alpha-Beta / PVS search for the given board position within the specified limits.
    /// </summary>
    public Move Search(
        in Board board,
        in SearchLimits limits,
        Action<SearchStatistics>? infoCallback = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Advance TT generation age
        _tt.NewSearch();

        // 2. Classify and pin root neural expert if multi-tier evaluator
        if (_evaluator is IMultiTierEvaluator multiTier)
        {
            multiTier.SetRootTopology(in board);
        }

        // 3. Initialize time manager
        _timeManager.Init(in limits, board.SideToMove);
        _stopped = false;

        // 4. Ensure workers array matches thread count
        if (_workers.Length != _threads)
        {
            InitWorkers(_threads);
        }

        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i].ResetForSearch();
        }

        // 5. Check if position has zero legal moves
        MoveList legalMoves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);
        if (legalMoves.Count == 0)
        {
            return Move.Null;
        }

        if (legalMoves.Count == 1)
        {
            // Only one legal move: if time limit or shallow search requested, we can still report depth 1
            if (limits.HasDepthLimit && limits.Depth == 1)
            {
                Move singleMove = legalMoves[0];
                infoCallback?.Invoke(new SearchStatistics
                {
                    Depth = 1,
                    SelDepth = 1,
                    Nodes = 1,
                    BestMove = singleMove,
                    Pv = [singleMove],
                    ScoreCp = 0,
                    RootScore = 0.0f
                });
                return singleMove;
            }
        }

        // 6. Launch Lazy SMP helper threads if Threads > 1
        Task[]? helperTasks = null;
        if (_threads > 1)
        {
            SearchLimits searchLimits = limits;
            helperTasks = new Task[_threads - 1];
            for (int i = 1; i < _threads; i++)
            {
                int workerId = i;
                Board helperBoard = board;
                helperTasks[i - 1] = Task.Run(() =>
                {
                    _workers[workerId].RunHelperSearch(in helperBoard, in searchLimits, cancellationToken);
                }, cancellationToken);
            }
        }

        // 7. Run main search on worker 0
        Move bestMove = _workers[0].RunMainSearch(in board, in limits, infoCallback, cancellationToken);

        // 8. Search finished: stop all helper workers
        _stopped = true;
        if (helperTasks != null)
        {
            try
            {
                Task.WaitAll(helperTasks, 500);
            }
            catch
            {
                // Helper worker task exceptions or cancellations are safely swallowed
            }
        }

        return bestMove;
    }

    /// <summary>
    /// Gets the aggregate nodes searched across all workers.
    /// </summary>
    public ulong GetTotalNodes()
    {
        ulong total = 0;
        for (int i = 0; i < _workers.Length; i++)
        {
            total += _workers[i].Nodes;
        }
        return total;
    }

    /// <summary>
    /// Internal search worker containing per-thread state, stacks, and history tables.
    /// Pre-allocated once to guarantee zero heap allocations during search.
    /// </summary>
    private sealed class SearchWorker
    {
        public readonly int Id;
        private readonly AlphaBetaEngine _engine;
        private readonly IEvaluator _evaluator;
        private readonly IIncrementalEvaluator? _incEval;
        private readonly TranspositionTable _tt;
        private readonly QuiescenceSearch _qsearch;

        public ulong Nodes;
        public int SelDepth;
        public ulong BetaCutoffs;
        public ulong FirstMoveCutoffs;

        private readonly SearchStack[] _stack;
        private readonly HistoryTable _history;
        private readonly CaptureHistory _captureHistory;
        private readonly ContinuationHistory _continuationHistory;
        private readonly CorrectionHistory _correctionHistory;
        private readonly Move[,] _counterMoves;
        private readonly List<RootMoveCandidate> _rootCandidates = [];

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
            _counterMoves = new Move[13, 64];
            _qsearch = new QuiescenceSearch(_evaluator, tt, _correctionHistory, _incEval);
            _qsearch.DeltaSafetyMargin = engine.QsDeltaMargin;
        }

        public void SetQsDeltaMargin(int margin)
        {
            _qsearch.DeltaSafetyMargin = margin;
        }

        public void ResetForSearch()
        {
            Nodes = 0;
            SelDepth = 0;
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

            // Probe TT for root move initially
            if (_tt.TryGet(board.ZobristKey, out TTEntry rootEntry) && !rootEntry.BestMove.IsNull)
            {
                bestMove = rootEntry.BestMove;
            }

            // Fallback move if TT has no entry yet
            if (bestMove.IsNull)
            {
                MoveList legal = default;
                MoveGenerator.GenerateLegalMoves(in board, ref legal);
                if (legal.Count > 0)
                {
                    bestMove = legal[0];
                }
            }

            long lastIterElapsed = 0;
            _incEval?.InitSearch(in board);

            // --- Iterative Deepening ---
            for (int currentDepth = 1; currentDepth <= maxDepth; currentDepth++)
            {
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
                    if (currentElapsed >= _engine._timeManager.SoftLimitMs)
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

                    while (true)
                    {
                        score = SearchRoot(ref board, currentDepth, alpha, beta, out iterBestMove, cancellationToken);

                        if (_engine._stopped || cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (score <= alpha)
                        {
                            // Fail-low: widen alpha, avoid narrowing beta to prevent oscillations
                            alpha = Math.Max(-Value.InfiniteValue, alpha - delta);
                            delta += delta + delta / 2;
                        }
                        else if (score >= beta)
                        {
                            // Fail-high: widen beta
                            beta = Math.Min(Value.InfiniteValue, beta + delta);
                            delta += delta + delta / 2;
                        }
                        else
                        {
                            // Score is within window
                            break;
                        }

                        if (delta > 2000)
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
                if (_rootCandidates.Count > 0)
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

        /// <summary>
        /// Helper search loop for Lazy SMP workers.
        /// Helper threads run iterative deepening independently, populating the shared TT.
        /// </summary>
        public void RunHelperSearch(in Board rootBoard, in SearchLimits limits, CancellationToken cancellationToken)
        {
            Board board = rootBoard;
            int maxDepth = limits.HasDepthLimit ? Math.Min(limits.Depth, MaxSearchDepth) : MaxSearchDepth;

            // Offset helper start depth slightly to increase branch coverage diversity
            int startDepth = 1 + (Id % 2);
            _incEval?.InitSearch(in board);

            for (int currentDepth = startDepth; currentDepth <= maxDepth; currentDepth++)
            {
                if (_engine._stopped || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                SearchRoot(ref board, currentDepth, -Value.InfiniteValue, Value.InfiniteValue, out _, cancellationToken);
            }
        }

        /// <summary>
        /// Root search with PVS move ordering and TT update.
        /// </summary>
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

            _tt.TryGet(key, out TTEntry entry);
            Move ttMove = entry.BestMove;

            _stack[0].Reset();
            _stack[0].InCheck = board.IsInCheck();

            // Root move picker
            var picker = new MovePicker(
                in board,
                ttMove,
                killer0: Move.Null,
                killer1: Move.Null,
                counterMove: Move.Null,
                _history,
                _captureHistory,
                _continuationHistory);

            int bestScore = -Value.InfiniteValue;
            int movesSearched = 0;

            Span<Move> quietsSearched = stackalloc Move[64];
            Span<Piece> quietsSearchedPiece = stackalloc Piece[64];
            int quietsCount = 0;
            float rootOpenness = depth >= 3 ? PawnTopology.CalculateOpenness(in board) : 0.5f;

            if (Id == 0)
            {
                _rootCandidates.Clear();
            }

            while (picker.Next(out Move move))
            {
                Piece movedPiece = board.GetPieceAt(move.From);
                board.MakeMove(move, out BoardState state);
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
                    if (depth >= 3 && movesSearched > 1 && !move.IsCapture && !move.IsPromotion)
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

                    if (score > alpha && searchDepth < nextDepth)
                    {
                        score = -AlphaBeta(ref board, nextDepth, -alpha - 1, -alpha, ply: 1, isPv: false, cancellationToken);
                    }

                    if (score > alpha)
                    {
                        // Full window re-search on PVS fail-high
                        score = -AlphaBeta(ref board, nextDepth, -beta, -alpha, ply: 1, isPv: true, cancellationToken);
                    }
                }

                board.UnmakeMove(move, in state);
                _incEval?.RollbackTo(0);

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
                            UpdateHistoriesOnCutoff(in board, move, movedPiece, depth, quietsSearched[..quietsCount], quietsSearchedPiece[..quietsCount], prev1Piece: Piece.None, prev1To: Square.None, prev2Piece: Piece.None, prev2To: Square.None);
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
            _tt.Store(key, storeMove, (byte)Math.Clamp(depth, 0, 255), TTEntry.ScoreToTt(bestScore, 0), _tt.CurrentAge, flag);

            return bestScore;
        }

        /// <summary>
        /// Negamax search with Principal Variation Search, Transposition Table, and dynamic pruning.
        /// </summary>
        private int AlphaBeta(
            ref Board board,
            int depth,
            int alpha,
            int beta,
            int ply,
            bool isPv,
            CancellationToken cancellationToken)
        {
            if (_engine._stopped || cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            // Update selective depth
            if (ply > SelDepth)
            {
                SelDepth = ply;
            }

            // Check detection & check extension
            bool inCheck = board.IsInCheck();
            _stack[ply].InCheck = inCheck;

            if (inCheck && depth < MaxSearchDepth)
            {
                depth++;
            }

            // 1. Quiescence Search at leaf nodes
            if (depth <= 0)
            {
                return _qsearch.Search(ref board, alpha, beta, ply);
            }

            // Stack limit safety
            if (ply >= MaxSearchDepth)
            {
                return _evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty).Raw;
            }

            // Periodic time management & cancellation check (dynamically masked for low-time safety)
            if ((Nodes & _engine._timeManager.CheckMask) == 0)
            {
                if (_engine._stopped ||
                    cancellationToken.IsCancellationRequested ||
                    _engine._timeManager.ShouldStop(Nodes) ||
                    _engine._timeManager.IsHardTimeUp())
                {
                    _engine._stopped = true;
                    return 0;
                }
            }

            int alphaOrig = alpha;
            ulong key = board.ZobristKey;

            // 2. Mate Distance Pruning
            int alphaM = Math.Max(alpha, -Value.MateValue + ply);
            int betaM = Math.Min(beta, Value.MateValue - (ply + 1));
            if (alphaM >= betaM)
            {
                return alphaM;
            }
            alpha = alphaM;
            beta = betaM;

            // 3. Draw Detection (Threefold repetition and Fifty-move rule)
            if (ply > 0)
            {
                if (board.IsFiftyMoveRule() || board.IsThreefoldRepetition() || board.HasInsufficientMaterial())
                {
                    return 0;
                }
            }

            // 4. Transposition Table Probe
            bool ttHit = _tt.TryGet(key, out TTEntry entry);
            Move ttMove = ttHit ? entry.BestMove : Move.Null;
            int ttScore = ttHit ? TTEntry.ScoreFromTt(entry.Score, ply) : 0;

            // TT Cutoff for non-PV nodes
            if (!isPv && ttHit && entry.Depth >= depth && _stack[ply].ExcludedMove.IsNull)
            {
                if (entry.Flags == TTFlags.Exact) return ttScore;
                if (entry.Flags == TTFlags.LowerBound && ttScore >= beta) return ttScore;
                if (entry.Flags == TTFlags.UpperBound && ttScore <= alpha) return ttScore;
            }

            // 5. Static Evaluation
            int staticEval = -Value.InfiniteValue;
            int eval = -Value.InfiniteValue;
            if (!inCheck)
            {
                int rawEval = _evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty).Raw;
                staticEval = _correctionHistory.GetAdjustedEval(in board, rawEval);
                eval = staticEval;

                if (ttHit)
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

            // 6. Reverse Futility Pruning (Static Null Move Pruning) for depth <= 4
            if (!isPv && !inCheck && depth <= 4 && _stack[ply].ExcludedMove.IsNull)
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

            float openness = (depth >= 2 && !inCheck) ? PawnTopology.CalculateOpenness(in board) : 0.5f;

            // 7. Adaptive Null Move Pruning (NMP)
            if (!isPv && !inCheck && depth >= 2 && eval >= beta &&
                _stack[ply].ExcludedMove.IsNull &&
                (ply >= 1 && _stack[ply - 1].CurrentMove != Move.Null) &&
                SearchHeuristics.HasNonPawnMaterial(in board, board.SideToMove))
            {
                int r = SearchHeuristics.GetNmpReduction(depth, eval, beta, openness, _engine.NmpBase);
                int nullDepth = depth - 1 - r;

                board.MakeNullMove(out BoardState nullState);
                _incEval?.PushNullMove(in board);
                _stack[ply].CurrentMove = Move.Null;
                _stack[ply].MovedPiece = Piece.None;

                int nullScore = -AlphaBeta(ref board, nullDepth, -beta, -beta + 1, ply + 1, isPv: false, cancellationToken);
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

            // 10. Singular Extensions
            int singularExtension = 0;
            if (depth >= 6 && ply > 0 && ttHit && !entry.BestMove.IsNull &&
                entry.Depth >= depth - 3 && entry.Flags != TTFlags.UpperBound &&
                _stack[ply].ExcludedMove.IsNull)
            {
                int singularBeta = ttScore - (_engine.SingularMargin * depth);
                _stack[ply].ExcludedMove = entry.BestMove;
                int sDepth = (depth - 1) / 2;

                int sScore = AlphaBeta(ref board, sDepth, singularBeta - 1, singularBeta, ply, isPv: false, cancellationToken);
                _stack[ply].ExcludedMove = Move.Null;

                if (sScore < singularBeta)
                {
                    singularExtension = (!isPv && ttScore >= beta) ? 2 : 1;
                }
            }

            // 11. Move Ordering with staged MovePicker
            Piece prev1Piece = ply >= 1 ? _stack[ply - 1].MovedPiece : Piece.None;
            Square prev1To = ply >= 1 && !_stack[ply - 1].CurrentMove.IsNull ? _stack[ply - 1].CurrentMove.To : Square.None;
            Piece prev2Piece = ply >= 2 ? _stack[ply - 2].MovedPiece : Piece.None;
            Square prev2To = ply >= 2 && !_stack[ply - 2].CurrentMove.IsNull ? _stack[ply - 2].CurrentMove.To : Square.None;

            Move counterMove = (prev1Piece != Piece.None && prev1To != Square.None)
                ? _counterMoves[ContinuationHistory.PieceToIndex(prev1Piece), (int)prev1To]
                : Move.Null;

            var picker = new MovePicker(
                in board,
                ttMove,
                _stack[ply].Killer0,
                _stack[ply].Killer1,
                counterMove,
                _history,
                _captureHistory,
                _continuationHistory,
                prev1Piece,
                prev1To,
                prev2Piece,
                prev2To);

            int bestScore = -Value.InfiniteValue;
            Move bestMove = Move.Null;
            int movesSearched = 0;

            Span<Move> quietsSearched = stackalloc Move[64];
            Span<Piece> quietsSearchedPiece = stackalloc Piece[64];
            int quietsCount = 0;
            while (picker.Next(out Move move))
            {
                // Skip singular extension excluded move
                if (move == _stack[ply].ExcludedMove)
                {
                    continue;
                }

                // Bad Captures Pruning at shallow depth
                if (!isPv && !inCheck && depth <= 2 && picker.Stage == MovePickerStage.BadCaptures)
                {
                    break;
                }

                bool isQuiet = !move.IsCapture && !move.IsPromotion;

                // Shallow Futility Pruning for non-tactical quiet moves at shallow depth
                if (!isPv && !inCheck && isQuiet && movesSearched > 0)
                {
                    if (depth <= 2 && eval + (150 * depth) <= alpha)
                    {
                        continue;
                    }
                    if (depth == 3 && staticEval + (80 * depth) <= alpha)
                    {
                        continue;
                    }
                }

                // Late Move Pruning (LMP) for quiet moves
                if (!isPv && !inCheck && depth <= 3 && isQuiet)
                {
                    int lmpThreshold = 3 + (2 * depth * depth);
                    if (movesSearched >= lmpThreshold)
                    {
                        continue;
                    }
                }

                Piece movedPiece = board.GetPieceAt(move.From);
                board.MakeMove(move, out BoardState state);
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
                    score = -AlphaBeta(ref board, nextDepth, -beta, -alpha, ply + 1, isPv, cancellationToken);
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

                            int hist = _history.GetScore(movedPiece.Color(), move);
                            if (hist < -_engine.HistoryThreshold) r++;
                            else if (hist > _engine.HistoryThreshold) r--;

                            int conthist1 = _continuationHistory.Get1PlyScore(prev1Piece, prev1To, movedPiece, move.To);
                            if (conthist1 > _engine.ConthistReward) r--;
                            else if (conthist1 < -_engine.ConthistPenalty) r++;

                            r = Math.Clamp(r, 0, depth - 2);
                        }
                        else if (move.IsCapture && !move.IsPromotion && movesSearched > 4)
                        {
                            // Capture LMR: 1-ply reduction for non-winning captures searched late
                            if (StaticExchangeEvaluator.Evaluate(in board, move) <= 0)
                            {
                                r = 1;
                            }
                        }
                    }

                    int searchDepth = Math.Max(0, nextDepth - r);
                    score = -AlphaBeta(ref board, searchDepth, -alpha - 1, -alpha, ply + 1, isPv: false, cancellationToken);

                    if (score > alpha && searchDepth < nextDepth)
                    {
                        score = -AlphaBeta(ref board, nextDepth, -alpha - 1, -alpha, ply + 1, isPv: false, cancellationToken);
                    }

                    if (isPv && score > alpha)
                    {
                        // PVS re-search with full window on fail-high
                        score = -AlphaBeta(ref board, nextDepth, -beta, -alpha, ply + 1, isPv: true, cancellationToken);
                    }
                }

                board.UnmakeMove(move, in state);
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
                            UpdateHistoriesOnCutoff(in board, move, movedPiece, depth, quietsSearched[..quietsCount], quietsSearchedPiece[..quietsCount], prev1Piece, prev1To, prev2Piece, prev2To);

                            // Killer move storage in SearchStack
                            if (!move.IsCapture && !move.IsPromotion)
                            {
                                _stack[ply].AddKiller(move);
                            }

                            // Correction history update on cutoff
                            if (!inCheck && staticEval != -Value.InfiniteValue)
                            {
                                _correctionHistory.Update(in board, score, staticEval);
                            }

                            // Transposition Table LowerBound store
                            if (_stack[ply].ExcludedMove.IsNull)
                            {
                                _tt.Store(key, move, (byte)Math.Clamp(depth, 0, 255), TTEntry.ScoreToTt(score, ply), _tt.CurrentAge, TTFlags.LowerBound);
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

            // Correction history update on non-cutoff nodes
            if (!inCheck && staticEval != -Value.InfiniteValue && bestScore > -Value.MateValue + 100 && bestScore < Value.MateValue - 100)
            {
                _correctionHistory.Update(in board, bestScore, staticEval);
            }

            // Transposition Table store (Exact or UpperBound)
            if (_stack[ply].ExcludedMove.IsNull)
            {
                TTFlags flag = (bestScore > alphaOrig) ? TTFlags.Exact : TTFlags.UpperBound;
                Move storeMove = !bestMove.IsNull ? bestMove : ttMove;
                _tt.Store(key, storeMove, (byte)Math.Clamp(depth, 0, 255), TTEntry.ScoreToTt(bestScore, ply), _tt.CurrentAge, flag);
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
            Piece prev1Piece,
            Square prev1To,
            Piece prev2Piece,
            Square prev2To)
        {
            int bonus = HistoryTable.CalculateBonus(depth);

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
                    _counterMoves[ContinuationHistory.PieceToIndex(prev1Piece), (int)prev1To] = move;
                }

                // 4. Malus for quiet moves that failed to cause a cutoff
                for (int i = 0; i < quietsSearched.Length; i++)
                {
                    Move failed = quietsSearched[i];
                    if (failed != move)
                    {
                        Piece failedPiece = quietsSearchedPiece[i];
                        _history.Update(board.SideToMove, failed, -bonus);
                        _continuationHistory.Update1Ply(prev1Piece, prev1To, failedPiece, failed.To, -bonus);
                        _continuationHistory.Update2Ply(prev2Piece, prev2To, failedPiece, failed.To, -bonus);
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
        }

        /// <summary>
        /// Traces the Principal Variation sequence through the Transposition Table.
        /// </summary>
        private List<Move> ExtractPv(in Board rootBoard, Move bestMove)
        {
            var pv = new List<Move>(MaxSearchDepth);
            if (!bestMove.IsNull)
            {
                pv.Add(bestMove);
            }

            Board pvBoard = rootBoard;
            Span<ulong> visitedKeys = stackalloc ulong[MaxSearchDepth];
            int visitedCount = 0;
            visitedKeys[visitedCount++] = rootBoard.ZobristKey;

            if (!bestMove.IsNull)
            {
                pvBoard.MakeMove(bestMove, out _);
                visitedKeys[visitedCount++] = pvBoard.ZobristKey;
            }

            for (int i = 1; i < MaxSearchDepth; i++)
            {
                if (!_tt.TryGet(pvBoard.ZobristKey, out TTEntry entry) || entry.BestMove.IsNull)
                {
                    break;
                }

                Move m = entry.BestMove;

                // Validate move legality
                MoveList legal = default;
                MoveGenerator.GenerateLegalMoves(in pvBoard, ref legal);
                bool isLegal = false;
                for (int j = 0; j < legal.Count; j++)
                {
                    if (legal[j] == m)
                    {
                        isLegal = true;
                        break;
                    }
                }

                if (!isLegal) break;

                pv.Add(m);
                pvBoard.MakeMove(m, out _);

                // Stop on repetition or 50-move rule
                if (pvBoard.IsFiftyMoveRule() || visitedKeys[..visitedCount].Contains(pvBoard.ZobristKey))
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
