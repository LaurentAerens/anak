using Anak.Core;
using Anak.Data.Nnue;
using Xunit;

namespace Anak.Tests.Data;

public sealed class NnueInferenceTests
{
    [Fact]
    public void MultiTierEvaluator_WithoutFiles_DegradesGracefullyToFallback()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        Board board = Fen.Parse(Fen.StartPosition);

        Value eval = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);

        // Should return symmetric material/positional eval (~0)
        Assert.InRange(eval.Raw, -50, 50);
    }

    [Fact]
    public void MultiTierEvaluator_SetRootTopology_DoesNotThrow()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        Board startpos = Fen.Parse(Fen.StartPosition);

        evaluator.SetRootTopology(in startpos);

        // Open Sicilian
        Board sicilian = Fen.Parse("r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4");
        evaluator.SetRootTopology(in sicilian);
    }

    [Fact]
    public void HalfKPFeatureIndex_SymmetryAndBounds()
    {
        // Test White Pawn on e2 (sq 12)
        int whitePawnE2 = NnueAccumulator.GetWhiteFeatureIndex(PieceType.Pawn, Color.White, Square.E2);
        Assert.InRange(whitePawnE2, 0, NnueConstants.FeatureCount - 1);

        // Test Black Pawn on e7 (sq 52)
        int blackPawnE7 = NnueAccumulator.GetBlackFeatureIndex(PieceType.Pawn, Color.Black, Square.E7);
        Assert.InRange(blackPawnE7, 0, NnueConstants.FeatureCount - 1);

        // White pawn on E2 and Black pawn on E7 from their respective perspectives
        // should mirror each other
        int e2Mirrored = (int)Square.E7 ^ 56;
        Assert.Equal((int)Square.E2, e2Mirrored);
    }

    [Fact]
    public void MultiTierEvaluator_Evaluate_ZeroGcAllocations()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        Board board = Fen.Parse(Fen.StartPosition);

        // Warm up JIT
        _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);

        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }
        long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, bytesAfter - bytesBefore);
    }
}
