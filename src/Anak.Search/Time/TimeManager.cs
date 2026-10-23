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
    private bool _hasTimeLimit;
    private Move _lastBestMove = Move.Null;
    private int _stableMoveDepths;

    public const int CheckInterval = 1024;
    public const int SafetyLagMs = 30;

    public ulong CheckMask { get; private set; } = 1023;
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;
    public long SoftLimitMs => _softLimitMs;
    public long HardLimitMs => _hardLimitMs;
    public bool HasTimeLimit => _hasTimeLimit;
    public int StableMoveDepths => _stableMoveDepths;
    public int ScoreDropThreshold { get; set; } = 45;

    /// <summary>
    /// Initializes time allocation based on remaining clock, increment, and move limits.
    /// </summary>
    public void Init(in SearchLimits limits, Color sideToMove)
    {
        _stopwatch.Restart();
        CheckMask = 1023;
        _lastBestMove = Move.Null;
        _stableMoveDepths = 0;

        if (limits.Infinite)
        {
            _hasTimeLimit = false;
            _softLimitMs = long.MaxValue;
            _hardLimitMs = long.MaxValue;
            _baseSoftLimitMs = long.MaxValue;
            _baseHardLimitMs = long.MaxValue;
            return;
        }

        if (limits.MoveTime > 0)
        {
            _hasTimeLimit = true;
            _softLimitMs = Math.Max(1, limits.MoveTime - SafetyLagMs);
            _hardLimitMs = limits.MoveTime;
            _baseSoftLimitMs = _softLimitMs;
            _baseHardLimitMs = _hardLimitMs;
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
                return;
            }

            _hasTimeLimit = false;
            _softLimitMs = long.MaxValue;
            _hardLimitMs = long.MaxValue;
            _baseSoftLimitMs = long.MaxValue;
            _baseHardLimitMs = long.MaxValue;
            return;
        }

        _hasTimeLimit = true;

        int safetyLag = remainingTime < 500 ? Math.Clamp(remainingTime / 4, 15, 30) : SafetyLagMs;

        // Emergency low-time handling (< 100ms)
        if (remainingTime < 100)
        {
            long maxLow = Math.Max(5, remainingTime - 15);
            _hardLimitMs = Math.Clamp(remainingTime / 2, 5, maxLow);
            _softLimitMs = _hardLimitMs;
            _baseSoftLimitMs = _softLimitMs;
            _baseHardLimitMs = _hardLimitMs;
            CheckMask = 63;
            return;
        }

        // Calculate expected moves remaining in the game (default 40 for balanced game pacing)
        int movesToGo = limits.MovesToGo > 0 ? Math.Clamp(limits.MovesToGo, 2, 50) : 40;

        // Dynamic target allocation: (time / moves) + (7/10 * inc)
        long allocated = (remainingTime / movesToGo) + ((long)increment * 7 / 10);

        // If time is low relative to increment, preserve clock by budgeting within increment
        if (increment > 0 && remainingTime < increment * 3)
        {
            allocated = Math.Min(allocated, (long)(increment * 0.85));
        }

        // Soft limit is the ideal allocation per move
        long maxAllowed = Math.Max(5, remainingTime - safetyLag);
        _softLimitMs = Math.Clamp(allocated, 5, maxAllowed);

        // Hard limit: allow up to 1.7x soft limit, strictly capped by remaining time - safety lag
        float hardMultiplier = remainingTime < 2000 ? 1.3f : 1.7f;
        long targetHard = (long)(_softLimitMs * hardMultiplier);
        _hardLimitMs = Math.Clamp(targetHard, _softLimitMs, maxAllowed);
        _baseSoftLimitMs = _softLimitMs;
        _baseHardLimitMs = _hardLimitMs;

        if (_hardLimitMs < 100 || remainingTime < 500)
        {
            CheckMask = 63; // Check every 64 nodes in critical time
        }
        else if (_hardLimitMs < 250 || remainingTime < 1500)
        {
            CheckMask = 127; // Check every 128 nodes in low time
        }
        else
        {
            CheckMask = 1023; // Check every 1024 nodes normally
        }
    }

    /// <summary>
    /// Dynamically scales the soft and hard time limits by a factor (e.g. 0.25 when position is hopelessly lost).
    /// </summary>
    public void ScaleSoftLimit(float factor)
    {
        if (!_hasTimeLimit) return;
        _softLimitMs = Math.Max(10, (long)(_baseSoftLimitMs * factor));
        _hardLimitMs = Math.Max(_softLimitMs, (long)(_baseHardLimitMs * factor));
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
            // Move flipped late in search (depth >= 8) with prior confidence: extend time up to hard limit
            if (depth >= 8 && _stableMoveDepths >= 2)
            {
                _softLimitMs = Math.Min(_hardLimitMs, (long)(_softLimitMs * 1.8f));
            }
            _lastBestMove = currentBest;
            _stableMoveDepths = 1;
        }

        // Highly stable move (stable for >= 4 depths): scale back allocation to 0.8x to conserve clock
        if (_stableMoveDepths >= 4)
        {
            _softLimitMs = Math.Max(10, (long)(_baseSoftLimitMs * 0.8f));
        }
    }

    /// <summary>
    /// Extends soft time limit up to 1.5x (capped by hard limit) when evaluation drops sharply (> ScoreDropThreshold cp).
    /// </summary>
    public void HandleScoreDrop(int currentScore, int prevScore, int depth)
    {
        if (!_hasTimeLimit || depth < 6) return;
        if (Math.Abs(prevScore) < Value.MateValue - 100 && currentScore < prevScore - ScoreDropThreshold)
        {
            _softLimitMs = Math.Min(_hardLimitMs, (long)(_softLimitMs * 1.5f));
        }
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
