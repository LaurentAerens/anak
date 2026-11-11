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

    public ZapRootCandidate(Move move, int score)
    {
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
    private uint _generation;
    private int _bestCandidateIndex;
    private long _totalNodes;
    private int _completedDepth;
    private volatile bool _isStopped;

    public long TotalNodes => Volatile.Read(ref _totalNodes);
    public int CompletedDepth => Volatile.Read(ref _completedDepth);
    public bool IsStopped => _isStopped;
    public int CandidateCount => _candidates.Length;
    public uint Generation => Volatile.Read(ref _generation);

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
    }

    public void Reset(Move[] legalRootMoves)
    {
        _isStopped = false;
        _totalNodes = 0;
        _completedDepth = 0;
        _bestCandidateIndex = 0;

        _candidates = new ZapRootCandidate[legalRootMoves.Length];
        for (int i = 0; i < legalRootMoves.Length; i++)
        {
            _candidates[i] = new ZapRootCandidate(legalRootMoves[i], -30000);
        }
    }

    public ZapRootCandidate GetCandidate(int index)
    {
        lock (_syncLock)
        {
            return _candidates[index];
        }
    }

    public ZapRootCandidate GetBestCandidate()
    {
        lock (_syncLock)
        {
            if (_candidates.Length == 0) return default;
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
            return _candidates[_bestCandidateIndex];
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
            _candidates[index].Score = score;
            _candidates[index].ScoreHistory += $"Baseline:{score} ";
            _candidates[index].BestFollowup = followup;
        }
    }

    public void UpdateCandidateTrajectory(int index, ReadOnlySpan<ZapTrajectoryStep> steps)
    {
        lock (_syncLock)
        {
            if ((uint)index < (uint)_candidates.Length)
            {
                _candidates[index].Trajectory = steps.ToArray();
            }
        }
    }

    public void SortCandidatesAndPrune(int hardAspirationMarginCp)
    {
        lock (_syncLock)
        {
            Array.Sort(_candidates, (a, b) => b.Score.CompareTo(a.Score));
            _bestCandidateIndex = 0;

            if (_candidates.Length > 0)
            {
                int leaderScore = _candidates[0].Score;
                int cutoff = leaderScore - hardAspirationMarginCp;

                _candidates[0].IsDeadStem = false;
                SetRootMoveStatus(0, RootMoveStatus.Active);

                for (int i = 1; i < _candidates.Length; i++)
                {
                    if (_candidates[i].Score < cutoff)
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

            // Promotion only increases the score
            if (improvedScore <= _candidates[contenderIndex].Score) return false;

            _candidates[contenderIndex].Score = improvedScore;
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

            _candidates[candidateIndex].Score = newScore;
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

    public void SetCompletedDepth(int depth)
    {
        Volatile.Write(ref _completedDepth, depth);
    }
}
