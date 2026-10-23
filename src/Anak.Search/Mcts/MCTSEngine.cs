using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Primary Monte Carlo Tree Search (MCTS) engine with zero heap allocations during search,
/// PUCT exploration, 64-byte cacheline Transposition Table integration with DAG subtree reuse,
/// and dynamic UCI time management.
/// </summary>
public sealed class MCTSEngine : ISearchEngine
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

    private int _threads = 1;
    public int Threads
    {
        get => _threads;
        set => _threads = Math.Max(1, value);
    }

    private readonly int[,,] _history = new int[2, 64, 64];

    private void AgeHistory()
    {
        for (int c = 0; c < 2; c++)
            for (int f = 0; f < 64; f++)
                for (int t = 0; t < 64; t++)
                    _history[c, f, t] /= 2;
    }

    public void ClearHistory()
    {
        Array.Clear(_history);
    }



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

        IIncrementalEvaluator? incrementalEvaluator = _evaluator as IIncrementalEvaluator;
        incrementalEvaluator?.InitSearch(in rootBoard);
        if (_tt.HashfullPermille() == 0)
        {
            ClearHistory();
        }
        else
        {
            AgeHistory();
        }


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

        // Launch Lazy SMP helper threads if Threads > 1
        CancellationTokenSource? helperCts = null;
        Task[]? helperTasks = null;
        if (Threads > 1)
        {
            helperCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var helperToken = helperCts.Token;
            int helperCount = Threads - 1;
            helperTasks = new Task[helperCount];
            Board workerBoard = board;
            SearchLimits workerLimits = limits;
            for (int h = 0; h < helperCount; h++)
            {
                int tid = h + 1;
                helperTasks[h] = Task.Run(() => RunHelperSearchWorker(workerBoard, workerLimits, tid, helperToken), helperToken);
            }
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

        // Initialize root children priors with tactical policy heuristics
        InitializeChildPriors(in rootBoard, rootMoves.AsReadOnlySpan(), rootFirstChild, ttBestMove);

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
                Move childMove = _arena[bestChildIdx].Move;
                Piece movedPiece = currentBoard.GetPieceAt(childMove.From);
                currentBoard.MakeMove(childMove, out BoardState state);
                incrementalEvaluator?.PushMove(childMove, movedPiece, state.CapturedPiece, in currentBoard);
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

                        // Initialize leaf children priors with tactical policy heuristics
                        InitializeChildPriors(in currentBoard, leafMoves.AsReadOnlySpan(), leafFirstChild, ttHintMove);
                    }

                    // Evaluate leaf position using IEvaluator
                    Value eval = _evaluator.Evaluate(in currentBoard, leafMoves.AsReadOnlySpan());
                    int effectiveCp = eval.Raw;

                    // Asymmetric Tactical Watchdog & Quiescent Capture Resolution:
                    // Checks if side-to-move at leaf can immediately win hanging material or punish a bad exchange.
                    if (depth >= 1 && effectiveCp < 600)
                    {
                        effectiveCp = ResolveTacticalCaptures(in currentBoard, leafMoves.AsReadOnlySpan(), effectiveCp, multiTier);
                    }

                    // Map centipawn value to [-1.0, 1.0] using hyperbolic tangent
                    leafScore = MathF.Tanh(effectiveCp / 400.0f);

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

                if (i > 0 && currentReward > 0.0f)
                {
                    Move m = node.Move;
                    if (!m.IsNull && !m.IsCapture && !m.IsPromotion)
                    {
                        Color parentSide = (i % 2 == 1) ? rootBoard.SideToMove : rootBoard.SideToMove.Opponent();
                        int currentVal = _history[(int)parentSide, (int)m.From, (int)m.To];
                        if (currentVal < 4000)
                        {
                            _history[(int)parentSide, (int)m.From, (int)m.To] = currentVal + (int)(currentReward * 16.0f);
                        }
                    }
                }

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

                    if (maxVisits > 0)
                    {
                        node.BestChildOffset = bestOffset;
                        Move bestChildMove = _arena[fc + bestOffset].Move;

                        // DAG Transposition Table update
                        _tt.Store(keyPath[i], bestChildMove, node.Visits, node.Q, 0.0f, _tt.CurrentAge);
                    }
                    else
                    {
                        _tt.Store(keyPath[i], Move.Null, node.Visits, node.Q, 0.0f, _tt.CurrentAge);
                    }
                }

                // Negamax flip: parent perspective is inverted from child perspective
                currentReward = -currentReward;
            }

            incrementalEvaluator?.RollbackTo(0);


            iteration++;

            // --- D. PERIODIC TERMINATION CHECKS ---
            if (_timeManager.ShouldStop(iteration)) break;
            if (limits.HasNodeLimit && iteration >= limits.Nodes) break;
            if (limits.HasDepthLimit && maxSelDepth >= limits.Depth && iteration >= (ulong)limits.Depth * 250) break;
            if (_parameters.MaxNodes > 0 && iteration >= _parameters.MaxNodes) break;
            if (!_arena.CanAllocate(256)) break;

            // Smart dynamic early termination checks every 128 iterations (only when time-constrained)
            if ((iteration & 127) == 0 && iteration >= 256 && _timeManager.HasTimeLimit && !limits.HasNodeLimit)
            {
                ref readonly MCTSNode curRoot = ref _arena[rootIndex];
                float rootQ = curRoot.Q;

                // 1. Hopeless / Dead Lost Position: Root is losing heavily (Q < -0.75, ~ -450 cp)
                // Scale down soft limit to 25% (or 15% if Q < -0.90) so engine doesn't agonize when lost.
                if (rootQ < -0.75f)
                {
                    float scaleFactor = rootQ < -0.90f ? 0.15f : 0.25f;
                    _timeManager.ScaleSoftLimit(scaleFactor);
                }

                // 2. Dominant Best Move: Top child has >= 70% of visits and >= 3x second child
                ushort rnc = curRoot.NumChildren;
                if (iteration >= 384 && rnc > 1)
                {
                    int rfc = curRoot.FirstChildIndex;
                    uint topVisits = 0;
                    uint secondVisits = 0;
                    for (ushort c = 0; c < rnc; c++)
                    {
                        uint v = _arena[rfc + c].Visits;
                        if (v > topVisits)
                        {
                            secondVisits = topVisits;
                            topVisits = v;
                        }
                        else if (v > secondVisits)
                        {
                            secondVisits = v;
                        }
                    }

                    if (topVisits >= curRoot.Visits * 0.70f && topVisits >= secondVisits * 3)
                    {
                        if (_timeManager.ElapsedMilliseconds >= _timeManager.SoftLimitMs * 0.25f)
                        {
                            break;
                        }
                    }
                }
            }

            // Soft time limit check after completing a full iteration
            if (_timeManager.IsSoftTimeUp())
            {
                if (_timeManager.IsHardTimeUp()) break;

                // If root has a dominant best move, stop immediately.
                // If contested, extend search towards hard limit.
                ref readonly MCTSNode curR = ref _arena[rootIndex];
                if (curR.NumChildren <= 1) break;

                int rfc = curR.FirstChildIndex;
                uint topV = 0, secV = 0;
                for (ushort c = 0; c < curR.NumChildren; c++)
                {
                    uint v = _arena[rfc + c].Visits;
                    if (v > topV) { secV = topV; topV = v; }
                    else if (v > secV) { secV = v; }
                }

                if (topV >= secV * 2) break;
            }

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

        // Clean up Lazy SMP helper threads
        if (helperCts != null)
        {
            helperCts.Cancel();
            try { Task.WaitAll(helperTasks!, 50); } catch { }
            helperCts.Dispose();
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
                // 1. Identify maximum visits and primary greedy best child
                uint maxVisits = 0;
                float maxQ = float.NegativeInfinity;
                ushort bestChildOffset = 0;

                bool foundMate = false;
                for (ushort i = 0; i < numChildren; i++)
                {
                    ref readonly MCTSNode child = ref _arena[fc + i];
                    float parentQ = -child.Q;
                    if (parentQ >= 1.0f && child.Visits > 0)
                    {
                        if (!foundMate || child.Visits > maxVisits)
                        {
                            foundMate = true;
                            maxVisits = child.Visits;
                            maxQ = parentQ;
                            bestChildOffset = i;
                        }
                        continue;
                    }

                    if (!foundMate && (child.Visits > maxVisits || (child.Visits == maxVisits && parentQ > maxQ)))
                    {
                        maxVisits = child.Visits;
                        maxQ = parentQ;
                        bestChildOffset = i;
                    }
                }


                // 2. Elite-candidate sampling (if MoveRandomness > 0)
                // Refined tie-breaking (Updated #8): ONLY triggers when alternative moves are virtually identical in eval (< 2 cp)
                // and have substantial search visit confidence (>= 85% of max visits).
                if (_parameters.MoveRandomness > 0 && maxVisits > 0 && MathF.Abs(maxQ) < 0.80f)
                {
                    float marginFraction = Math.Clamp(_parameters.MoveRandomness / 100.0f, 0.0f, 0.50f);
                    float qTolerance = 0.008f + (0.055f * marginFraction);
                    float minEliteQ = maxQ - qTolerance;
                    uint minEliteVisits = Math.Max(2, (uint)(maxVisits * 0.25f));


                    // Identify alternative elite candidates (excluding primary best child)
                    Span<ushort> altOffsets = stackalloc ushort[numChildren];
                    Span<float> altWeights = stackalloc float[numChildren];
                    int altCount = 0;
                    float totalAltWeight = 0.0f;

                    for (ushort i = 0; i < numChildren; i++)
                    {
                        if (i == bestChildOffset) continue;

                        ref readonly MCTSNode child = ref _arena[fc + i];
                        float parentQ = -child.Q;
                        if (child.Visits >= minEliteVisits && parentQ >= minEliteQ)
                        {
                            altOffsets[altCount] = i;
                            float w = child.Visits;
                            altWeights[altCount] = w;
                            totalAltWeight += w;
                            altCount++;
                        }
                    }

                    if (altCount > 0 && totalAltWeight > 0.0f)
                    {
                        if (Random.Shared.NextSingle() < marginFraction)
                        {
                            float roll = Random.Shared.NextSingle() * totalAltWeight;
                            float accumulated = 0.0f;
                            for (int e = 0; e < altCount; e++)
                            {
                                accumulated += altWeights[e];
                                if (accumulated >= roll)
                                {
                                    selectedBestMove = _arena[fc + altOffsets[e]].Move;
                                    break;
                                }
                            }
                        }
                    }
                }

                // Fall back to greedy best child if single dominant move or MoveRandomness == 0
                if (selectedBestMove.IsNull)
                {
                    selectedBestMove = _arena[fc + bestChildOffset].Move;
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
            if (!selectedBestMove.IsNull && _arena[rootIndex].IsExpanded)
            {
                ref MCTSNode rNode = ref _arena[rootIndex];
                int rfc = rNode.FirstChildIndex;
                for (ushort c = 0; c < rNode.NumChildren; c++)
                {
                    if (_arena[rfc + c].Move == selectedBestMove)
                    {
                        rNode.BestChildOffset = c;
                        break;
                    }
                }
            }

            long elapsed = Math.Max(1, _timeManager.ElapsedMilliseconds);
            List<Move> pv = ExtractPv(rootIndex);
            if (!selectedBestMove.IsNull && pv.Count == 0)
            {
                pv.Add(selectedBestMove);
            }
            ulong nps = (iteration * 1000UL) / (ulong)elapsed;

            List<RootMoveCandidate> mctsTop = [];
            if (rootNode.IsExpanded && rootNode.NumChildren > 0)
            {
                var children = new List<(Move Move, uint Visits, float Q)>();
                for (ushort c = 0; c < rootNode.NumChildren; c++)
                {
                    ref readonly MCTSNode child = ref _arena[rootNode.FirstChildIndex + c];
                    if (child.Visits > 0 && !child.Move.IsNull)
                    {
                        children.Add((child.Move, child.Visits, child.Q));
                    }
                }
                foreach (var ch in children.OrderByDescending(c => c.Visits).Take(5))
                {
                    double winProb = Math.Clamp((ch.Q + 1.0) / 2.0, 0.001, 0.999);
                    int cp = (int)(400.0 * Math.Log10(winProb / (1.0 - winProb)));
                    mctsTop.Add(new RootMoveCandidate(ch.Move, cp, 0, [ch.Move]));
                }
            }

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
                RootScore = _arena[rootIndex].Q,
                TopMoves = mctsTop
            });
        }

        return selectedBestMove;
    }

    /// <summary>
    /// Selects the best child according to the PUCT formula:
    /// PUCT(s, a) = -Q(s, a) + c_puct * P(s, a) * (sqrt(sum_b N(s, b)) / (1 + N(s, a)))
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SelectBestChild(in MCTSNode parentNode, NodeArena? arena = null)
    {
        NodeArena a = arena ?? _arena;
        int firstChild = parentNode.FirstChildIndex;
        ushort numChildren = parentNode.NumChildren;

        if (firstChild < 0 || numChildren == 0) return -1;

        // Calculate sum_b N(s, b) across all children
        uint sumChildVisits = 0;
        for (int i = 0; i < numChildren; i++)
        {
            sumChildVisits += a[firstChild + i].Visits;
        }

        float parentVisitsSqrt = MathF.Sqrt(Math.Max(1.0f, sumChildVisits));
        float cpuct = _parameters.Cpuct;
        float defaultFpu = _parameters.FpuValue != 0.0f ? _parameters.FpuValue : parentNode.Q;

        float bestPuct = float.NegativeInfinity;
        int bestChildIdx = -1;

        for (int i = 0; i < numChildren; i++)
        {
            int childIdx = firstChild + i;
            ref readonly MCTSNode child = ref a[childIdx];

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
    /// Asymmetric Tactical Watchdog & 1-Ply Quiescent Capture Resolution:
    /// Checks if the side to move at the leaf has an immediate winning capture or promotion
    /// (e.g., capturing a hanging Queen/Rook or recapturing after an opponent's bad sacrifice).
    /// Returns the improved evaluation in centipawns from the leaf side-to-move's perspective.
    /// </summary>
    private static int ResolveTacticalCaptures(
        in Board board,
        ReadOnlySpan<Move> legalMoves,
        int baseCp,
        IMultiTierEvaluator? multiTier)
    {
        int bestGainCp = 0;
        Move bestCapture = Move.Null;

        for (int i = 0; i < legalMoves.Length; i++)
        {
            Move m = legalMoves[i];
            if (!m.IsCapture && !m.IsPromotion) continue;

            int netGain = StaticExchangeEvaluator.Evaluate(in board, m);
            if (netGain > bestGainCp)
            {
                bestGainCp = netGain;
                bestCapture = m;
            }
        }

        if (bestGainCp < 100)
        {
            return baseCp;
        }

        int tacticalCp = baseCp + (int)(bestGainCp * 0.90f);

        // Query position volatility to dynamically lower the Micro-NNUE cross-check threshold on sharp boards
        if (multiTier != null && !bestCapture.IsNull)
        {
            int volatility = multiTier.EvaluateVolatility(in board).Raw;
            int microThreshold = volatility >= 150 ? 100 : 200;

            if (bestGainCp >= microThreshold)
            {
                Board nextBoard = board;
                nextBoard.MakeMove(bestCapture, out _);
                int microAfterCp = -multiTier.EvaluateMicro(in nextBoard).Raw;
                if (microAfterCp > tacticalCp)
                {
                    tacticalCp = (tacticalCp + microAfterCp) / 2;
                }
            }
        }

        return Math.Max(baseCp, tacticalCp);
    }

    /// <summary>
    /// Initializes child nodes with tactically-informed policy priors (MVV-LVA captures, checks, promotions).
    /// Prevents uniform exploration across quiet moves during tactical conflicts.
    /// </summary>
    private void InitializeChildPriors(in Board board, ReadOnlySpan<Move> moves, int firstChildIdx, Move ttHintMove, NodeArena? arena = null)
    {
        NodeArena a = arena ?? _arena;
        Span<float> weights = stackalloc float[moves.Length];
        float totalWeight = 0.0f;

        for (int i = 0; i < moves.Length; i++)
        {
            float w = ComputeMoveHeuristicWeight(in board, moves[i]);
            weights[i] = w;
            totalWeight += w;
        }

        float invTotal = totalWeight > 0.0f ? 1.0f / totalWeight : (1.0f / moves.Length);

        for (int i = 0; i < moves.Length; i++)
        {
            ref MCTSNode child = ref a[firstChildIdx + i];
            child.Move = moves[i];
            child.FirstChildIndex = -1;

            float heuristicPrior = weights[i] * invTotal;

            if (ttHintMove != Move.Null)
            {
                if (child.Move == ttHintMove)
                {
                    child.Prior = Math.Max(heuristicPrior, 0.40f);
                }
                else if (moves.Length > 1)
                {
                    child.Prior = heuristicPrior * 0.60f;
                }
                else
                {
                    child.Prior = heuristicPrior;
                }
            }
            else
            {
                child.Prior = heuristicPrior;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ComputeMoveHeuristicWeight(in Board board, Move move)
    {
        float weight = 10.0f; // Base weight for quiet moves
        Color us = board.SideToMove;
        PieceType attackerType = board.GetPieceAt(move.From).Type();
        int attackerVal = StaticExchangeEvaluator.GetPieceValue(attackerType);

        if (move.IsPromotion)
        {
            weight += move.PromotionPieceType == PieceType.Queen ? 60.0f : 20.0f;
        }

        if (move.IsCapture)
        {
            PieceType victimType = move.IsEnPassant ? PieceType.Pawn : board.GetPieceAt(move.To).Type();
            int victimVal = StaticExchangeEvaluator.GetPieceValue(victimType);

            int seeGain = StaticExchangeEvaluator.Evaluate(in board, move);
            if (seeGain < 0)
            {
                // Defended lower-value piece (e.g. QxP defended): heavily suppress prior
                weight = seeGain <= -300 ? 0.5f : 2.0f;
            }
            else
            {
                weight += 20.0f;
                int mvvLva = (victimVal / 10) - (attackerVal / 100);
                weight += Math.Max(0, mvvLva);
                if (seeGain > 0)
                {
                    weight += 15.0f; // Winning exchange
                }
            }
        }
        else if (move.IsCastling)
        {
            weight += 15.0f;
        }
        else
        {
            // Quiet move: check if blundering into a losing capture
            if (!StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, move, -50))
            {
                weight = 1.0f; // Blundering piece
            }
            else
            {
                // History heuristic bonus
                int hist = _history[(int)us, (int)move.From, (int)move.To];
                if (hist > 0)
                {
                    weight += Math.Clamp(hist / 32.0f, 0.0f, 25.0f);
                }
            }
        }

        return weight;
    }

    /// <summary>
    /// Background worker executing MCTS simulations for Lazy SMP thread scaling.
    /// Shares the Transposition Table with the main search thread.
    /// </summary>
    private void RunHelperSearchWorker(Board board, SearchLimits limits, int threadId, CancellationToken cancellationToken)
    {
        NodeArena arena = new(500_000);
        Board rootBoard = board;

        MoveList rootMoves = default;
        rootBoard.GenerateLegalMoves(ref rootMoves);
        if (rootMoves.Count <= 1) return;

        if (!arena.AllocateBlock(1, out int rootIndex)) return;
        ref MCTSNode rootNode = ref arena[rootIndex];
        rootNode.Move = Move.Null;
        rootNode.Prior = 1.0f;

        if (!arena.AllocateBlock(rootMoves.Count, out int rootFirstChild)) return;
        rootNode.FirstChildIndex = rootFirstChild;
        rootNode.NumChildren = (ushort)rootMoves.Count;

        _tt.TryGet(rootBoard.ZobristKey, out TTEntry ttRoot);
        InitializeChildPriors(in rootBoard, rootMoves.AsReadOnlySpan(), rootFirstChild, ttRoot.BestMove, arena);

        Span<int> path = stackalloc int[MaxSearchDepth];
        Span<ulong> keyPath = stackalloc ulong[MaxSearchDepth];
        ulong iteration = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            path[0] = rootIndex;
            keyPath[0] = rootBoard.ZobristKey;
            int depth = 0;
            Board currentBoard = board;

            while (arena[path[depth]].IsExpanded && arena[path[depth]].NumChildren > 0)
            {
                int parentIdx = path[depth];
                ref readonly MCTSNode parentNode = ref arena[parentIdx];
                int bestChildIdx = SelectBestChild(in parentNode, arena);
                if (bestChildIdx < 0) break;

                depth++;
                if (depth >= MaxSearchDepth) break;

                path[depth] = bestChildIdx;
                currentBoard.MakeMove(arena[bestChildIdx].Move, out _);
                keyPath[depth] = currentBoard.ZobristKey;
            }

            int leafIdx = path[depth];
            ref MCTSNode leafNode = ref arena[leafIdx];
            float leafScore;

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
                    leafScore = currentBoard.IsInCheck() ? -1.0f : 0.0f;
                }
                else
                {
                    if (arena.CanAllocate(leafMoves.Count) && arena.AllocateBlock(leafMoves.Count, out int leafFirstChild))
                    {
                        leafNode.FirstChildIndex = leafFirstChild;
                        leafNode.NumChildren = (ushort)leafMoves.Count;

                        bool ttHit = _tt.TryGet(currentBoard.ZobristKey, out TTEntry ttLeaf);
                        Move ttHintMove = ttHit ? ttLeaf.BestMove : Move.Null;
                        InitializeChildPriors(in currentBoard, leafMoves.AsReadOnlySpan(), leafFirstChild, ttHintMove, arena);
                    }

                    Value eval = _evaluator.Evaluate(in currentBoard, leafMoves.AsReadOnlySpan());
                    leafScore = MathF.Tanh(eval.Raw / 400.0f);
                    _tt.Store(currentBoard.ZobristKey, Move.Null, 1, leafScore, 0.0f, _tt.CurrentAge);
                }
            }

            // Backpropagation
            float currentReward = leafScore;
            for (int i = depth; i >= 0; i--)
            {
                int nodeIdx = path[i];
                ref MCTSNode node = ref arena[nodeIdx];
                node.AddVisit(currentReward);

                if (node.IsExpanded && node.NumChildren > 0)
                {
                    ushort bestOffset = 0;
                    uint maxVisits = 0;
                    float maxQ = float.NegativeInfinity;
                    int fc = node.FirstChildIndex;

                    for (ushort c = 0; c < node.NumChildren; c++)
                    {
                        ref readonly MCTSNode child = ref arena[fc + c];
                        float parentQ = -child.Q;
                        if (child.Visits > maxVisits || (child.Visits == maxVisits && parentQ > maxQ))
                        {
                            maxVisits = child.Visits;
                            maxQ = parentQ;
                            bestOffset = c;
                        }
                    }

                    if (maxVisits > 0)
                    {
                        node.BestChildOffset = bestOffset;
                        Move bestChildMove = arena[fc + bestOffset].Move;
                        _tt.Store(keyPath[i], bestChildMove, node.Visits, node.Q, 0.0f, _tt.CurrentAge);
                    }
                    else
                    {
                        _tt.Store(keyPath[i], Move.Null, node.Visits, node.Q, 0.0f, _tt.CurrentAge);
                    }
                }

                currentReward = -currentReward;
            }

            iteration++;
            if (!arena.CanAllocate(256)) break;
        }
    }
}

