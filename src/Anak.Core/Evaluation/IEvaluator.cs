namespace Anak.Core;

public interface IEvaluator
{
    Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves);
    Value Evaluate(in Board board) => Evaluate(in board, ReadOnlySpan<Move>.Empty);
}
