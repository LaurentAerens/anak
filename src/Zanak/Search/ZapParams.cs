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
    /// By default uses 1 agent per CPU core, with a minimum of 2 on low-thread CPUs.
    /// </summary>
    public int DefaultThreads { get; set; } = Math.Max(2, Environment.ProcessorCount);

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
    /// Maximum number of new sibling spines per pickup. Unfinished siblings remain queued.
    /// </summary>
    public int MaxSiblingExploration { get; set; } = 6;

    public bool InterleaveRootSeeding { get; set; }

    /// <summary>Pickup penalty per estimated probe ply in the next sibling batch. Zero disables.</summary>
    public int CrosspointCostWeight { get; set; } = 2;

    /// <summary>
    /// Percentage weight applied to Piece-Square Table (PST) quiet move scoring.
    /// </summary>
    public int QuietPstWeight { get; set; } = 100;

    /// <summary>
    /// Whether to check SEE safety on quiet moves to avoid hanging pieces in greedy policy.
    /// </summary>
    public bool SafetyCheckEnabled { get; set; } = true;

    /// <summary>
    /// Obsolete: spines are no longer abandoned by the probe (agents prune them). Kept for tuner compatibility.
    /// </summary>
    public int SpineAbandonThresholdCp { get; set; } = 250;

    /// <summary>
    /// Minimum mover gain (cp) at ply 1 for an eval swing to create responsibility debt.
    /// </summary>
    public int AdaptiveShockBaseCp { get; set; } = 52;

    /// <summary>
    /// Increase in the responsibility swing threshold per ply deeper into the spine.
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

    /// <summary>
    /// Root pre-filter margin (cp): a 1-ply move is discarded before spine generation only if the opponent's best
    /// capture after it wins at least this much more than after a null move (or it allows mate in 1). 0 disables.
    /// </summary>
    public int RootFilterMarginCp { get; set; } = 180;

    /// <summary>
    /// Depth (plies after the junction move) of the mini alpha-beta used by Refutation / Promotion agents.
    /// </summary>
    public int AgentSearchDepth { get; set; } = 3;

    /// <summary>
    /// Node cap for a single agent mini-search (one junction move).
    /// </summary>
    public int AgentNodeLimit { get; set; } = 4000;

    /// <summary>
    /// Very conservative root kill margin (cp). A root move is only excluded from agent work when both its spine score
    /// and its mini-search screen score are at least this far below the best. Kills are reversible (agents can revive).
    /// </summary>
    public int RootKillMarginCp { get; set; } = 600;

    /// <summary>
    /// Depth of the per-root-move mini-search screen run right after the spine of each move (Phase 1).
    /// </summary>
    public int RootScreenDepth { get; set; } = 2;

    /// <summary>
    /// Weight applied to a crosspoint's responsibility (attributed eval-swing debt) in priority sorting.
    /// </summary>
    public int CrosspointDeltaWeight { get; set; } = 100;

    /// <summary>
    /// Percentage of remaining blame carried to each later unpaid reply of a side that owes an eval swing.
    /// </summary>
    public int CrosspointDebtDecayPct { get; set; } = 50;

    /// <summary>
    /// Minimum spine score change (cp) from a splice before earlier decisions on that line are re-ranked.
    /// </summary>
    public int BackCreditMinCp { get; set; } = 30;

    /// <summary>
    /// How many earlier decisions by the other side (junction-1, junction-3, ...) receive back credit.
    /// </summary>
    public int BackCreditMaxSteps { get; set; } = 3;

    /// <summary>
    /// How many times back credit may reopen an already explored crosspoint.
    /// </summary>
    public int CrosspointMaxReopens { get; set; } = 2;

    /// <summary>
    /// Weight applied to root spine score in crosspoint priority sorting.
    /// </summary>
    public int CrosspointSpineEvalWeight { get; set; } = 50;

    /// <summary>
    /// Diversity penalty multiplied by how many crosspoints have already been picked from a root spine.
    /// </summary>
    public int CrosspointDiversityPenalty { get; set; } = 40;

    /// <summary>
    /// Centipawn margin below BestSpineLeafEval. Crosspoints with ParentEval below this margin remain dormant.
    /// </summary>
    public int CrosspointPickupMarginCp { get; set; } = 350;

    /// <summary>
    /// Pickup penalty (percent of centipawns) for a subspine's distance from the best line at its node,
    /// accumulated down the spine tree. Off-line subspines stay eligible, just later.
    /// </summary>
    public int CrosspointSubspineGapWeight { get; set; } = 100;

    /// <summary>
    /// Minimum mover static-eval drop (cp) on a quiet spine move to attribute self-responsibility.
    /// </summary>
    public int QuietEvalDropMinCp { get; set; } = 45;

    /// <summary>
    /// Priority bonus for quiet non-capture, non-check-evasion, non-promotion spine crosspoints.
    /// </summary>
    public int CrosspointQuietBonus { get; set; } = 55;

    /// <summary>
    /// Additional priority bonus for quiet non-checking pawn pushes along the greedy spine.
    /// </summary>
    public int CrosspointQuietPawnPushBonus { get; set; } = 50;

    /// <summary>
    /// Priority bonus when the greedy spine transitions from a capture to a quiet move.
    /// </summary>
    public int CrosspointCaptureToQuietBonus { get; set; } = 45;

    /// <summary>
    /// Priority bonus when a quiet spine move lands on a square attacked by the opponent.
    /// </summary>
    public int CrosspointAttackedDestBonus { get; set; } = 25;

    /// <summary>
    /// Priority bonus for captures with non-positive SEE (dubious/equal exchanges vs winning captures).
    /// </summary>
    public int CrosspointNonWinningCaptureBonus { get; set; } = 40;

    /// <summary>
    /// Priority bonus for quiet spine crosspoints at ply 6 or deeper where greedy policy accuracy degrades sharply.
    /// </summary>
    public int CrosspointDeepQuietBonus { get; set; } = 35;

    /// <summary>
    /// Heavy priority weight for ply-1 crosspoints where the opponent has an immediate capture or hanging-piece threat.
    /// </summary>
    public int CrosspointPly1CaptureThreatWeight { get; set; } = 450;

    /// <summary>
    /// Memory bound on crosspoint entries per search. Subspines make the queue practically unbounded,
    /// so the clock, not the queue, ends a search; past this cap new positions are no longer queued.
    /// </summary>
    public int MaxQueueEntries { get; set; } = 500_000;
}
