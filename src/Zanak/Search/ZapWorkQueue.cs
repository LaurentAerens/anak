using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Zanak.Search;

public readonly record struct ZapPickupTrace(
    uint Generation, ulong ZobristKey, int Ply, ZapAgentRole Role, int RootIndex,
    long PriorityClass, long Responsibility, long Tactics, long Proximity, long SubspineGap,
    long EstimatedCost, long ProgressPenalty, long SpineScore, long Diversity, long Total,
    int ExploredSiblings, int RemainingSiblings);

/// <summary>
/// Thread-safe global crosspoint table that deduplicates positions by ZobristKey and ply,
/// tracks multi-spine references (LinkedSpinesMask),
/// and ranks candidates with SPSA-tunable priority.
/// Every traced position (root spines and agent subspines) is an entry, so the table grows for the
/// whole search: pickup uses one heap per root spine (static priority) and only the heap tops are
/// compared with the live spine score and pickup count.
/// </summary>
public sealed class ZapWorkQueue
{
    private readonly List<ZapCrosspoint> _entries = new(256);
    private readonly List<int> _versions = new(256);
    private readonly Dictionary<ulong, int> _indexByKey = new(256);
    private readonly PriorityQueue<(int Index, int Version), long>[] _heaps = new PriorityQueue<(int, int), long>[64];
    private readonly int[] _spinePickCount = new int[64];
    private readonly object _lock = new();
    private readonly ZapParams _params;
    private int _unpicked;
    public event Action<ZapPickupTrace>? Picked;

    public ZapWorkQueue(ZapParams? parameters = null)
    {
        _params = parameters ?? new ZapParams();
        for (int i = 0; i < _heaps.Length; i++) _heaps[i] = new PriorityQueue<(int, int), long>();
    }

    public int Count
    {
        get { lock (_lock) return _unpicked; }
    }

    public bool IsEmpty => Count == 0;

    public ZapCrosspoint[] Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    internal void EnqueueTrajectory(in Board startBoard, int rootIndex, ReadOnlySpan<ZapTrajectoryStep> steps,
        Color engineColor, uint generation, int startingPly = 1, ZapAgentRole? role = null, bool immediateOnly = false,
        bool prevWasCapture = false)
    {
        Board board = startBoard;
        int[] responsibility = ComputeResponsibility(steps, engineColor, startingPly, _params);
        int maxResponsibilityAbove = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            if (immediateOnly && i > 0) break;
            ZapTrajectoryStep step = steps[i];
            if (step.ZobristKey != board.ZobristKey) break;
            Board nextBoard = board;
            bool hasNext = nextBoard.MakeMove(step.Move, out _);
            ZapAgentRole positionRole = step.SideToMove == engineColor ? ZapAgentRole.Promotion : ZapAgentRole.Refutation;
            if (role == null || role == positionRole)
            {
                int ply = startingPly + i;
                bool immediate = ply == 1 && positionRole == ZapAgentRole.Refutation;
                int boost = (immediate ? 400 : 0) + (Math.Abs((int)step.StaticEval) >= 28000 ? 500 : 0);
                int malus = i == 0 ? 0 : (int)((long)maxResponsibilityAbove * _params.CrosspointAboveShockMalusWeight / 100);
                short shock = (short)Math.Clamp((long)responsibility[i] + boost - malus, short.MinValue, short.MaxValue);
                bool stepPrevCapture = i == 0 ? prevWasCapture : steps[i - 1].Move.IsCapture;
                Enqueue(new ZapCrosspoint(board, in nextBoard, hasNext, rootIndex, ply, steps[^1].StaticEval, shock,
                    positionRole == ZapAgentRole.Refutation ? CrosspointKind.AdversarialRefutation : CrosspointKind.Opportunity,
                    Math.Max(1, steps.Length - i), step.Move, _params.CrosspointRootBiasWeight,
                    generation, positionRole, step.ParentEval, stepPrevCapture, _params));
            }
            maxResponsibilityAbove = Math.Max(maxResponsibilityAbove, responsibility[i]);
            if (!hasNext) break;
            board = nextBoard;
        }
    }

    /// <summary>
    /// Queues every position of an agent subspine (a child edge of a picked crosspoint), except the
    /// picked position itself. Entries keep only their place in the spine tree; the board is rebuilt
    /// when an agent picks them up. Returns the number of positions offered.
    /// </summary>
    internal int EnqueueSubspine(in Board nodeBoard, ZapSpineTree.Edge edge, ulong linkedSpinesMask, int nodePly,
        int spineGap, Color engineColor, uint generation)
    {
        ZapTrajectoryStep[] steps = edge.Steps;
        if (steps.Length < 2) return 0;
        if (spineGap > 0 && AtOffLineSoftCap) return 0;
        int[] responsibility = ComputeResponsibility(steps, engineColor, nodePly, _params);
        short gap = (short)Math.Clamp(spineGap, 0, short.MaxValue);
        short baseline = steps[^1].StaticEval;
        Board board = nodeBoard;
        int offered = 0;
        int maxResponsibilityAbove = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            ZapTrajectoryStep step = steps[i];
            if (step.ZobristKey != board.ZobristKey) break;
            Board nextBoard = default;
            bool hasNext = false;
            if (i > 0 || i + 1 < steps.Length)
            {
                nextBoard = board;
                hasNext = nextBoard.MakeMove(step.Move, out _);
            }
            if (i > 0)
            {
                if (AtCapacity || (gap > 0 && AtOffLineSoftCap)) break;
                ZapAgentRole role = step.SideToMove == engineColor ? ZapAgentRole.Promotion : ZapAgentRole.Refutation;
                int boost = Math.Abs((int)step.StaticEval) >= 28000 ? 500 : 0;
                int malus = (int)((long)maxResponsibilityAbove * _params.CrosspointAboveShockMalusWeight / 100);
                short shock = (short)Math.Clamp((long)responsibility[i] + boost - malus, short.MinValue, short.MaxValue);
                Enqueue(new ZapCrosspoint(board, in nextBoard, hasNext, edge, i, linkedSpinesMask, nodePly + i, baseline, step.ParentEval,
                    shock, gap, Math.Max(1, steps.Length - i), step.Move, generation, role,
                    steps[i - 1].Move.IsCapture, _params));
                offered++;
            }
            maxResponsibilityAbove = Math.Max(maxResponsibilityAbove, responsibility[i]);
            if (i + 1 < steps.Length)
            {
                if (!hasNext) break;
                board = nextBoard;
            }
        }
        return offered;
    }

    private bool AtCapacity
    {
        get { lock (_lock) return _entries.Count >= _params.MaxQueueEntries; }
    }

    private bool AtOffLineSoftCap
    {
        get { lock (_lock) return _entries.Count >= _params.MaxQueueEntries / 2; }
    }

    /// <summary>
    /// Attributes each eval swing to the decisions responsible for it rather than to the move that
    /// cashed it in. A swing in favour of the mover is a debt owed by the victim: the victim's move
    /// just before it (which allowed it) and the victim's following moves that leave it unpaid get
    /// the blame, the first unpaid reply most. The capturing move itself carries no blame of its own.
    /// This only ranks pickup; every position stays an eligible crosspoint.
    /// </summary>
    internal static int[] ComputeResponsibility(ReadOnlySpan<ZapTrajectoryStep> steps, Color engineColor,
        int startingPly, ZapParams parameters)
    {
        int n = steps.Length;
        var blame = new int[n];
        for (int j = 0; j < n; j++)
        {
            int gain = Gain(steps[j], engineColor);
            int threshold = parameters.AdaptiveShockBaseCp + (startingPly + j - 1) * parameters.AdaptiveShockSlopeCp;
            if (gain < threshold) continue;

            if (j > 0) blame[j - 1] += gain;
            int debt = gain;
            int weightPct = 100;
            for (int k = j + 1; k < n && weightPct >= 5; k += 2)
            {
                int repaid = Gain(steps[k], engineColor);
                int residual = debt - Math.Max(0, repaid);
                if (residual <= 0) break;
                blame[k] += residual * weightPct / 100;
                // A further loss by the victim deepens the debt it still has to pay back.
                debt = residual + Math.Max(0, -repaid);
                weightPct = weightPct * parameters.CrosspointDebtDecayPct / 100;
            }
        }
        for (int j = 0; j < n; j++)
        {
            int gain = Gain(steps[j], engineColor);
            int drop = -gain;
            if (drop >= parameters.ShockThresholdCp ||
                (!steps[j].Move.IsCapture && drop >= parameters.QuietEvalDropMinCp))
            {
                // Telemetry shows negative mover deltas directly predict Stockfish rank drops even without an immediate capture reply.
                blame[j] = Math.Max(blame[j], drop);
            }
            blame[j] = Math.Min(short.MaxValue, blame[j]);
        }
        return blame;

        static int Gain(in ZapTrajectoryStep step, Color engineColor) =>
            step.SideToMove == engineColor ? step.EvalDelta : -step.EvalDelta;
    }

    /// <summary>
    /// Enqueues or links a crosspoint into the deduplicated table.
    /// If an entry with the same ZobristKey exists for the same generation, merges LinkedSpinesMask
    /// and takes the best shock magnitude / priority. An explored entry is reopened only when it is
    /// offered with a longer horizon (a deeper spine); a different continuation alone does not reopen it,
    /// since the agent already traced every surviving sibling there.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(in ZapCrosspoint crosspoint)
    {
        lock (_lock)
        {
            // A crosspoint represents a specific position in the search tree (ZobristKey + Ply).
            // When multiple spines encounter the same position at the same ply, deduplicate and merge LinkedSpinesMask.
            ulong positionId = crosspoint.ZobristKey ^ ((ulong)crosspoint.Ply << 56);
            if (_indexByKey.TryGetValue(positionId, out int existingIdx))
            {
                ref ZapCrosspoint existing = ref CollectionsMarshal.AsSpan(_entries)[existingIdx];
                if (existing.Generation == crosspoint.Generation)
                {
                    bool changed = (crosspoint.LinkedSpinesMask & ~existing.LinkedSpinesMask) != 0;
                    existing.LinkedSpinesMask |= crosspoint.LinkedSpinesMask;
                    if (crosspoint.ShockMagnitude > existing.ShockMagnitude)
                    {
                        existing.ShockMagnitude = crosspoint.ShockMagnitude;
                        changed = true;
                    }
                    if (crosspoint.TacticalPriority > existing.TacticalPriority)
                    {
                        existing.TacticalPriority = crosspoint.TacticalPriority;
                        changed = true;
                    }
                    if (crosspoint.SpineGap < existing.SpineGap)
                    {
                        existing.SpineGap = crosspoint.SpineGap;
                        changed = true;
                    }
                    existing.PriorityScore = Math.Max(existing.PriorityScore, crosspoint.PriorityScore);
                    if (!existing.HasBoard && crosspoint.HasBoard)
                    {
                        existing.ShareBoard(crosspoint.BoardBox);
                        existing.TreeEdge = null;
                    }
                    if (crosspoint.RemainingHorizon > existing.RemainingHorizon)
                    {
                        existing.Revision++;
                        existing.Work = null;
                        existing.RemainingHorizon = crosspoint.RemainingHorizon;
                        existing.ParentEval = crosspoint.ParentEval;
                        existing.OriginalMove = crosspoint.OriginalMove;
                        existing.BaselineScore = crosspoint.BaselineScore;
                        if (!existing.HasBoard)
                        {
                            existing.TreeEdge = crosspoint.TreeEdge;
                            existing.TreeStep = crosspoint.TreeStep;
                        }
                        if (existing.Picked && !existing.InFlight)
                        {
                            ReopenedEntryCount++;
                            existing.Picked = false;
                            _unpicked++;
                        }
                        changed = true;
                    }
                    MergedEntryCount++;
                    if (changed && !existing.Picked) Push(existingIdx);
                    return;
                }
            }

            if (_entries.Count >= _params.MaxQueueEntries)
            {
                CapacityRejectedCount++;
                return;
            }
            NewEntryCount++;
            Add(positionId, crosspoint);
        }
    }

    /// <summary>
    /// Back credit: re-ranks an earlier spine decision after an agent changed the line below it.
    /// A previously explored entry is reopened (a bounded number of times) because its agent compared
    /// against a spine score that is now stale. Only pickup order changes; scores are untouched.
    /// </summary>
    internal void Reconsider(in ZapCrosspoint crosspoint, int boost)
    {
        lock (_lock)
        {
            ulong positionId = crosspoint.ZobristKey ^ ((ulong)crosspoint.Ply << 56);
            if (_indexByKey.TryGetValue(positionId, out int existingIdx))
            {
                ref ZapCrosspoint existing = ref CollectionsMarshal.AsSpan(_entries)[existingIdx];
                if (existing.Generation == crosspoint.Generation)
                {
                    existing.LinkedSpinesMask |= crosspoint.LinkedSpinesMask;
                    existing.ShockMagnitude = (short)Math.Min(short.MaxValue, existing.ShockMagnitude + boost);
                    if (!existing.HasBoard && crosspoint.HasBoard)
                    {
                        existing.ShareBoard(crosspoint.BoardBox);
                        existing.TreeEdge = null;
                    }
                    if (existing.OriginalMove != crosspoint.OriginalMove)
                    {
                        existing.Revision++;
                        existing.Work = null;
                        existing.OriginalMove = crosspoint.OriginalMove;
                        existing.RemainingHorizon = crosspoint.RemainingHorizon;
                        existing.BaselineScore = crosspoint.BaselineScore;
                        existing.ParentEval = crosspoint.ParentEval;
                    }
                    if (existing.Picked && existing.ReopenCount < _params.CrosspointMaxReopens)
                    {
                        existing.ReopenCount++;
                        existing.Revision++;
                        existing.Work = null;
                        if (!existing.InFlight)
                        {
                            existing.Picked = false;
                            _unpicked++;
                        }
                    }
                    if (!existing.Picked) Push(existingIdx);
                    return;
                }
            }

            ZapCrosspoint fresh = crosspoint;
            fresh.ShockMagnitude = (short)Math.Min(short.MaxValue, Math.Max((int)fresh.ShockMagnitude, boost));
            fresh.Picked = false;
            Add(positionId, fresh);
        }
    }

    private void Add(ulong positionId, in ZapCrosspoint crosspoint)
    {
        int idx = _entries.Count;
        _entries.Add(crosspoint);
        _versions.Add(0);
        _indexByKey[positionId] = idx;
        if (!crosspoint.Picked)
        {
            _unpicked++;
            Push(idx);
        }
    }

    private void Push(int idx)
    {
        ZapCrosspoint item = _entries[idx];
        int version = ++CollectionsMarshal.AsSpan(_versions)[idx];
        long key = -StaticPriority(in item);
        ulong mask = item.LinkedSpinesMask == 0 ? 1UL : item.LinkedSpinesMask;
        while (mask != 0)
        {
            int r = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            _heaps[r].Enqueue((idx, version), key);
        }
    }

    /// <summary>Priority that does not depend on live search state: delta/blame, tactics, closeness to root, subspine gap.</summary>
    private long StaticPriority(in ZapCrosspoint item)
    {
        long nearRootBonus = item.Ply switch
        {
            1 => 20,
            2 => 10,
            _ => 0,
        };
        long priority = (long)item.ShockMagnitude * _params.CrosspointDeltaWeight / 100
            + item.TacticalPriority
            + nearRootBonus
            + Math.Max(0, 32 - item.Ply) * _params.CrosspointRootBiasWeight
            - (long)item.SpineGap * _params.CrosspointSubspineGapWeight / 100
            - EstimatedCost(in item)
            - ProgressPenalty(in item);
        bool isImmediateRefutation = item.Ply == 1 &&
            item.Kind == CrosspointKind.AdversarialRefutation &&
            item.Role == ZapAgentRole.Refutation &&
            (item.Work?.Batches ?? 0) == 0;
        return isImmediateRefutation ? priority + _params.ImmediateRefutationBonus : priority;
    }

    private long EstimatedCost(in ZapCrosspoint item)
    {
        int remaining = item.Work is { } work ? work.Moves.Length - work.Next : _params.MaxSiblingExploration;
        return (long)item.RemainingHorizon * Math.Min(Math.Max(1, _params.MaxSiblingExploration), remaining)
            * _params.CrosspointCostWeight;
    }

    private long ProgressPenalty(in ZapCrosspoint item) =>
        (long)(item.Work?.Batches ?? 0) * _params.CrosspointDiversityPenalty;

    internal bool IsCurrent(in ZapCrosspoint cp)
    {
        lock (_lock)
        {
            ulong key = ZapSpineTree.Key(cp.ZobristKey, cp.Ply);
            return !_indexByKey.TryGetValue(key, out int index) ||
                (_entries[index].Generation == cp.Generation && _entries[index].Revision == cp.Revision);
        }
    }

    internal void FinishBatch(in ZapCrosspoint cp, ZapSiblingWork? work, bool pending)
    {
        lock (_lock)
        {
            ulong key = ZapSpineTree.Key(cp.ZobristKey, cp.Ply);
            if (!_indexByKey.TryGetValue(key, out int index))
            {
                if (!pending) return;
                ZapCrosspoint resumed = cp;
                resumed.Work = work;
                resumed.Picked = false;
                resumed.InFlight = false;
                Enqueue(in resumed);
                return;
            }
            ref ZapCrosspoint existing = ref CollectionsMarshal.AsSpan(_entries)[index];
            if (existing.Generation != cp.Generation) return;
            existing.InFlight = false;
            bool revised = existing.Revision != cp.Revision;
            existing.Work = revised || !pending ? null : work;
            if (pending || revised)
            {
                existing.Picked = false;
                _unpicked++;
                Push(index);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out ZapCrosspoint crosspoint)
    {
        return TryDequeue(out crosspoint, null, 0);
    }

    /// <summary>
    /// Attempts to dequeue the highest-priority crosspoint: static priority plus, for each linked
    /// root spine, that spine's live score minus a diversity penalty for crosspoints already picked
    /// from it. Demoted spines are never evicted; their lower score only delays pickup.
    /// </summary>
    public bool TryDequeue(out ZapCrosspoint crosspoint, ZapSharedState? sharedState, uint currentGeneration, int excludedRoot = -1)
    {
        ZapPickupTrace? trace = null;
        lock (_lock)
        {
            int root = 0;
            int rootScore = 0;
            int bestIdx = sharedState == null
                ? FindBestLinear(currentGeneration, excludedRoot)
                : FindBestFromHeaps(sharedState, currentGeneration, out root, out rootScore, excludedRoot);

            if (bestIdx >= 0)
            {
                ref ZapCrosspoint chosen = ref CollectionsMarshal.AsSpan(_entries)[bestIdx];
                if (sharedState == null) root = chosen.RootCandidateIndex;
                if (Picked != null)
                {
                    long responsibility = (long)chosen.ShockMagnitude * _params.CrosspointDeltaWeight / 100;
                    long proximity = Math.Max(0, 32 - chosen.Ply) * _params.CrosspointRootBiasWeight;
                    long gap = (long)chosen.SpineGap * _params.CrosspointSubspineGapWeight / 100;
                    long cost = EstimatedCost(in chosen);
                    long progress = ProgressPenalty(in chosen);
                    long score = (long)(sharedState == null ? chosen.BaselineScore : rootScore) * _params.CrosspointSpineEvalWeight / 100;
                    long diversity = (long)_spinePickCount[root] * _params.CrosspointDiversityPenalty;
                    long priority = StaticPriority(in chosen);
                    trace = new ZapPickupTrace(chosen.Generation, chosen.ZobristKey, chosen.Ply, chosen.Role, root,
                        priority - responsibility - chosen.TacticalPriority - proximity + gap + cost + progress,
                        responsibility, chosen.TacticalPriority, proximity, gap, cost, progress, score, diversity,
                        priority + score - diversity, chosen.Work?.Next ?? 0,
                        chosen.Work is { } work ? work.Moves.Length - work.Next : -1);
                }
                chosen.Picked = true;
                chosen.InFlight = true;
                _unpicked--;
                DequeuedCount++;
                ulong pickedMask = chosen.LinkedSpinesMask;
                while (pickedMask != 0)
                {
                    int chosenRoot = BitOperations.TrailingZeroCount(pickedMask);
                    pickedMask &= pickedMask - 1;
                    _spinePickCount[chosenRoot]++;
                }
                crosspoint = chosen;
            }
            else
            {
                crosspoint = default;
                return false;
            }
        }
        if (trace is { } value) Picked?.Invoke(value);
        return true;
    }

    internal void DecaySpinePickCount(int rootIndex)
    {
        if ((uint)rootIndex >= (uint)_spinePickCount.Length) return;
        lock (_lock)
        {
            _spinePickCount[rootIndex] /= 2;
        }
    }

    private int FindBestFromHeaps(ZapSharedState sharedState, uint currentGeneration, out int bestRoot, out int bestSpineScore, int excludedRoot = -1)
    {
        bestRoot = 0;
        bestSpineScore = 0;
        Span<ZapCrosspoint> span = CollectionsMarshal.AsSpan(_entries);
        Span<int> versions = CollectionsMarshal.AsSpan(_versions);
        int candidateCount = sharedState.CandidateCount;
        int leaderIdx = sharedState.BestCandidateIndex;
        int leaderScore = (uint)leaderIdx < (uint)candidateCount ? sharedState.GetScore(leaderIdx) : 0;
        int bestIdx = -1;
        long bestPriority = long.MinValue;
        for (int r = 0; r < _heaps.Length; r++)
        {
            if (r == excludedRoot) continue;
            PriorityQueue<(int Index, int Version), long> heap = _heaps[r];
            int top = -1;
            long negKey = 0;
            while (heap.TryPeek(out (int Index, int Version) element, out negKey))
            {
                ref ZapCrosspoint item = ref span[element.Index];
                if (item.Picked || versions[element.Index] != element.Version)
                {
                    heap.Dequeue();
                    continue;
                }
                if (currentGeneration != 0 && item.Generation != currentGeneration)
                {
                    item.Picked = true;
                    _unpicked--;
                    EvictedStaleCount++;
                    heap.Dequeue();
                    continue;
                }
                top = element.Index;
                break;
            }
            if (top < 0) continue;

            int spineScore = r < candidateCount ? sharedState.GetScore(r) : span[top].BaselineScore;
            long roleBoost = 0;
            if (span[top].Role == ZapAgentRole.Refutation && r == leaderIdx)
            {
                roleBoost = 25;
            }
            else if (span[top].Role == ZapAgentRole.Promotion && r != leaderIdx && leaderScore - spineScore <= _params.HardAspirationMarginCp)
            {
                roleBoost = 20;
            }
            long priority = -negKey
                + (long)spineScore * _params.CrosspointSpineEvalWeight / 100
                + roleBoost
                - (long)_spinePickCount[r] * _params.CrosspointDiversityPenalty;
            if (bestIdx < 0 || priority > bestPriority)
            {
                bestPriority = priority;
                bestIdx = top;
                bestRoot = r;
                bestSpineScore = spineScore;
            }
        }
        return bestIdx;
    }

    private int FindBestLinear(uint currentGeneration, int excludedRoot = -1)
    {
        Span<ZapCrosspoint> span = CollectionsMarshal.AsSpan(_entries);
        int bestIdx = -1;
        long bestPriority = long.MinValue;
        for (int i = 0; i < span.Length; i++)
        {
            ref ZapCrosspoint item = ref span[i];
            if (item.Picked) continue;
            if (item.RootCandidateIndex == excludedRoot) continue;
            if (currentGeneration != 0 && item.Generation != currentGeneration)
            {
                item.Picked = true;
                _unpicked--;
                EvictedStaleCount++;
                continue;
            }

            int root = item.RootCandidateIndex;
            long priority = StaticPriority(in item)
                + (long)item.BaselineScore * _params.CrosspointSpineEvalWeight / 100
                - (long)_spinePickCount[root] * _params.CrosspointDiversityPenalty;
            if (bestIdx < 0 || priority > bestPriority)
            {
                bestPriority = priority;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _versions.Clear();
            _indexByKey.Clear();
            foreach (var heap in _heaps) heap.Clear();
            Array.Clear(_spinePickCount, 0, _spinePickCount.Length);
            _unpicked = 0;
            DequeuedCount = 0;
            EvictedStaleCount = 0;
            NewEntryCount = 0;
            MergedEntryCount = 0;
            ReopenedEntryCount = 0;
            CapacityRejectedCount = 0;
        }
    }

    public int TotalEntries
    {
        get { lock (_lock) return _entries.Count; }
    }

    public int NewEntryCount { get; private set; }
    public int MergedEntryCount { get; private set; }
    public int ReopenedEntryCount { get; private set; }
    public int CapacityRejectedCount { get; private set; }

    public int DequeuedCount { get; private set; }
    public int EvictedStaleCount { get; private set; }
}
