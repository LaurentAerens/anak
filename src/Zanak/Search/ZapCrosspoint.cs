using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// Classification of retrospective discrepancy shock points along a trajectory spine.
/// </summary>
public enum CrosspointKind : byte
{
    /// <summary>
    /// Engine's turn: an engine move dropped evaluation, suggesting greedy policy missed a stronger move.
    /// </summary>
    Opportunity = 0,

    /// <summary>
    /// Opponent's turn: an opponent move caused an evaluation surge for the engine, suggesting greedy policy blundered for opponent.
    /// </summary>
    AdversarialRefutation = 1
}

/// <summary>
/// Operational assignment role for asymmetric worker agents.
/// </summary>
public enum ZapAgentRole : byte
{
    /// <summary>
    /// Refutation squad: targets the Leader on opponent plies to stress-test and refute it.
    /// </summary>
    Refutation = 0,

    /// <summary>
    /// Promotion squad: targets qualified Contenders on engine plies to discover tactics that elevate them.
    /// </summary>
    Promotion = 1
}

/// <summary>
/// Descriptor representing a discrepancy shock node to be explored by worker threads.
/// </summary>
public readonly struct ZapCrosspoint : IComparable<ZapCrosspoint>
{
    public readonly Board Board;
    public readonly int RootCandidateIndex;
    public readonly uint Generation;
    public readonly ZapAgentRole Role;
    public readonly int Ply;
    public readonly short BaselineScore;
    public readonly short ShockMagnitude;
    public readonly CrosspointKind Kind;
    public readonly int RemainingHorizon;
    public readonly Move OriginalMove;
    public readonly int PriorityScore;

    public ZapCrosspoint(
        in Board board,
        int rootCandidateIndex,
        int ply,
        short baselineScore,
        short shockMagnitude,
        CrosspointKind kind,
        int remainingHorizon,
        Move originalMove,
        int rootBiasWeight = 25,
        uint generation = 1,
        ZapAgentRole role = ZapAgentRole.Refutation)
    {
        Board = board;
        RootCandidateIndex = rootCandidateIndex;
        Generation = generation;
        Role = role;
        Ply = ply;
        BaselineScore = baselineScore;
        ShockMagnitude = shockMagnitude;
        Kind = kind;
        RemainingHorizon = remainingHorizon;
        OriginalMove = originalMove;
        PriorityScore = shockMagnitude + (Math.Max(0, 32 - ply) * rootBiasWeight);
    }

    /// <summary>
    /// Higher priority score (higher shock and closer to root) has higher priority in work queue.
    /// </summary>
    public int CompareTo(ZapCrosspoint other) => other.PriorityScore.CompareTo(PriorityScore);
}
