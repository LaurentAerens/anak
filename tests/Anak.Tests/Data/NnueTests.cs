using Anak.Core;
using Anak.Data.Nnue;
using Xunit;

namespace Anak.Tests.Data;

public sealed class NnueTests
{
    [Fact]
    public void MultiTierEvaluator_IncrementalSearchStack_MatchesFullEvaluation()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        Assert.True(evaluator.IsActive);

        Board board = Fen.Parse("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        evaluator.InitSearch(in board);

        MoveList moves = new();
        MoveGenerator.GenerateLegalMoves(in board, ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];
            Board next = board;
            Piece moved = next.GetPieceAt(move.From);
            Assert.True(next.MakeMove(move, out BoardState state));

            evaluator.PushMove(move, moved, state.CapturedPiece, in next);
            int incEval = evaluator.Evaluate(in next).Raw;
            evaluator.RollbackTo(0);

            int fullEval = MultiTierNnueEvaluator.EvaluateWithWeights(in next, evaluator.ActiveWeights!);
            if (next.HalfmoveClock > 0)
            {
                fullEval = (fullEval * Math.Max(0, 200 - next.HalfmoveClock)) / 200;
            }

            Assert.Equal(fullEval, incEval);
        }
    }

    [Fact]
    public void MultiTierEvaluator_WorkerInstance_EvaluatesIdentically()
    {
        using var parent = new MultiTierNnueEvaluator();
        using var worker = (MultiTierNnueEvaluator)parent.CreateWorkerInstance();

        Board board = Fen.Parse("r1bq1rk1/ppp2ppp/2np1n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQ1RK1 w - - 0 7");
        parent.InitSearch(in board);
        worker.InitSearch(in board);

        Assert.Equal(parent.Evaluate(in board).Raw, worker.Evaluate(in board).Raw);
    }
}
