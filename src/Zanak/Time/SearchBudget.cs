namespace Zanak.Time;

/// <summary>
/// Pre-calculated search budget defining temporal limits and structural spine/horizon depths.
/// </summary>
public readonly record struct SearchBudget(
    int SoftLimitMs,
    int HardLimitMs,
    int TargetBaseDepth,
    int MaxDepthHorizon
);
