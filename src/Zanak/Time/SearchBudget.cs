namespace Zanak.Time;

/// <summary>
/// Pre-calculated search budget defining temporal limits and structural spine/horizon depths.
/// <see cref="TimeManager"/> is the single source of truth for <paramref name="TargetBaseDepth"/> and
/// <paramref name="MaxDepthHorizon"/> (including UCI <c>go depth N</c> overrides). When
/// <paramref name="ExtendHorizonWithTime"/> is set, clock-driven searches deepen spines on a time
/// schedule up to <paramref name="MaxDepthHorizon"/> and spend remaining time draining crosspoints.
/// </summary>
public readonly record struct SearchBudget(
    int SoftLimitMs,
    int HardLimitMs,
    int TargetBaseDepth,
    int MaxDepthHorizon,
    bool ExtendHorizonWithTime = false
);
