using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Zanak.Time;

/// <summary>
/// Calculates execution duration limits and structural depth targets based on available clock reserves,
/// and provides low-overhead polling checks for search termination.
/// </summary>
public sealed class TimeManager
{
    private const int FallbackSoftLimitMs = 1000;
    private const int FallbackHardLimitMs = 2000;
    private const double PanicSoftThresholdRatio = 0.85;

    private readonly TimeParams _params;
    private readonly Stopwatch _stopwatch = new();
    private int _stableDominanceCount;
    private int _lastLeaderMoveRaw;
    private int _lastDominanceDepth;

    /// <summary>
    /// Time management parameters used to compute search budgets.
    /// </summary>
    public TimeParams Parameters => _params;

    /// <summary>
    /// Elapsed milliseconds since <see cref="Start"/> was called.
    /// </summary>
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    /// <summary>
    /// Initializes a new <see cref="TimeManager"/> instance.
    /// </summary>
    public TimeManager(TimeParams? parameters = null)
    {
        _params = parameters ?? TimeParams.Default;
    }

    /// <summary>
    /// Resets per-search dominance state and restarts the search stopwatch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Start()
    {
        _stableDominanceCount = 0;
        _lastLeaderMoveRaw = 0;
        _lastDominanceDepth = 0;
        _stopwatch.Restart();
    }

    /// <summary>
    /// Stops the search stopwatch.
    /// </summary>
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
        bool infinite = false,
        int threads = 1)
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
            softMs = FallbackSoftLimitMs;
            hardMs = FallbackHardLimitMs;
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
            // UCI "go depth N" replaces the depth the clock would choose; the time limits stay as computed.
            targetBaseDepth = Math.Max(_params.AbsoluteMinBaseDepth, depthLimit);
            maxDepthHorizon = targetBaseDepth;
        }
        else if (infinite)
        {
            targetBaseDepth = _params.ClassicalMaxBaseDepth;
            maxDepthHorizon = Math.Min(_params.ExtendedHorizonMaxPlies, targetBaseDepth + _params.ClassicalHorizonOffset);
        }
        else
        {
            double computeMs = Math.Max(1.0, (double)softMs * Math.Max(1, threads));
            double logComputeMs = Math.Log10(computeMs);
            // Smooth low-time boost for bullet/fast-blitz (ramps in above 5ms so 1ms emergency moves stay depth 1,
            // then decays smoothly toward 0 as computeMs grows past LowTimePivotMs).
            double ramp = Math.Clamp((computeMs - 5.0) / 25.0, 0.0, 1.0);
            double lowTimeBoost = _params.LowTimeDepthBoost * ramp / (1.0 + (computeMs / Math.Max(1.0, _params.LowTimePivotMs)));
            int scaledBase = (int)Math.Round(_params.LogBaseDepthOffset + _params.LogBaseDepthScale * logComputeMs + lowTimeBoost);
            targetBaseDepth = Math.Clamp(scaledBase, 1, _params.ClassicalMaxBaseDepth);
            int horizonDelta = Math.Clamp((int)Math.Round(_params.LogHorizonDeltaOffset + _params.LogHorizonDeltaScale * logComputeMs + (lowTimeBoost * 0.5)), 2, _params.ClassicalHorizonOffset);
            maxDepthHorizon = Math.Min(_params.ExtendedHorizonMaxPlies, targetBaseDepth + horizonDelta);
        }

        return new SearchBudget(softMs, hardMs, targetBaseDepth, maxDepthHorizon, ExtendHorizonWithTime: depthLimit <= 0);
    }

    /// <summary>
    /// Checks whether the search has been cancelled or reached its hard time limit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldStop(in SearchBudget budget, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return true;
        return _stopwatch.ElapsedMilliseconds >= budget.HardLimitMs;
    }

    /// <summary>
    /// Checks if the soft time limit has expired at the end of an iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasReachedSoftLimit(in SearchBudget budget, int leaderScore, int contenderScore)
    {
        long elapsed = _stopwatch.ElapsedMilliseconds;
        if (elapsed >= budget.HardLimitMs) return true;

        int effectiveSoftMs = budget.SoftLimitMs;

        // Panic extension check: if gap <= PanicExtensionGapCp and nearing soft limit, extend soft limit
        if (Math.Abs(leaderScore - contenderScore) <= _params.PanicExtensionGapCp && elapsed >= (budget.SoftLimitMs * PanicSoftThresholdRatio))
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
        if (currentDepth <= _lastDominanceDepth) return false;
        _lastDominanceDepth = currentDepth;

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
