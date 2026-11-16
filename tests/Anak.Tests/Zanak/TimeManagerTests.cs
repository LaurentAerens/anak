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
    public void AllocateBudget_DepthLimit_OverridesDepthButKeepsClockTime()
    {
        var tm = new TimeManager();
        SearchBudget clock = tm.AllocateBudget(remainingTimeMs: 60_000, incrementMs: 1_000);
        SearchBudget withDepth = tm.AllocateBudget(remainingTimeMs: 60_000, incrementMs: 1_000, depthLimit: 7);

        Assert.Equal(clock.SoftLimitMs, withDepth.SoftLimitMs);
        Assert.Equal(clock.HardLimitMs, withDepth.HardLimitMs);
        Assert.Equal(7, withDepth.TargetBaseDepth);
        Assert.Equal(7, withDepth.MaxDepthHorizon);
        Assert.False(withDepth.ExtendHorizonWithTime);
    }

    [Fact]
    public void AllocateBudget_DepthScaling_ScalesSmoothlyWithTime()
    {
        var tm = new TimeManager();

        // Extreme bullet (soft = 1ms) -> depth 1 (no spine generation)
        var extremeBullet = tm.AllocateBudget(remainingTimeMs: 20, incrementMs: 0);
        Assert.Equal(1, extremeBullet.TargetBaseDepth);
        Assert.True(extremeBullet.MaxDepthHorizon >= extremeBullet.TargetBaseDepth);

        // Monotonic increase across time controls
        var hyperBullet = tm.AllocateBudget(remainingTimeMs: 1_000, incrementMs: 0);
        var fastBullet = tm.AllocateBudget(remainingTimeMs: 5_000, incrementMs: 0);
        var bullet = tm.AllocateBudget(remainingTimeMs: 15_000, incrementMs: 0);
        var blitz = tm.AllocateBudget(remainingTimeMs: 60_000, incrementMs: 0);

        Assert.True(hyperBullet.TargetBaseDepth > extremeBullet.TargetBaseDepth);
        Assert.True(fastBullet.TargetBaseDepth >= hyperBullet.TargetBaseDepth);
        Assert.True(bullet.TargetBaseDepth >= fastBullet.TargetBaseDepth);
        Assert.True(blitz.TargetBaseDepth > bullet.TargetBaseDepth);
        Assert.True(blitz.MaxDepthHorizon > blitz.TargetBaseDepth);
    }

    [Fact]
    public void StableDominance_TriggersOnlyAfterConsecutiveIterations()
    {
        var tm = new TimeManager();
        tm.Start();

        int depth = tm.Parameters.StableDominanceMinDepth;
        for (int i = 1; i < tm.Parameters.StableDominanceIters; i++)
        {
            Assert.False(tm.CheckStableDominance(currentDepth: depth++, leaderScore: 200 + i * 10, contenderScore: 10, leaderMoveRaw: 42));
        }

        Assert.True(tm.CheckStableDominance(currentDepth: depth, leaderScore: 250, contenderScore: 10, leaderMoveRaw: 42));
    }

    [Fact]
    public void StableDominance_DoesNotCountRepeatedPollingAtSameDepth()
    {
        var tm = new TimeManager();
        tm.Start();
        int depth = tm.Parameters.StableDominanceMinDepth;
        for (int i = 0; i < 10; i++)
        {
            Assert.False(tm.CheckStableDominance(depth, 200, 10, 42));
        }
        for (int i = 2; i < tm.Parameters.StableDominanceIters; i++)
        {
            Assert.False(tm.CheckStableDominance(++depth, 200, 10, 42));
        }
        Assert.True(tm.CheckStableDominance(++depth, 200, 10, 42));
    }
}
