namespace Anak.Core;

/// <summary>
/// Extended evaluation interface supporting root topology pinning (Open vs Closed)
/// and ultra-fast tactical watchdog micro-evaluations.
/// </summary>
public interface IMultiTierEvaluator : IEvaluator
{
    /// <summary>
    /// Pins the active neural model (Open vs Closed) based on the root board's pawn topology.
    /// Called once at search initialization.
    /// </summary>
    void SetRootTopology(in Board rootBoard);

    /// <summary>
    /// Evaluates the position using the ultra-compact Micro-NNUE model for fast blunder and trap checking.
    /// </summary>
    Value EvaluateMicro(in Board board);
}
