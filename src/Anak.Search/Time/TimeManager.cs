using System.Diagnostics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Manages dynamic time allocation for search moves and periodic time expiration checks.
/// </summary>
public sealed class TimeManager
{
    private readonly Stopwatch _stopwatch = new();
    private long _softLimitMs;
    private long _hardLimitMs;
    private long _baseSoftLimitMs;
    private long _baseHardLimitMs;
    private long _maxAllowedMs;
    private bool _hasTimeLimit;
    private Move _lastBestMove = Move.Null;
    private int _stableMoveDepths;
    private bool _hadFailLowThisIter;

    public const int CheckInterval = 512;
    public const int SafetyLagMs = 40;

    public ulong CheckMask { get; private set; } = 1023;
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;
    public long SoftLimitMs => _softLimitMs;
    public long HardLimitMs => _hardLimitMs;
    public bool HasTimeLimit => _hasTimeLimit;
    public bool IsTimeCrunch => _hasTimeLimit && (_softLimitMs < 20 || _hardLimitMs < 35);
    public int StableMoveDepths => _stableMoveDepths;
    public int ScoreDropThreshold { get; set; } = 45;

    // SPSA-tuned Dynamic Time Management parameters (converged across 52,352 games)
    public int TimeMovesToGo { get; set; } = 38;
    public int TimeIncFactor { get; set; } = 73;
    public int TimeHardMultiplier { get; set; } = 181;
    public int TimeSoftHardRatio { get; set; } = 60;
    public int TimeFlipScale { get; set; } = 150;
    public int TimeStableScale { get; set; } = 75;
    public int TimeScoreDropScale { get; set; } = 131;
    public int TimeFailLowScale { get; set; } = 140;

    /// <summary>
    /// Gets the estimated target depth achievable within the soft time limit.
    /// In blitz (25ms), typically depth 9-10; in classical, depth 16-22.
    /// </summary>
    public int EstimateTargetDepth()
    {
        if (!_hasTimeLimit) return 64;
        if (_softLimitMs < 30) return 9;
        if (_softLimitMs < 80) return 11;
        if (_softLimitMs < 250) return 13;
        if (_softLimitMs < 1000) return 15;
        if (_softLimitMs < 5000) return 18;
        return 22;
    }

    /// <summary>
    /// Initializes time allocation based on remaining clock, increment, and move limits.
    /// </summary>
    public void Init(in SearchLimits limits, Color sideToMove)
    {
        _stopwatch.Restart();
        CheckMask = 1023;
        _lastBestMove = Move.Null;
        _stableMoveDepths = 0;
        _hadFailLowThisIter = false;

        if (limits.Infinite)
        {
            _hasTimeLimit = false;
            _softLimitMs = long.MaxValue;
            _hardLimitMs = long.MaxValue;
            _baseSoftLimitMs = long.MaxValue;
            _baseHardLimitMs = long.MaxValue;
            _maxAllowedMs = long.MaxValue;
            return;
        }

        if (limits.MoveTime > 0)
        {
            _hasTimeLimit = true;
            _softLimitMs = Math.Max(1, limits.MoveTime - SafetyLagMs);
            _hardLimitMs = limits.MoveTime;
            _baseSoftLimitMs = _softLimitMs;
            _baseHardLimitMs = _hardLimitMs;
            _maxAllowedMs = limits.MoveTime;
            CheckMask = _hardLimitMs < 100 ? 63UL : (_hardLimitMs < 250 ? 127UL : 1023UL);
            return;
        }

        int remainingTime = sideToMove == Color.White ? limits.WhiteTime : limits.BlackTime;
        int increment = sideToMove == Color.White ? limits.WhiteIncrement : limits.BlackIncrement;

        if (remainingTime <= 0)
        {
            if (!limits.HasNodeLimit && !limits.HasDepthLimit)
            {
                _hasTimeLimit = true;
                _softLimitMs = 2970;
                _hardLimitMs = 3000;
                _baseSoftLimitMs = _softLimitMs;
                _baseHardLimitMs = _hardLimitMs;
                _maxAllowedMs = 3000;
                return;
            }

            _hasTimeLimit = false;
            _softLimitMs = long.MaxValue;
            _hardLimitMs = long.MaxValue;
            _baseSoftLimitMs = long.MaxValue;
            _baseHardLimitMs = long.MaxValue;
            _maxAllowedMs = long.MaxValue;
            return;
        }

        _hasTimeLimit = true;

        int safetyLag = remainingTime < 500 ? Math.Clamp(remainingTime / 5, 25, 45) : SafetyLagMs;

        // Emergency low-time handling (< 150ms)
        if (remainingTime < 150)
        {
            long overhead = Math.Clamp(remainingTime / 3, 25, 45);
            long maxLow = Math.Max(5, remainingTime - overhead);
            _maxAllowedMs = maxLow;
            _hardLimitMs = Math.Clamp(remainingTime / 2, 5, maxLow);
            // Crucial: soft limit must NEVER equal hard limit!
            // Give soft limit 50% of hard limit so iterative deepening NEVER launches an iteration
            // when dangerously close to the hard limit.
            _softLimitMs = Math.Max(5, (long)(_hardLimitMs * 0.5f));
            _baseSoftLimitMs = _softLimitMs;
            _baseHardLimitMs = _hardLimitMs;
            CheckMask = 31;
            return;
        }

        // Calculate expected moves remaining in the game (default TimeMovesToGo for balanced game pacing)
        int movesToGo = limits.MovesToGo > 0 ? Math.Clamp(limits.MovesToGo, 2, 50) : TimeMovesToGo;

        // Dynamic target allocation: (time / moves) + (TimeIncFactor% * inc)
        long allocated = (remainingTime / movesToGo) + ((long)increment * TimeIncFactor / 100);

        // If time is low relative to increment, preserve clock by budgeting within increment
        if (increment > 0 && remainingTime < increment * 3)
        {
            allocated = Math.Min(allocated, (long)(increment * 0.85));
        }

        // Soft limit is the ideal allocation per move
        long maxAllowed = Math.Max(5, remainingTime - safetyLag);
        _maxAllowedMs = maxAllowed;
        _softLimitMs = Math.Clamp(allocated, 5, maxAllowed);

        // Hard limit: allow up to TimeHardMultiplier% soft limit (or 1.5x in bullet), strictly capped by remaining time - safety lag
        float hardMultiplier = remainingTime < 2000 ? 1.5f : (TimeHardMultiplier / 100.0f);
        long targetHard = (long)(_softLimitMs * hardMultiplier);
        _hardLimitMs = Math.Clamp(targetHard, _softLimitMs, maxAllowed);

        // Soft limit starts at TimeSoftHardRatio% of hard limit, leaving headroom to expand up to 80% on fail-low / instability
        _softLimitMs = Math.Min(_softLimitMs, Math.Max(5, (long)(_hardLimitMs * (TimeSoftHardRatio / 100.0f))));
        _baseSoftLimitMs = _softLimitMs;
        _baseHardLimitMs = _hardLimitMs;

        if (_hardLimitMs < 100 || remainingTime < 500)
        {
            CheckMask = 31; // Check every 32 nodes in critical time
        }
        else if (_hardLimitMs < 250 || remainingTime < 1500)
        {
            CheckMask = 63; // Check every 64 nodes in low time
        }
        else
        {
            CheckMask = 511; // Check every 512 nodes normally
        }
    }

    /// <summary>
    /// Dynamically scales the soft and hard time limits by a factor (e.g. 0.25 when position is hopelessly lost).
    /// </summary>
    public void ScaleSoftLimit(float factor)
    {
        if (!_hasTimeLimit) return;
        long newHard = Math.Max(2, (long)(_baseHardLimitMs * factor));
        long maxSoft = Math.Max(1, newHard - 1);
        _hardLimitMs = newHard;
        _softLimitMs = Math.Clamp((long)(_baseSoftLimitMs * factor), 1, maxSoft);
    }

    /// <summary>
    /// Caps soft and hard time limits to a minimal budget when only one legal move exists.
    /// </summary>
    public void CapForSingleMove(long maxSoftMs = 5, long maxHardMs = 10)
    {
        if (!_hasTimeLimit) return;
        _hardLimitMs = Math.Min(_hardLimitMs, maxHardMs);
        _softLimitMs = Math.Min(_softLimitMs, Math.Min(maxSoftMs, Math.Max(1, _hardLimitMs - 1)));
        CheckMask = 31;
    }

    /// <summary>
    /// Adjusts soft time limits based on root best move stability and late move flips.
    /// </summary>
    public void UpdateMoveStability(Move currentBest, int depth)
    {
        if (!_hasTimeLimit || currentBest.IsNull) return;

        if (currentBest == _lastBestMove)
        {
            _stableMoveDepths++;
        }
        else
        {
            // Move flipped late in search (depth >= 8) with prior confidence: extend time, capped at 80% of hard limit
            if (depth >= 8 && _stableMoveDepths >= 2)
            {
                long maxSoft = Math.Max(1, Math.Min(_hardLimitMs - 1, (long)(_hardLimitMs * 0.80f)));
                _softLimitMs = Math.Clamp((long)(_softLimitMs * (TimeFlipScale / 100.0f)), 1, maxSoft);
            }
            _lastBestMove = currentBest;
            _stableMoveDepths = 1;
        }

        // Highly stable move (stable for >= 4 depths): scale back allocation to TimeStableScale% to conserve clock,
        // unless this iteration experienced a fail-low extension.
        if (_stableMoveDepths >= 4 && !_hadFailLowThisIter)
        {
            long maxSoft = Math.Max(1, _hardLimitMs - 1);
            _softLimitMs = Math.Clamp((long)(_baseSoftLimitMs * (TimeStableScale / 100.0f)), Math.Min(10, maxSoft), maxSoft);
        }

        _hadFailLowThisIter = false;
    }

    /// <summary>
    /// Extends soft time limit up to TimeScoreDropScale% (strictly capped by 80% of hard limit) when evaluation drops sharply.
    /// </summary>
    public void HandleScoreDrop(int currentScore, int prevScore, int depth)
    {
        if (!_hasTimeLimit || depth < 6) return;
        if (Math.Abs(prevScore) < Value.MateValue - 100 && currentScore < prevScore - ScoreDropThreshold)
        {
            long maxSoft = Math.Max(1, Math.Min(_hardLimitMs - 1, (long)(_hardLimitMs * 0.80f)));
            _softLimitMs = Math.Clamp((long)(_softLimitMs * (TimeScoreDropScale / 100.0f)), 1, maxSoft);
        }
    }

    /// <summary>
    /// Extends time budget when an aspiration search fails low at the root on depth >= 6.
    /// Scales soft limit up to TimeFailLowScale% (capped by 80% of hard limit) and expands hard limit if clock allows.
    /// </summary>
    public void ExtendForFailLow(int depth)
    {
        if (!_hasTimeLimit || depth < 6 || IsTimeCrunch) return;

        _hadFailLowThisIter = true;

        // Allow hard limit to expand up to 2.2x base soft limit within maxAllowed
        if (_maxAllowedMs > _hardLimitMs)
        {
            long potentialHard = (long)(_baseSoftLimitMs * 2.2f);
            _hardLimitMs = Math.Clamp(potentialHard, _hardLimitMs, _maxAllowedMs);
        }

        long maxSoft = Math.Max(1, Math.Min(_hardLimitMs - 1, (long)(_hardLimitMs * 0.80f)));
        _softLimitMs = Math.Clamp((long)(_softLimitMs * (TimeFailLowScale / 100.0f)), 1, maxSoft);
    }

    /// <summary>
    /// Checks periodically whether the hard time limit has been exceeded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldStop(ulong nodes)
    {
        if (!_hasTimeLimit) return false;
        if ((nodes & CheckMask) != 0) return false;

        return _stopwatch.ElapsedMilliseconds >= _hardLimitMs;
    }

    /// <summary>
    /// Checks if the soft time limit has been exceeded, indicating no new iteration should begin.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSoftTimeUp()
    {
        if (!_hasTimeLimit) return false;
        return _stopwatch.ElapsedMilliseconds >= _softLimitMs;
    }

    /// <summary>
    /// Checks if the absolute hard time limit has expired.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsHardTimeUp()
    {
        if (!_hasTimeLimit) return false;
        return _stopwatch.ElapsedMilliseconds >= _hardLimitMs;
    }
}
