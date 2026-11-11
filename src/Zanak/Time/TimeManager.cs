using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Zanak.Time;

/// <summary>
/// Calculates execution duration limits and structural depth targets based on available clock reserves,
/// and provides low-overhead polling checks for search termination.
/// </summary>
public sealed class TimeManager
{
    private readonly TimeParams _params;
    private readonly Stopwatch _stopwatch = new();
    private int _stableDominanceCount;
    private int _lastLeaderMoveRaw;

    public TimeParams Parameters => _params;
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    public TimeManager(TimeParams? parameters = null)
    {
        _params = parameters ?? TimeParams.Default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Start()
    {
        _stableDominanceCount = 0;
        _lastLeaderMoveRaw = 0;
        _stopwatch.Restart();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Stop()
    {
        _stopwatch.Stop();
    }

    /// <summary>
    /// Calculates both temporal limits and depth targets from clock inputs.
    /// </summary>
    public SearchBudget AllocateBudget(
        int remainingTimeMs,
        int incrementMs = 0,
        int movesToGo = 0,
        int moveTimeMs = 0,
        int depthLimit = 0,
        bool infinite = false)
    {
        // 1. Time Limits Calculation
        int softMs;
        int hardMs;

        if (infinite)
        {
            softMs = int.MaxValue;
            hardMs = int.MaxValue;
        }
        else if (moveTimeMs > 0)
        {
            softMs = (int)(moveTimeMs * _params.SoftMoveTimeFactor);
            hardMs = moveTimeMs;
        }
        else if (remainingTimeMs <= 0)
        {
            // Fallback for unbounded or zero-clock tests
            softMs = 1000;
            hardMs = 2000;
        }
        else
        {
            double baseTime;
            if (movesToGo > 0)
            {
                baseTime = (remainingTimeMs / (movesToGo + _params.MovesToGoDivisorOffset)) + (incrementMs * _params.MovesToGoIncFactor);
            }
            else
            {
                baseTime = (remainingTimeMs / _params.SuddenDeathDivisor) + (incrementMs * _params.SuddenDeathIncFactor);
            }

            int softMax = Math.Max(1, (int)(remainingTimeMs * _params.SoftClampMaxRatio));
            softMs = Math.Clamp((int)baseTime, 1, softMax);

            int hardMax = Math.Max(5, (int)(remainingTimeMs * _params.HardClampMaxRatio));
            hardMs = Math.Clamp((int)(baseTime * _params.HardLimitMultiplier), 5, hardMax);

            // Clock flag forfeit guard: hard ceiling <= T - HardBufferMs
            int forfeitGuard = Math.Max(1, remainingTimeMs - _params.HardBufferMs);
            if (hardMs > forfeitGuard)
            {
                hardMs = forfeitGuard;
            }
            if (softMs > hardMs)
            {
                softMs = hardMs;
            }
        }

        // 2. Depth Budget Calculation
        int targetBaseDepth;
        int maxDepthHorizon;

        if (depthLimit > 0)
        {
            targetBaseDepth = Math.Max(_params.AbsoluteMinBaseDepth, depthLimit - 4);
            maxDepthHorizon = Math.Max(targetBaseDepth + 2, depthLimit);
        }
        else if (infinite)
        {
            targetBaseDepth = _params.ClassicalMaxBaseDepth;
            maxDepthHorizon = targetBaseDepth + _params.ClassicalHorizonOffset;
        }
        else
        {
            double allocatedSeconds = softMs / 1000.0;
            if (allocatedSeconds < 0.15)
            {
                targetBaseDepth = _params.HyperBulletBaseDepth;
                maxDepthHorizon = _params.HyperBulletMaxHorizon;
            }
            else if (allocatedSeconds < 0.50)
            {
                targetBaseDepth = _params.FastBulletBaseDepth;
                maxDepthHorizon = _params.FastBulletMaxHorizon;
            }
            else if (allocatedSeconds < 1.0)
            {
                targetBaseDepth = _params.BulletBaseDepth;
                maxDepthHorizon = _params.BulletMaxHorizon;
            }
            else if (allocatedSeconds <= 5.0)
            {
                targetBaseDepth = _params.BlitzBaseDepth;
                maxDepthHorizon = _params.BlitzMaxHorizon;
            }
            else if (allocatedSeconds <= 30.0)
            {
                targetBaseDepth = _params.RapidBaseDepth;
                maxDepthHorizon = _params.RapidMaxHorizon;
            }
            else
            {
                int scaled = _params.ClassicalBaseDepthOffset + (int)Math.Floor(_params.ClassicalLogScale * Math.Log10(Math.Max(1.0, allocatedSeconds)));
                targetBaseDepth = Math.Clamp(scaled, _params.ClassicalBaseDepthOffset, _params.ClassicalMaxBaseDepth);
                maxDepthHorizon = targetBaseDepth + _params.ClassicalHorizonOffset;
            }

            targetBaseDepth = Math.Max(targetBaseDepth, _params.AbsoluteMinBaseDepth);
            maxDepthHorizon = Math.Max(maxDepthHorizon, _params.AbsoluteMinMaxHorizon);
        }

        return new SearchBudget(softMs, hardMs, targetBaseDepth, maxDepthHorizon);
    }

    /// <summary>
    /// Checks periodic mid-search termination conditions every CheckNodeInterval nodes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldStop(long nodes, in SearchBudget budget, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return true;
        return _stopwatch.ElapsedMilliseconds >= budget.HardLimitMs;
    }

    /// <summary>
    /// Checks if soft time limit has expired at the end of an iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasReachedSoftLimit(in SearchBudget budget, int leaderScore, int contenderScore, int leaderMoveRaw)
    {
        long elapsed = _stopwatch.ElapsedMilliseconds;
        if (elapsed >= budget.HardLimitMs) return true;

        int effectiveSoftMs = budget.SoftLimitMs;

        // Panic extension check: if gap <= 10 cp and nearing soft limit, extend soft limit up to 1.5x
        if (Math.Abs(leaderScore - contenderScore) <= _params.PanicExtensionGapCp && elapsed >= (budget.SoftLimitMs * 0.85))
        {
            effectiveSoftMs = Math.Min((int)(budget.SoftLimitMs * _params.PanicExtensionFactor), budget.HardLimitMs);
        }

        return elapsed >= effectiveSoftMs;
    }

    /// <summary>
    /// Evaluates whether stable dominance condition is met to bank clock time early.
    /// </summary>
    public bool CheckStableDominance(int currentDepth, int leaderScore, int contenderScore, int leaderMoveRaw)
    {
        if (currentDepth < _params.StableDominanceMinDepth) return false;

        int scoreLead = leaderScore - contenderScore;
        if (scoreLead >= _params.StableDominanceLeadCp && leaderMoveRaw != 0)
        {
            if (leaderMoveRaw == _lastLeaderMoveRaw)
            {
                _stableDominanceCount++;
            }
            else
            {
                _lastLeaderMoveRaw = leaderMoveRaw;
                _stableDominanceCount = 1;
            }

            if (_stableDominanceCount >= _params.StableDominanceIters)
            {
                return true;
            }
        }
        else
        {
            _stableDominanceCount = 0;
            _lastLeaderMoveRaw = leaderMoveRaw;
        }

        return false;
    }
}
