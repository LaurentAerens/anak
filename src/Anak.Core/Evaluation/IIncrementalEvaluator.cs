namespace Anak.Core;

/// <summary>
/// Extended evaluator interface supporting incremental accumulator updates along search tree descent.
/// Eliminates full piece-feature recalculations at leaf nodes.
/// </summary>
public interface IIncrementalEvaluator : IEvaluator
{
    /// <summary>
    /// Initializes accumulator stack at root position before starting search simulations.
    /// </summary>
    void InitSearch(in Board rootBoard);

    /// <summary>
    /// Pushes a move along the search descent path and updates the accumulator incrementally.
    /// </summary>
    void PushMove(Move move, Piece movedPiece, Piece capturedPiece, in Board boardAfter);

    /// <summary>
    /// Pushes a null move along the search descent path (pieces unchanged, only key and depth updated).
    /// </summary>
    void PushNullMove(in Board boardAfter);

    /// <summary>
    /// Rolls back the internal accumulator stack depth (e.g. to 0 after finishing a simulation path).
    /// </summary>
    void RollbackTo(int depth);

    /// <summary>
    /// Creates a worker evaluator instance sharing neural weights with an independent accumulator stack.
    /// </summary>
    IIncrementalEvaluator CreateWorkerInstance();
}
