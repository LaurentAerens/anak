namespace Anak.Search;

/// <summary>
/// Configurable hyperparameters governing Monte Carlo Tree Search exploration and expansion.
/// </summary>
public sealed class MCTSParameters
{
    /// <summary>
    /// Exploration constant c_puct in the PUCT formula.
    /// Balances exploitation (high Q) vs exploration (high prior / low visits).
    /// Default: 1.414f (approx sqrt(2)).
    /// </summary>
    public float Cpuct { get; set; } = 1.41421356f;

    /// <summary>
    /// First Play Urgency (FPU) value assigned to unvisited nodes.
    /// In [-1.0, 1.0] range (default: 0.0f).
    /// </summary>
    public float FpuValue { get; set; } = 0.0f;

    /// <summary>
    /// Temperature parameter for move selection at root.
    /// 0.0f corresponds to greedy competitive play (move with maximum visits).
    /// Values > 0 sample proportional to (N_i)^(1/T).
    /// </summary>
    public float Temperature { get; set; } = 0.0f;

    /// <summary>
    /// Root move randomness / tie-breaking margin in percent (0 = pure greedy, 10 = sample among moves within 10% of max visits).
    /// Prevents robotic determinism when multiple candidate moves evaluate to near-identical quality.
    /// Default: 10 (10%).
    /// </summary>
    public int MoveRandomness { get; set; } = 10;

    /// <summary>
    /// Virtual loss applied to in-flight threads during multi-threaded MCTS search.
    /// Default: 1.0f.
    /// </summary>
    public float VirtualLoss { get; set; } = 1.0f;

    /// <summary>
    /// Maximum number of MCTS iterations/nodes to simulate (0 = unlimited).
    /// </summary>
    public ulong MaxNodes { get; set; } = 0;

    /// <summary>
    /// Base factor for dynamic CPUCT calculation (AlphaZero style).
    /// </summary>
    public float CpuctBase { get; set; } = 19652.0f;

    /// <summary>
    /// Factor for dynamic CPUCT calculation.
    /// </summary>
    public float CpuctFactor { get; set; } = 2.0f;

    public MCTSParameters Clone() => (MCTSParameters)MemberwiseClone();
}
