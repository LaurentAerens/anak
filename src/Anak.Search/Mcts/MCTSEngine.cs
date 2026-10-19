using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Primary Monte Carlo Tree Search (MCTS) engine with zero heap allocations during search,
/// PUCT exploration, 64-byte cacheline Transposition Table integration with DAG subtree reuse,
/// and dynamic UCI time management.
/// </summary>
public sealed class MCTSEngine
{
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;
    private readonly NodeArena _arena;
    private readonly TimeManager _timeManager;
    private readonly MCTSParameters _parameters;

    public const int MaxSearchDepth = 256;

    public MCTSParameters Parameters => _parameters;
    public TranspositionTable TranspositionTable => _tt;
    public NodeArena Arena => _arena;
    public TimeManager TimeManager => _timeManager;

    /// <summary>
    /// Constructs a new MCTS engine instance.
    /// </summary>
    /// <param name="evaluator">The evaluation function instance.</param>
    /// <param name="defaultHashMb">Default Transposition Table size in megabytes (default: 64 MB).</param>
    /// <param name="arenaCapacity">Capacity of the flat pre-allocated node arena (default: 2,000,000 nodes).</param>
    public MCTSEngine(IEvaluator evaluator, int defaultHashMb = TranspositionTable.DefaultSizeMb, int arenaCapacity = NodeArena.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _tt = new TranspositionTable(defaultHashMb);
        _arena = new NodeArena(arenaCapacity);
        _timeManager = new TimeManager();
        _parameters = new MCTSParameters();
    }

    /// <summary>
    /// Constructs a new MCTS engine with injected components.
    /// </summary>
    public MCTSEngine(IEvaluator evaluator, TranspositionTable tt, NodeArena arena, MCTSParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _tt = tt ?? new TranspositionTable();
        _arena = arena ?? new NodeArena();
        _timeManager = new TimeManager();
        _parameters = parameters ?? new MCTSParameters();
    }

    /// <summary>
    /// Executes Monte Carlo Tree Search for the given board position within the specified limits.
    /// Returns the best move selected by maximum visit count.
    /// </summary>
    /// <param name="board">Starting chess position.</param>
    /// <param name="limits">UCI search limits (time, nodes, depth, infinite).</param>
    /// <param name="infoCallback">Periodic UCI search diagnostic callback.</param>
    /// <param name="cancellationToken">Cancellation token to prematurely halt search.</param>
    /// <returns>Selected best move, or Move.Null if no legal moves exist.</returns>
    public Move Search(
        in Board board,
        in SearchLimits limits,
        Action<SearchStatistics>? infoCallback = null,
        CancellationToken cancellationToken = default)
    {
        // Advance TT generation
        _tt.NewSearch();

        // Reset arena for zero GC allocation
        _arena.Reset();

        // Initialize time manager
        _timeManager.Init(in limits, board.SideToMove);

        // 1. Generate root legal moves
        Board rootBoard = board;

        // Pin root topology for multi-tier neural models (Open vs Closed)
        IMultiTierEvaluator? multiTier = _evaluator as IMultiTierEvaluator;
        multiTier?.SetRootTopology(in rootBoard);

        MoveList rootMoves = default;
        rootBoard.GenerateLegalMoves(ref rootMoves);

        if (rootMoves.Count == 0)
        {
            return Move.Null;
        }

        // Single legal move fast-path if time limits apply
        if (rootMoves.Count == 1 && limits.HasTimeLimit && !limits.Infinite)
        {
            Move singleMove = rootMoves[0];
            infoCallback?.Invoke(new SearchStatistics
            {
                Depth = 1,
                SelDepth = 1,
                Nodes = 1,
                TimeMs = _timeManager.ElapsedMilliseconds,
                Nps = 0,
                BestMove = singleMove,
                Pv = [singleMove],
                HashfullPermille = _tt.HashfullPermille(),
                TbHits = 0,
                RootScore = 0.0f
            });
            return singleMove;
        }

        // 2. Allocate and initialize root node
        if (!_arena.AllocateBlock(1, out int rootIndex))
        {
            return rootMoves[0];
        }

        ref MCTSNode rootNode = ref _arena[rootIndex];
        rootNode.Move = Move.Null;
        rootNode.Prior = 1.0f;

        // Allocate root children in arena
        if (!_arena.AllocateBlock(rootMoves.Count, out int rootFirstChild))
        {
            return rootMoves[0];
        }

        rootNode.FirstChildIndex = rootFirstChild;
        rootNode.NumChildren = (ushort)rootMoves.Count;

        // Check TT for existing root stats (DAG transposition reuse)
        bool hasTtRoot = _tt.TryGet(rootBoard.ZobristKey, out TTEntry ttRoot);
        Move ttBestMove = hasTtRoot ? ttRoot.BestMove : Move.Null;

        // Initialize root children priors
        float basePrior = 1.0f / rootMoves.Count;
        for (int i = 0; i < rootMoves.Count; i++)
        {
            ref MCTSNode child = ref _arena[rootFirstChild + i];
            child.Move = rootMoves[i];
            child.FirstChildIndex = -1;

            if (ttBestMove != Move.Null && child.Move == ttBestMove)
            {
                child.Prior = Math.Max(basePrior, 0.40f);
            }
            else
            {
                child.Prior = (ttBestMove != Move.Null && rootMoves.Count > 1)
                    ? (0.60f / (rootMoves.Count - 1))
                    : basePrior;
            }
        }

        // 3. MCTS Simulation Loop
        ulong iteration = 0;
        int maxSelDepth = 0;
        long lastInfoTime = 0;

        Span<int> path = stackalloc int[MaxSearchDepth];
        Span<ulong> keyPath = stackalloc ulong[MaxSearchDepth];

        while (!cancellationToken.IsCancellationRequested)
        {
            path[0] = rootIndex;
            keyPath[0] = rootBoard.ZobristKey;
            int depth = 0;

            Board currentBoard = board;

            // --- A. SELECTION (PUCT) ---
            while (_arena[path[depth]].IsExpanded && _arena[path[depth]].NumChildren > 0)
            {
                int parentIdx = path[depth];
                ref readonly MCTSNode parentNode = ref _arena[parentIdx];

                int bestChildIdx = SelectBestChild(parentNode);
                if (bestChildIdx < 0) break;

                depth++;
                if (depth >= MaxSearchDepth) break;

                path[depth] = bestChildIdx;
                currentBoard.MakeMove(_arena[bestChildIdx].Move, out _);
                keyPath[depth] = currentBoard.ZobristKey;
            }

            if (depth > maxSelDepth)
            {
                maxSelDepth = depth;
            }

            // --- B. LEAF EVALUATION & EXPANSION ---
            int leafIdx = path[depth];
            ref MCTSNode leafNode = ref _arena[leafIdx];
            float leafScore;

            // Check for draw rules
            if (currentBoard.IsThreefoldRepetition() || currentBoard.IsFiftyMoveRule())
            {
                leafScore = 0.0f;
            }
            else
            {
                MoveList leafMoves = default;
                currentBoard.GenerateLegalMoves(ref leafMoves);

                if (leafMoves.Count == 0)
                {
                    // Terminal state
                    if (currentBoard.IsInCheck())
                    {
                        // Checkmate: side to move lost
                        leafScore = -1.0f;
                    }
                    else
                    {
                        // Stalemate: draw
                        leafScore = 0.0f;
                    }
                }
                else
                {
                    // Expand leaf if arena has sufficient space
                    if (_arena.CanAllocate(leafMoves.Count) && _arena.AllocateBlock(leafMoves.Count, out int leafFirstChild))
                    {
                        leafNode.FirstChildIndex = leafFirstChild;
                        leafNode.NumChildren = (ushort)leafMoves.Count;

                        // Check TT for child moves to guide policy prior and DAG reuse
                        bool ttHit = _tt.TryGet(currentBoard.ZobristKey, out TTEntry ttLeaf);
                        Move ttHintMove = ttHit ? ttLeaf.BestMove : Move.Null;

                        // DAG Transposition Subtree Reuse: Seed leaf visits and value if previously explored
                        if (ttHit && ttLeaf.Visits > 0 && leafNode.Visits == 0)
                        {
                            leafNode.Visits = ttLeaf.Visits;
                            leafNode.TotalValue = ttLeaf.Value * ttLeaf.Visits;
                        }

                        float uniformPrior = 1.0f / leafMoves.Count;
                        for (int i = 0; i < leafMoves.Count; i++)
                        {
                            ref MCTSNode child = ref _arena[leafFirstChild + i];
                            child.Move = leafMoves[i];
                            child.FirstChildIndex = -1;

                            if (ttHintMove != Move.Null && child.Move == ttHintMove)
                            {
                                child.Prior = Math.Max(uniformPrior, 0.40f);
                            }
                            else
                            {
                                child.Prior = (ttHintMove != Move.Null && leafMoves.Count > 1)
                                    ? (0.60f / (leafMoves.Count - 1))
                                    : uniformPrior;
                            }
                        }
                    }

                    // Evaluate leaf position using IEvaluator
                    Value eval = _evaluator.Evaluate(in currentBoard, leafMoves.AsReadOnlySpan());

                    // Map centipawn value to [-1.0, 1.0] using hyperbolic tangent
                    leafScore = MathF.Tanh(eval.Raw / 400.0f);

                    // Asymmetric Tactical Watchdog (Micro-NNUE): check for opponent traps on promising deep nodes
                    if (depth >= 4 && leafScore >= 0.45f && multiTier != null)
                    {
                        if (VerifyTacticalSafety(in currentBoard, multiTier, out float penalty))
                        {
                            leafScore = penalty;
                        }
                    }

                    // Transposition Table store with DAG reuse
                    _tt.Store(currentBoard.ZobristKey, Move.Null, 1, leafScore, 0.0f, _tt.CurrentAge);
                }
            }

            // --- C. BACKPROPAGATION ---
            float currentReward = leafScore;
            for (int i = depth; i >= 0; i--)
            {
                int nodeIdx = path[i];
                ref MCTSNode node = ref _arena[nodeIdx];
                node.AddVisit(currentReward);

                // Update BestChildOffset and TT entry
                if (node.IsExpanded && node.NumChildren > 0)
                {
                    ushort bestOffset = 0;
                    uint maxVisits = 0;
                    float maxQ = float.NegativeInfinity;
                    int fc = node.FirstChildIndex;

                    for (ushort c = 0; c < node.NumChildren; c++)
                    {
                        ref readonly MCTSNode child = ref _arena[fc + c];
                        float parentQ = -child.Q;
                        if (child.Visits > maxVisits || (child.Visits == maxVisits && parentQ > maxQ))
                        {
                            maxVisits = child.Visits;
                            maxQ = parentQ;
                            bestOffset = c;
                        }
                    }

                    node.BestChildOffset = bestOffset;
                    Move bestChildMove = _arena[fc + bestOffset].Move;

                    // DAG Transposition Table update
                    _tt.Store(keyPath[i], bestChildMove, node.Visits, node.Q, 0.0f, _tt.CurrentAge);
                }

                // Negamax flip: parent perspective is inverted from child perspective
                currentReward = -currentReward;
            }

            iteration++;

            // --- D. PERIODIC TERMINATION CHECKS ---
            if (_timeManager.ShouldStop(iteration)) break;
            if (limits.HasNodeLimit && iteration >= limits.Nodes) break;
            if (limits.HasDepthLimit && maxSelDepth >= limits.Depth && iteration >= (ulong)limits.Depth * 250) break;
            if (_parameters.MaxNodes > 0 && iteration >= _parameters.MaxNodes) break;
            if (!_arena.CanAllocate(256)) break;

            // Soft time limit check after completing a full iteration
            if (_timeManager.IsSoftTimeUp()) break;

            // --- E. PERIODIC INFO REPORTING ---
            long elapsed = _timeManager.ElapsedMilliseconds;
            if (infoCallback != null && (elapsed - lastInfoTime >= 100 || (iteration & 2047) == 0))
            {
                lastInfoTime = elapsed;
                List<Move> pv = ExtractPv(rootIndex);
                int depthReached = pv.Count;
                ulong nps = elapsed > 0 ? (iteration * 1000UL) / (ulong)elapsed : 0;
                Move curBest = pv.Count > 0 ? pv[0] : Move.Null;

                infoCallback(new SearchStatistics
                {
                    Depth = depthReached,
                    SelDepth = maxSelDepth,
                    Nodes = iteration,
                    TimeMs = elapsed,
                    Nps = nps,
                    BestMove = curBest,
                    Pv = pv,
                    HashfullPermille = _tt.HashfullPermille(),
                    TbHits = 0,
                    RootScore = _arena[rootIndex].Q
                });

                if (limits.HasDepthLimit && depthReached >= limits.Depth)
                {
                    break;
                }
            }
        }

        // --- 4. BEST MOVE EXTRACTION ---
        ref readonly MCTSNode finalRoot = ref _arena[rootIndex];
        Move selectedBestMove = Move.Null;

        if (finalRoot.IsExpanded && finalRoot.NumChildren > 0)
        {
            int fc = finalRoot.FirstChildIndex;
            ushort numChildren = finalRoot.NumChildren;

            if (_parameters.Temperature <= 0.0f)
            {
                // Greedy selection: pick child with highest visits (tie-break by Q)
                uint maxVisits = 0;
                float maxQ = float.NegativeInfinity;

                for (ushort i = 0; i < numChildren; i++)
                {
                    ref readonly MCTSNode child = ref _arena[fc + i];
                    float parentQ = -child.Q;
                    if (child.Visits > maxVisits || (child.Visits == maxVisits && parentQ > maxQ))
                    {
                        maxVisits = child.Visits;
                        maxQ = parentQ;
                        selectedBestMove = child.Move;
                    }
                }
            }
            else
            {
                // Temperature sampling: select child with probability proportional to N^(1/T)
                float invTemp = 1.0f / _parameters.Temperature;
                Span<float> weights = stackalloc float[numChildren];
                float totalWeight = 0.0f;

                for (int i = 0; i < numChildren; i++)
                {
                    ref readonly MCTSNode child = ref _arena[fc + i];
                    float w = child.Visits > 0 ? MathF.Pow(child.Visits, invTemp) : 0.0f;
                    weights[i] = w;
                    totalWeight += w;
                }

                if (totalWeight > 0.0f)
                {
                    float threshold = Random.Shared.NextSingle() * totalWeight;
                    float cumSum = 0.0f;
                    for (int i = 0; i < numChildren; i++)
                    {
                        cumSum += weights[i];
                        if (cumSum >= threshold)
                        {
                            selectedBestMove = _arena[fc + i].Move;
                            break;
                        }
                    }
                }

                if (selectedBestMove == Move.Null)
                {
                    selectedBestMove = _arena[fc + finalRoot.BestChildOffset].Move;
                }
            }

            if (selectedBestMove == Move.Null && numChildren > 0)
            {
                selectedBestMove = _arena[fc].Move;
            }
        }
        else
        {
            selectedBestMove = rootMoves.Count > 0 ? rootMoves[0] : Move.Null;
        }

        // Final info report
        if (infoCallback != null)
        {
            long elapsed = Math.Max(1, _timeManager.ElapsedMilliseconds);
            List<Move> pv = ExtractPv(rootIndex);
            ulong nps = (iteration * 1000UL) / (ulong)elapsed;

            infoCallback(new SearchStatistics
            {
                Depth = pv.Count,
                SelDepth = maxSelDepth,
                Nodes = iteration,
                TimeMs = elapsed,
                Nps = nps,
                BestMove = selectedBestMove,
                Pv = pv,
                HashfullPermille = _tt.HashfullPermille(),
                TbHits = 0,
                RootScore = _arena[rootIndex].Q
            });
        }

        return selectedBestMove;
    }

    /// <summary>
    /// Selects the best child according to the PUCT formula:
    /// PUCT(s, a) = -Q(s, a) + c_puct * P(s, a) * (sqrt(sum_b N(s, b)) / (1 + N(s, a)))
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SelectBestChild(in MCTSNode parentNode)
    {
        int firstChild = parentNode.FirstChildIndex;
        ushort numChildren = parentNode.NumChildren;

        if (firstChild < 0 || numChildren == 0) return -1;

        // Calculate sum_b N(s, b) across all children
        uint sumChildVisits = 0;
        for (int i = 0; i < numChildren; i++)
        {
            sumChildVisits += _arena[firstChild + i].Visits;
        }

        float parentVisitsSqrt = MathF.Sqrt(Math.Max(1.0f, sumChildVisits));
        float cpuct = _parameters.Cpuct;
        float defaultFpu = _parameters.FpuValue != 0.0f ? _parameters.FpuValue : parentNode.Q;

        float bestPuct = float.NegativeInfinity;
        int bestChildIdx = -1;

        for (int i = 0; i < numChildren; i++)
        {
            int childIdx = firstChild + i;
            ref readonly MCTSNode child = ref _arena[childIdx];

            // Invert child's perspective Q to match parent's perspective.
            // Unvisited children use defaultFpu which is already in parent's perspective.
            float childQ = child.Visits > 0 ? -child.Q : defaultFpu;

            // Exploration term U(s, a)
            float u = cpuct * child.Prior * (parentVisitsSqrt / (1.0f + child.Visits));
            float puctScore = childQ + u;

            if (puctScore > bestPuct)
            {
                bestPuct = puctScore;
                bestChildIdx = childIdx;
            }
        }

        return bestChildIdx;
    }

    /// <summary>
    /// Extracts the Principal Variation (PV) by following BestChildOffset links from the root.
    /// </summary>
    private List<Move> ExtractPv(int rootIndex)
    {
        List<Move> pv = new(16);
        int currentIdx = rootIndex;

        while (currentIdx >= 0 && currentIdx < _arena.UsedCount && pv.Count < MaxSearchDepth)
        {
            ref readonly MCTSNode node = ref _arena[currentIdx];
            if (!node.IsExpanded || node.NumChildren == 0 || node.Visits == 0) break;

            int bestChildIdx = node.FirstChildIndex + node.BestChildOffset;
            if (bestChildIdx < 0 || bestChildIdx >= _arena.UsedCount) break;

            ref readonly MCTSNode bestChild = ref _arena[bestChildIdx];
            if (bestChild.Visits == 0 || bestChild.Move.IsNull) break;

            pv.Add(bestChild.Move);
            currentIdx = bestChildIdx;
        }

        return pv;
    }

    /// <summary>
    /// Asymmetric Tactical Watchdog: performs a fast 1-ply tactical burst on aggressive opponent candidate replies
    /// using the Micro-NNUE model to verify there are no hidden tactical refutations (blunders, forks, hanging pieces).
    /// </summary>
    private static bool VerifyTacticalSafety(in Board board, IMultiTierEvaluator multiTier, out float penalty)
    {
        penalty = 0.0f;
        MoveList opponentMoves = default;
        Board copy = board;
        copy.GenerateLegalMoves(ref opponentMoves);

        if (opponentMoves.Count == 0) return false;

        for (int i = 0; i < opponentMoves.Count && i < 16; i++)
        {
            Move m = opponentMoves[i];
            if (m.IsCapture || m.IsPromotion)
            {
                Board oppBoard = copy;
                oppBoard.MakeMove(m, out _);
                Value oppMicroVal = multiTier.EvaluateMicro(in oppBoard);

                // oppMicroVal is from opponent's perspective.
                // If opponent evaluation is heavily winning (>= 200 cp), our move walked into a blunder.
                if (oppMicroVal.Raw >= 200)
                {
                    penalty = -MathF.Tanh(oppMicroVal.Raw / 400.0f);
                    return true;
                }
            }
        }

        return false;
    }
}
