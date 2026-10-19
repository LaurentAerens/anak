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

    public const int CheckInterval = 1024;
    public const int SafetyLagMs = 30;

    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;
    public long SoftLimitMs => _softLimitMs;
    public long HardLimitMs => _hardLimitMs;
    public bool HasTimeLimit => _hasTimeLimit;

    /// <summary>
    /// Initializes time allocation based on remaining clock, increment, and move limits.
    /// </summary>
    public void Init(in SearchLimits limits, Color sideToMove)
    {
        _stopwatch.Restart();

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

        // Emergency low-time handling (< 100ms)
        if (remainingTime < 100)
        {
            _hardLimitMs = Math.Max(10, remainingTime / 2);
            _softLimitMs = _hardLimitMs;
            _baseSoftLimitMs = _softLimitMs;
            _baseHardLimitMs = _hardLimitMs;
            return;
        }

        // Calculate expected moves remaining in the game (default 35 for balanced game pacing)
        int movesToGo = limits.MovesToGo > 0 ? Math.Clamp(limits.MovesToGo, 2, 50) : 35;

        // Dynamic target allocation: (time / moves) + (3/4 * inc)
        long allocated = (remainingTime / movesToGo) + ((long)increment * 3 / 4);

        // Soft limit is the ideal allocation per move
        _softLimitMs = Math.Clamp(allocated, 10, remainingTime - SafetyLagMs);

        // Hard limit allows up to 2.5x soft limit for complex or critical tactical branches
        long maxHard = Math.Min(remainingTime - SafetyLagMs, (long)(_softLimitMs * 2.5));
        _hardLimitMs = Math.Max(_softLimitMs, maxHard);
        _baseSoftLimitMs = _softLimitMs;
        _baseHardLimitMs = _hardLimitMs;
    }

    /// <summary>
    /// Dynamically scales the soft and hard time limits by a factor (e.g. 0.25 when position is hopelessly lost).
    /// </summary>
    public void ScaleSoftLimit(float factor)
    {
        if (!_hasTimeLimit) return;
        _softLimitMs = Math.Max(20, (long)(_baseSoftLimitMs * factor));
        _hardLimitMs = Math.Max(_softLimitMs, (long)(_baseHardLimitMs * factor));
    }

    /// <summary>
    /// Checks periodically (every 1024 nodes) whether the hard time limit has been exceeded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldStop(ulong nodes)
    {
        if (!_hasTimeLimit) return false;
        if ((nodes & (CheckInterval - 1)) != 0) return false;

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
