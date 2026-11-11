using Xunit;
using Zanak.Time;

namespace Anak.Tests.Zanak;

public sealed class TimeManagerTests
{
    [Fact]
    public void AllocateBudget_ExplicitMoveTime_CalculatesCorrectSoftAndHardCeiling()
    {
        var tm = new TimeManager();
        SearchBudget budget = tm.AllocateBudget(
            remainingTimeMs: 10_000,
            incrementMs: 0,
            movesToGo: 0,
            moveTimeMs: 1000);

        Assert.Equal(950, budget.SoftLimitMs);
        Assert.Equal(1000, budget.HardLimitMs);
    }

    [Fact]
    public void AllocateBudget_SuddenDeath_RespectsHardCeilingAndBuffer()
    {
        var tm = new TimeManager();
        SearchBudget budget = tm.AllocateBudget(
            remainingTimeMs: 60_000,
            incrementMs: 1_000);

        Assert.True(budget.SoftLimitMs > 0);
        Assert.True(budget.HardLimitMs >= budget.SoftLimitMs);
        Assert.True(budget.HardLimitMs <= 60_000 - 10);
    }

    [Fact]
    public void AllocateBudget_DepthScaling_AssignsCorrectTiers()
    {
        var tm = new TimeManager();

        // Hyper-Bullet (< 0.15s)
        var hyperBullet = tm.AllocateBudget(remainingTimeMs: 1_000, incrementMs: 0); // soft ~ 40ms
        Assert.Equal(3, hyperBullet.TargetBaseDepth);
        Assert.Equal(3, hyperBullet.MaxDepthHorizon);

        // Fast-Bullet (0.15s - 0.5s)
        var fastBullet = tm.AllocateBudget(remainingTimeMs: 5_000, incrementMs: 0); // soft ~ 200ms
        Assert.Equal(5, fastBullet.TargetBaseDepth);
        Assert.Equal(5, fastBullet.MaxDepthHorizon);

        // Bullet (0.5s - 1.0s)
        var bullet = tm.AllocateBudget(remainingTimeMs: 15_000, incrementMs: 0); // soft ~ 600ms
        Assert.Equal(7, bullet.TargetBaseDepth);
        Assert.Equal(8, bullet.MaxDepthHorizon);

        // Blitz (1s - 5s)
        var blitz = tm.AllocateBudget(remainingTimeMs: 60_000, incrementMs: 0); // soft ~ 2400 ms
        Assert.Equal(5, blitz.TargetBaseDepth);
        Assert.Equal(12, blitz.MaxDepthHorizon);
    }

    [Fact]
    public void StableDominance_TriggersOnlyAfterConsecutiveIterations()
    {
        var tm = new TimeManager();
        tm.Start();

        // Iteration 1
        Assert.False(tm.CheckStableDominance(currentDepth: 8, leaderScore: 200, contenderScore: 10, leaderMoveRaw: 42));

        // Iteration 2
        Assert.False(tm.CheckStableDominance(currentDepth: 9, leaderScore: 210, contenderScore: 10, leaderMoveRaw: 42));

        // Iteration 3
        Assert.True(tm.CheckStableDominance(currentDepth: 10, leaderScore: 220, contenderScore: 10, leaderMoveRaw: 42));
    }
}
