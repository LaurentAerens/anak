namespace Zanak.Search;

/// <summary>
/// Centralized SPSA/Texel-tunable parameters for the ZAP search engine.
/// All parameters are mutable and exposed for tuning and UCI setoption control.
/// </summary>
public sealed class ZapParams
{
    public static ZapParams Default { get; } = new();

    /// <summary>
    /// Evaluation shock threshold in centipawns (|ΔEval| > 73 cp) that flags a ply as a discrepancy crosspoint.
    /// </summary>
    public int ShockThresholdCp { get; set; } = 73;

    /// <summary>
    /// Centipawn score deficit below the leader score (e.g. Leader - 247 cp) that classifies a root move as a dead stem.
    /// </summary>
    public int DeadStemPruneMarginCp { get; set; } = 247;

    /// <summary>
    /// Centipawn window below LeaderScore (e.g. 99 cp) for a spine to qualify for worker crosspoint generation.
    /// Spines below this margin are classified as Dead Stems with zero assigned worker agents.
    /// </summary>
    public int HardAspirationMarginCp { get; set; } = 99;

    /// <summary>
    /// Centipawn window below LeaderScore (e.g. 50 cp) for a qualified contender to receive Promotion Squad exploration.
    /// </summary>
    public int ContenderPromotionMarginCp { get; set; } = 50;

    /// <summary>
    /// Static exchange evaluation (SEE) threshold for selecting winning tactical captures.
    /// </summary>
    public int WinningSeeThresholdCp { get; set; } = 0;

    /// <summary>
    /// Maximum number of shock crosspoints to extract from a single linear probe spine.
    /// </summary>
    public int MaxDiscrepancyCrosspoints { get; set; } = 64;

    /// <summary>
    /// Centipawn margin for dynamic alpha/beta pruning during worker branch exploration.
    /// </summary>
    public int WorkerPruneMarginCp { get; set; } = 201;

    /// <summary>
    /// Default size of transposition table in MB.
    /// </summary>
    public int DefaultHashMb { get; set; } = 64;

    /// <summary>
    /// Default number of search worker threads.
    /// </summary>
    public int DefaultThreads { get; set; } = 1;

    /// <summary>
    /// Minimum search depth at which Syzygy WDL tablebases are probed during probe spine tracing.
    /// </summary>
    public int SyzygyProbeDepth { get; set; } = 1;

    /// <summary>
    /// Maximum piece count (e.g. 5 or 6) allowed for tablebase probing.
    /// </summary>
    public int MaxTablebasePieces { get; set; } = 6;

    /// <summary>
    /// Root proximity priority bias per ply closer to the root for crosspoints.
    /// Prioritizes exploring discrepancies near the root before deep leaf discrepancies.
    /// </summary>
    public int CrosspointRootBiasWeight { get; set; } = 23;

    /// <summary>
    /// Maximum number of tactical quiescence resolution plies at the end of a probe.
    /// </summary>
    public int QuiescenceMaxPlies { get; set; } = 2;

    /// <summary>
    /// Maximum number of sibling moves to explore at any single discrepancy crosspoint.
    /// Focuses search power on the strongest candidate moves.
    /// </summary>
    public int MaxSiblingExploration { get; set; } = 6;

    /// <summary>
    /// Percentage weight applied to Piece-Square Table (PST) quiet move scoring.
    /// </summary>
    public int QuietPstWeight { get; set; } = 100;

    /// <summary>
    /// Whether to check SEE safety on quiet moves to avoid hanging pieces in greedy policy.
    /// </summary>
    public bool SafetyCheckEnabled { get; set; } = true;

    /// <summary>
    /// Score drop threshold (in cp) that triggers early abandonment of a ruined probe spine.
    /// </summary>
    public int SpineAbandonThresholdCp { get; set; } = 250;

    /// <summary>
    /// Base shock threshold for depth-adaptive shock detection at ply 1.
    /// </summary>
    public int AdaptiveShockBaseCp { get; set; } = 52;

    /// <summary>
    /// Increase in shock threshold per ply deeper into the search tree.
    /// </summary>
    public int AdaptiveShockSlopeCp { get; set; } = 13;

    /// <summary>
    /// Score bonus given to immediate recaptures on the destination square of the opponent's prior move.
    /// </summary>
    public int RecaptureBonus { get; set; } = 2500;

    /// <summary>
    /// Penalty applied when leaving a friendly piece en prise to attack by lower-value pieces.
    /// </summary>
    public int EnPriseThreatPenalty { get; set; } = 4001;
}
