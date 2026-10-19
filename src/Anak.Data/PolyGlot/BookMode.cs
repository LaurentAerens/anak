namespace Anak.Data.PolyGlot;

/// <summary>
/// Move selection strategy when probing the PolyGlot opening book.
/// </summary>
public enum BookMode
{
    /// <summary>
    /// Weighted random selection proportional to entry weights (provides natural tournament variety).
    /// </summary>
    WeightedRandom,

    /// <summary>
    /// Deterministically selects the single move with the highest weight.
    /// </summary>
    BestMove,

    /// <summary>
    /// Chooses uniformly at random among all non-zero weight moves.
    /// </summary>
    UniformRandom
}
