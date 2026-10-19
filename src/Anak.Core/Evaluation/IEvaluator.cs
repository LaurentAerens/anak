namespace Anak.Core;

public interface IEvaluator
{
    Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves);
}
