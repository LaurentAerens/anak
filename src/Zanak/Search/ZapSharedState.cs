using System.Runtime.CompilerServices;
using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// Status of a root move candidate during gated agent exploration.
/// </summary>
public enum RootMoveStatus : byte
{
    Active = 0,
    Refuted = 1,
    Dead = 2
}

/// <summary>
/// Root move candidate tracked throughout iterative discrepancy search.
/// </summary>
public struct ZapRootCandidate
{
    public Move Move;
    public int Score;
    public bool IsDeadStem;
    public RootMoveStatus Status;
    public int CompletedDepth;
    public Move BestFollowup;
    public ZapTrajectoryStep[] Trajectory;
    public string ScoreHistory;

    /// <summary>Agent-quality (mini-search) screen score of the 1-ply move; only valid when <see cref="HasScreen"/>.</summary>
    public int ScreenScore;
    public bool HasScreen;

    /// <summary>Best (lowest) score proven by a refutation agent: later spine re-deepening may not rise above it.</summary>
    public int AgentCeiling;

    /// <summary>Best (highest) score proven by a promotion agent: later spine re-deepening may not fall below it.</summary>
    public int AgentFloor;

    public ZapRootCandidate(Move move, int score)
    {
        ScreenScore = 0;
        HasScreen = false;
        AgentCeiling = int.MaxValue;
        AgentFloor = int.MinValue;
        Move = move;
        Score = score;
        IsDeadStem = false;
        Status = RootMoveStatus.Active;
        CompletedDepth = 0;
        BestFollowup = Move.Null;
        Trajectory = [];
        ScoreHistory = $"Init:{score} ";
    }
}

/// <summary>
/// Thread-safe shared search state maintaining root bounds, candidate evaluations,
/// total node counters, dynamic root move eviction statuses, and search cancellation.
/// </summary>
public sealed class ZapSharedState
{
    private readonly object _syncLock = new();
    private ZapRootCandidate[] _candidates = [];
    private int[] _rootCompletedCrosspoints = [];
    private uint _generation;
    private int _bestCandidateIndex;
    private long _totalNodes;
    private int _completedDepth;
    private volatile bool _isStopped;
    private int _killMarginCp = 600;

    public long TotalNodes => Volatile.Read(ref _totalNodes);
    public int CompletedDepth => Volatile.Read(ref _completedDepth);
    public bool IsStopped => _isStopped;
    public int CandidateCount => _candidates.Length;
    public uint Generation => Volatile.Read(ref _generation);
    public event Action<ZapAgentTrace>? AgentTraced;

    internal void TraceAgent(in ZapCrosspoint cp, int baseline, int score, Move alternative,
        bool completed, int appliedRoots, ReadOnlySpan<ZapTrajectoryStep> trajectory)
    {
        if (AgentTraced is { } handler)
        {
            handler(new ZapAgentTrace(cp.Generation, cp.ZobristKey, cp.LinkedSpinesMask, cp.Ply, cp.Role,
                baseline, score, alternative, completed, appliedRoots, trajectory.ToArray()));
        }
    }

    public int BestCandidateIndex
    {
        get
        {
            lock (_syncLock)
            {
                return _bestCandidateIndex;
            }
        }
    }

    public uint NextGeneration()
    {
        return Interlocked.Increment(ref _generation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetRootMoveStatus(int rootIndex, RootMoveStatus status)
    {
        lock (_syncLock)
        {
            if ((uint)rootIndex < (uint)_candidates.Length)
            {
                _candidates[rootIndex].Status = status;
                _candidates[rootIndex].IsDeadStem = (status != RootMoveStatus.Active);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RootMoveStatus GetRootMoveStatus(int rootIndex)
    {
        lock (_syncLock)
        {
            if ((uint)rootIndex < (uint)_candidates.Length)
            {
                return _candidates[rootIndex].Status;
            }
            return RootMoveStatus.Dead;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsRootMoveRefutedOrDead(int rootIndex)
    {
        lock (_syncLock)
        {
            if ((uint)rootIndex < (uint)_candidates.Length)
            {
                RootMoveStatus s = _candidates[rootIndex].Status;
                return s == RootMoveStatus.Refuted || s == RootMoveStatus.Dead;
            }
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementNodes(long count = 1)
    {
        Interlocked.Add(ref _totalNodes, count);
    }

    public void RequestStop()
    {
        _isStopped = true;
    }

    public void Reset()
    {
        _isStopped = false;
        _totalNodes = 0;
        _completedDepth = 0;
        _bestCandidateIndex = 0;
        _candidates = [];
        _rootCompletedCrosspoints = [];
    }

    public void Reset(Move[] legalRootMoves)
    {
        _isStopped = false;
        _totalNodes = 0;
        _completedDepth = 0;
        _bestCandidateIndex = 0;

        _candidates = new ZapRootCandidate[legalRootMoves.Length];
        _rootCompletedCrosspoints = new int[legalRootMoves.Length];
        for (int i = 0; i < legalRootMoves.Length; i++)
        {
            _candidates[i] = new ZapRootCandidate(legalRootMoves[i], -30000)
            {
                AgentCeiling = int.MaxValue,
                AgentFloor = int.MinValue
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementRootCompletedCrosspoints(int rootIndex)
    {
        int[] counts = _rootCompletedCrosspoints;
        if ((uint)rootIndex < (uint)counts.Length)
        {
            Interlocked.Increment(ref counts[rootIndex]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetRootCompletedCrosspoints(int rootIndex)
    {
        int[] counts = _rootCompletedCrosspoints;
        return (uint)rootIndex < (uint)counts.Length ? Volatile.Read(ref counts[rootIndex]) : 0;
    }

    public ZapRootCandidate GetCandidate(int index)
    {
        lock (_syncLock)
        {
            return _candidates[index];
        }
    }

    public ZapRootCandidate GetBestCandidate() => GetBestCandidate(out _);

    public ZapRootCandidate GetBestCandidate(out int index)
    {
        lock (_syncLock)
        {
            if (_candidates.Length == 0)
            {
                index = -1;
                return default;
            }
            int best = _bestCandidateIndex;
            if ((uint)best >= (uint)_candidates.Length || _candidates[best].IsDeadStem)
            {
                int maxScore = int.MinValue;
                for (int i = 0; i < _candidates.Length; i++)
                {
                    if (!_candidates[i].IsDeadStem && _candidates[i].Score > maxScore)
                    {
                        maxScore = _candidates[i].Score;
                        best = i;
                    }
                }
                if (best < 0 && _candidates.Length > 0)
                {
                    for (int i = 0; i < _candidates.Length; i++)
                    {
                        if (_candidates[i].Score > maxScore)
                        {
                            maxScore = _candidates[i].Score;
                            best = i;
                        }
                    }
                }
                if (best >= 0 && best < _candidates.Length) _bestCandidateIndex = best;
            }
            index = _bestCandidateIndex;
            return _candidates[index];
        }
    }

    public Move GetBestMove()
    {
        lock (_syncLock)
        {
            if (_candidates.Length == 0) return Move.Null;
            int best = _bestCandidateIndex;
            if ((uint)best >= (uint)_candidates.Length || _candidates[best].IsDeadStem)
            {
                int maxScore = int.MinValue;
                for (int i = 0; i < _candidates.Length; i++)
                {
                    if (!_candidates[i].IsDeadStem && _candidates[i].Score > maxScore)
                    {
                        maxScore = _candidates[i].Score;
                        best = i;
                    }
                }
                if (best < 0 && _candidates.Length > 0)
                {
                    for (int i = 0; i < _candidates.Length; i++)
                    {
                        if (_candidates[i].Score > maxScore)
                        {
                            maxScore = _candidates[i].Score;
                            best = i;
                        }
                    }
                }
                if (best >= 0 && best < _candidates.Length) _bestCandidateIndex = best;
            }
            return _candidates[_bestCandidateIndex].Move;
        }
    }

    public void AppendCandidateScoreHistory(int index, string text)
    {
        lock (_syncLock)
        {
            if ((uint)index < (uint)_candidates.Length)
            {
                _candidates[index].ScoreHistory += text;
            }
        }
    }

    public void UpdateCandidateBaseline(int index, int score, Move followup = default)
    {
        lock (_syncLock)
        {
            score = Math.Max(score, _candidates[index].AgentFloor);
            score = Math.Min(score, _candidates[index].AgentCeiling);
            _candidates[index].Score = score;
            _candidates[index].ScoreHistory += $"Baseline:{score} ";
            _candidates[index].BestFollowup = followup;
            RecomputeBestLocked();
        }
    }

    public void UpdateCandidateScreen(int index, int screenScore)
    {
        lock (_syncLock)
        {
            if ((uint)index < (uint)_candidates.Length)
            {
                _candidates[index].ScreenScore = screenScore;
                _candidates[index].HasScreen = true;
                _candidates[index].ScoreHistory += $"Screen:{screenScore} ";
            }
        }
    }

    public void UpdateCandidateTrajectory(int index, ReadOnlySpan<ZapTrajectoryStep> steps)
    {
        lock (_syncLock)
        {
            if ((uint)index < (uint)_candidates.Length)
            {
                _candidates[index].Trajectory = steps.ToArray();
                _candidates[index].CompletedDepth = steps.Length + 1;
            }
        }
    }

    public int GetScore(int index)
    {
        lock (_syncLock) return (uint)index < (uint)_candidates.Length ? _candidates[index].Score : 0;
    }

    /// <summary>
    /// Publishes the spine tree's backed-up value and principal line for a root spine. Called under the
    /// tree lock so concurrent agents publish in tree order.
    /// </summary>
    internal void SetTreeResult(int index, int treeValue, ZapTrajectoryStep[] principalLine)
    {
        lock (_syncLock)
        {
            if ((uint)index >= (uint)_candidates.Length) return;
            int newScore = Math.Min(Math.Max(treeValue, _candidates[index].AgentFloor), _candidates[index].AgentCeiling);
            _candidates[index].Trajectory = principalLine;
            if (principalLine.Length > 0) _candidates[index].BestFollowup = principalLine[0].Move;
            _candidates[index].CompletedDepth = Math.Max(_candidates[index].CompletedDepth, principalLine.Length + 1);
            _candidates[index].Score = newScore;
            ReviveIfRecoveredLocked(index);
            RecomputeBestLocked();
        }
    }

    public ZapRootCandidate GetSecondBestCandidate()
    {
        lock (_syncLock)
        {
            int bestIdx = _bestCandidateIndex;
            int secondIdx = -1;
            int secondScore = int.MinValue;
            for (int i = 0; i < _candidates.Length; i++)
            {
                if (i != bestIdx && !_candidates[i].IsDeadStem && _candidates[i].Score > secondScore)
                {
                    secondScore = _candidates[i].Score;
                    secondIdx = i;
                }
            }
            return secondIdx >= 0 ? _candidates[secondIdx] : new ZapRootCandidate(Move.Null, -30000);
        }
    }

    public void SortCandidatesAndPrune(int hardAspirationMarginCp)
    {
        lock (_syncLock)
        {
            _killMarginCp = hardAspirationMarginCp;

            if (_candidates.Length == 0) return;

            // Stable indexing: do not physically reorder _candidates, preserve 1:1 mapping with root moves and masks
            int bestIdx = 0;
            int leaderScore = _candidates[0].Score;
            for (int i = 1; i < _candidates.Length; i++)
            {
                if (_candidates[i].Score > leaderScore)
                {
                    leaderScore = _candidates[i].Score;
                    bestIdx = i;
                }
            }
            _bestCandidateIndex = bestIdx;

            int cutoff = leaderScore - hardAspirationMarginCp;

            int screenBest = int.MinValue;
            for (int i = 0; i < _candidates.Length; i++)
            {
                if (_candidates[i].HasScreen && _candidates[i].ScreenScore > screenBest)
                {
                    screenBest = _candidates[i].ScreenScore;
                }
            }

            for (int i = 0; i < _candidates.Length; i++)
            {
                if (i == bestIdx)
                {
                    _candidates[i].IsDeadStem = false;
                    SetRootMoveStatus(i, RootMoveStatus.Active);
                    continue;
                }

                bool spineLow = _candidates[i].Score < cutoff;
                bool screenLow = _candidates[i].HasScreen && _candidates[i].ScreenScore < screenBest - hardAspirationMarginCp;
                if (spineLow && screenLow)
                {
                    _candidates[i].IsDeadStem = true;
                    SetRootMoveStatus(i, RootMoveStatus.Dead);
                }
                else
                {
                    _candidates[i].IsDeadStem = false;
                    SetRootMoveStatus(i, RootMoveStatus.Active);
                }
            }
        }
    }

    /// <summary>
    /// Revives a Dead/Refuted candidate whose score recovered to within the kill margin of the best alive score.
    /// Caller must hold the lock.
    /// </summary>
    private void ReviveIfRecoveredLocked(int index)
    {
        if (_candidates[index].Status == RootMoveStatus.Active) return;

        int bestAlive = int.MinValue;
        for (int i = 0; i < _candidates.Length; i++)
        {
            if (i != index && _candidates[i].Status == RootMoveStatus.Active && _candidates[i].Score > bestAlive)
            {
                bestAlive = _candidates[i].Score;
            }
        }

        if (bestAlive == int.MinValue || _candidates[index].Score >= bestAlive - _killMarginCp)
        {
            SetRootMoveStatus(index, RootMoveStatus.Active);
        }
    }

    /// <summary>
    /// Refutation agent result for ANY root candidate (not only the leader): lowers its score, records the proof as a
    /// ceiling so a re-deepened greedy spine cannot undo it, and never changes its status.
    /// </summary>
    public void ApplyAgentRefutation(int index, int refutedScore, int depth)
    {
        lock (_syncLock)
        {
            if ((uint)index >= (uint)_candidates.Length) return;
            _candidates[index].AgentCeiling = Math.Min(_candidates[index].AgentCeiling, refutedScore);
            _candidates[index].AgentFloor = Math.Min(_candidates[index].AgentFloor, _candidates[index].AgentCeiling);
            if (refutedScore < _candidates[index].Score)
            {
                _candidates[index].Score = refutedScore;
                _candidates[index].ScoreHistory += $"AgentRefuted:{refutedScore} ";
                _candidates[index].CompletedDepth = Math.Max(_candidates[index].CompletedDepth, depth);
                RecomputeBestLocked();
            }
        }
    }

    private void RecomputeBestLocked()
    {
        int bestIdx = -1;
        int bestScore = int.MinValue;
        for (int i = 0; i < _candidates.Length; i++)
        {
            if (!_candidates[i].IsDeadStem && _candidates[i].Score > bestScore)
            {
                bestScore = _candidates[i].Score;
                bestIdx = i;
            }
        }
        if (bestIdx >= 0) _bestCandidateIndex = bestIdx;
    }
    /// <summary>
    /// Refutation squad: updates the Leader's score when an opponent refutation drops its score.
    /// If the score drops below the top contender, triggers Leader Demotion and marks RootMoveStatus as Refuted.
    /// </summary>
    public bool TryApplyLeaderRefutation(int leaderIndex, int refutedScore, int depth, out bool demoted)
    {
        lock (_syncLock)
        {
            demoted = false;
            if ((uint)leaderIndex >= (uint)_candidates.Length) return false;

            // Opponent refutation only lowers the score
            if (refutedScore >= _candidates[leaderIndex].Score) return false;

            _candidates[leaderIndex].Score = refutedScore;
            _candidates[leaderIndex].AgentCeiling = Math.Min(_candidates[leaderIndex].AgentCeiling, refutedScore);
            _candidates[leaderIndex].AgentFloor = Math.Min(_candidates[leaderIndex].AgentFloor, _candidates[leaderIndex].AgentCeiling);
            _candidates[leaderIndex].ScoreHistory += $"Refuted:{refutedScore} ";
            _candidates[leaderIndex].CompletedDepth = Math.Max(_candidates[leaderIndex].CompletedDepth, depth);

            // Recompute best candidate across all active candidates
            int bestIdx = -1;
            int bestScore = int.MinValue;
            for (int i = 0; i < _candidates.Length; i++)
            {
                if (!_candidates[i].IsDeadStem && _candidates[i].Score > bestScore)
                {
                    bestScore = _candidates[i].Score;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                if (bestIdx != leaderIndex && bestScore > _candidates[leaderIndex].Score)
                {
                    // Leader Demoted!
                    demoted = true;
                    _bestCandidateIndex = bestIdx;

                    SetRootMoveStatus(leaderIndex, RootMoveStatus.Refuted);
                    if (_candidates[leaderIndex].Score < bestScore - 1500)
                    {
                        _candidates[leaderIndex].IsDeadStem = true;
                        SetRootMoveStatus(leaderIndex, RootMoveStatus.Dead);
                    }

                    // Promote the new leader!
                    _candidates[bestIdx].IsDeadStem = false;
                    SetRootMoveStatus(bestIdx, RootMoveStatus.Active);
                    return true;
                }
                else
                {
                    _bestCandidateIndex = bestIdx;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Promotion squad: updates a Contender's score when an engine tactic improves its score.
    /// If score surpasses the Leader, triggers Leader Promotion.
    /// </summary>
    public bool TryApplyContenderPromotion(int contenderIndex, int improvedScore, int depth, out bool promotedToLeader)
    {
        lock (_syncLock)
        {
            promotedToLeader = false;
            if ((uint)contenderIndex >= (uint)_candidates.Length) return false;
            improvedScore = Math.Min(improvedScore, _candidates[contenderIndex].AgentCeiling);

            // Promotion only increases the score
            if (improvedScore <= _candidates[contenderIndex].Score) return false;

            _candidates[contenderIndex].Score = improvedScore;
            _candidates[contenderIndex].AgentFloor = Math.Max(_candidates[contenderIndex].AgentFloor, improvedScore);
            ReviveIfRecoveredLocked(contenderIndex);
            _candidates[contenderIndex].ScoreHistory += $"Promoted:{improvedScore} ";
            _candidates[contenderIndex].CompletedDepth = Math.Max(_candidates[contenderIndex].CompletedDepth, depth);

            int currentLeaderScore = _candidates[_bestCandidateIndex].Score;
            if (improvedScore > currentLeaderScore)
            {
                promotedToLeader = true;
                _bestCandidateIndex = contenderIndex;
                SetRootMoveStatus(contenderIndex, RootMoveStatus.Active);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Propagates an updated score for a root candidate from discrepancy exploration.
    /// Returns true if the best move changed.
    /// </summary>
    public bool TryUpdateScore(int candidateIndex, int newScore, int depth)
    {
        lock (_syncLock)
        {
            if ((uint)candidateIndex >= (uint)_candidates.Length) return false;

            // Agent proofs survive spine re-deepening: floor first, then ceiling (a refutation outranks a promotion).
            newScore = Math.Max(newScore, _candidates[candidateIndex].AgentFloor);
            newScore = Math.Min(newScore, _candidates[candidateIndex].AgentCeiling);

            _candidates[candidateIndex].Score = newScore;
            ReviveIfRecoveredLocked(candidateIndex);
            _candidates[candidateIndex].ScoreHistory += $"TryUpdate:{newScore} ";
            _candidates[candidateIndex].CompletedDepth = Math.Max(_candidates[candidateIndex].CompletedDepth, depth);

            // Recompute best candidate across all non-dead, non-refuted candidates
            int bestIdx = -1;
            int bestScore = int.MinValue;
            for (int i = 0; i < _candidates.Length; i++)
            {
                if (!_candidates[i].IsDeadStem)
                {
                    if (_candidates[i].Score > bestScore)
                    {
                        bestScore = _candidates[i].Score;
                        bestIdx = i;
                    }
                }
            }

            if (bestIdx >= 0)
            {
                bool changed = bestIdx != _bestCandidateIndex;
                _bestCandidateIndex = bestIdx;
                return changed;
            }

            return false;
        }
    }

    /// <summary>
    /// Splices a new sub-spine tail into root candidate trajectories that pass through zobristKey,
    /// and updates their root scores.
    /// </summary>
    public bool SpliceTrajectoryTail(
        int candidateIndex,
        ulong zobristKey,
        int newScore,
        Move siblingMove,
        bool isOpponentTurn,
        ReadOnlySpan<ZapTrajectoryStep> newTail,
        uint generation = 0,
        Move originalMove = default)
    {
        lock (_syncLock)
        {
            if ((uint)candidateIndex >= (uint)_candidates.Length) return false;
            if (generation != 0 && generation != _generation) return false;
            if (newTail.IsEmpty || newTail[0].ZobristKey != zobristKey || newTail[0].Move != siblingMove)
            {
                throw new ArgumentException("A replacement trajectory must include its junction move.", nameof(newTail));
            }

            ZapTrajectoryStep[] oldTraj = _candidates[candidateIndex].Trajectory;
            int junctionIdx = -1;
            for (int i = 0; i < oldTraj.Length; i++)
            {
                if (oldTraj[i].ZobristKey == zobristKey)
                {
                    junctionIdx = i;
                    break;
                }
            }

            if (junctionIdx < 0)
            {
                SpliceRejectNotOnSpine++;
                return false;
            }
            if (!originalMove.IsNull && oldTraj[junctionIdx].Move != originalMove) { SpliceRejectMoveChanged++; return false; }
            ref ZapRootCandidate candidate = ref _candidates[candidateIndex];
            if (isOpponentTurn ? newScore >= candidate.Score : newScore <= candidate.Score) { SpliceRejectNotBetterThanSpine++; return false; }

            if (isOpponentTurn)
            {
                candidate.AgentCeiling = Math.Min(candidate.AgentCeiling, newScore);
                candidate.AgentFloor = Math.Min(candidate.AgentFloor, candidate.AgentCeiling);
            }
            else
            {
                newScore = Math.Min(newScore, candidate.AgentCeiling);
                if (newScore <= candidate.Score) { SpliceRejectCeiling++; return false; }
                candidate.AgentFloor = Math.Max(candidate.AgentFloor, newScore);
            }

            var spliced = new ZapTrajectoryStep[junctionIdx + newTail.Length];
            Array.Copy(oldTraj, 0, spliced, 0, junctionIdx);
            newTail.CopyTo(spliced.AsSpan(junctionIdx));
            candidate.Trajectory = spliced;
            candidate.BestFollowup = spliced[0].Move;
            candidate.ScoreHistory += $"Splice(zob={zobristKey:X},junc={junctionIdx},newScore={newScore},sib={siblingMove.ToUci()}) ";
            TryUpdateScore(candidateIndex, newScore, spliced.Length + 1);
            return true;
        }
    }

    public void SetCompletedDepth(int depth)
    {
        Volatile.Write(ref _completedDepth, depth);
    }

    public int SpliceRejectNotOnSpine;
    public int SpliceRejectMoveChanged;
    public int SpliceRejectNotBetterThanSpine;
    public int SpliceRejectCeiling;

    public void ResetSpliceDiagnostics()
    {
        lock (_syncLock)
        {
            SpliceRejectNotOnSpine = SpliceRejectMoveChanged = SpliceRejectNotBetterThanSpine = SpliceRejectCeiling = 0;
        }
    }
}
