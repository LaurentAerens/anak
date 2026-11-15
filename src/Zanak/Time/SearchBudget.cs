namespace Zanak.Time;

/// <summary>
/// Pre-calculated search budget defining temporal limits and structural spine/horizon depths.
/// When <paramref name="ExtendHorizonWithTime"/> is set, <paramref name="MaxDepthHorizon"/> is only the
/// initial target: the search keeps deepening spines while time remains instead of stopping at it.
/// </summary>
public readonly record struct SearchBudget(
    int SoftLimitMs,
    int HardLimitMs,
    int TargetBaseDepth,
    int MaxDepthHorizon,
    bool ExtendHorizonWithTime = false
);
