namespace Anak.Core;

/// <summary>
/// Extended evaluation interface supporting multi-expert routing
/// and ultra-fast tactical watchdog and volatility micro-evaluations.
/// </summary>
public interface IMultiTierEvaluator : IEvaluator
{
    /// <summary>
    /// Gets the active root expert classified during <see cref="SetRootTopology"/>.
    /// </summary>
    NnueExpertType ActiveRootExpert { get; }

    /// <summary>
    /// Classifies the board position into one of the specialized NNUE experts.
    /// </summary>
    NnueExpertType ClassifyBoard(in Board board);

    /// <summary>
    /// Classifies and pins the active root neural expert based on the root board's topology and phase.
    /// Called once at search initialization.
    /// </summary>
    void SetRootTopology(in Board rootBoard);

    /// <summary>
    /// Gets whether the ultra-compact Micro-NNUE watchdog model is loaded.
    /// </summary>
    bool HasMicroModel { get; }

    /// <summary>
    /// Evaluates the position using the ultra-compact Micro-NNUE model for fast blunder and trap checking.
    /// </summary>
    Value EvaluateMicro(in Board board);

    /// <summary>
    /// Evaluates the tactical sharpness / volatility of the position using the micro volatility network
    /// or fast bitboard tension fallback.
    /// </summary>
    Value EvaluateVolatility(in Board board);
}
