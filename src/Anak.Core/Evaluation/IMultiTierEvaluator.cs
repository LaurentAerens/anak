namespace Anak.Core;

/// <summary>
/// Extended evaluation interface supporting multi-expert routing.
/// </summary>
public interface IMultiTierEvaluator : IEvaluator
{
    /// <summary>
    /// Gets the active root expert classified during <see cref="SetRootTopology"/>.
    /// </summary>
    NnueExpertType ActiveRootExpert { get; }

    /// <summary>
    /// Gets whether the evaluator is running a unified Shared-FT Multi-Head (ArchMoE) network
    /// where all expert heads share a single calibrated scale and feature transformer.
    /// </summary>
    bool IsUnifiedMoE => false;

    /// <summary>
    /// Classifies the board position into one of the specialized NNUE experts.
    /// </summary>
    NnueExpertType ClassifyBoard(in Board board);

    /// <summary>
    /// Classifies and pins the active root neural expert based on the root board's topology and phase.
    /// Called once at search initialization.
    /// </summary>
    void SetRootTopology(in Board rootBoard);
}
