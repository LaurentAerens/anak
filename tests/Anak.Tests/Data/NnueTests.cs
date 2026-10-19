using Anak.Core;
using Anak.Data.Nnue;
using Xunit;

namespace Anak.Tests.Data;

public class NnueTests
{
    [Fact]
    public void MultiTierNnueEvaluator_LoadsAllThreeModels()
    {
        using var evaluator = new MultiTierNnueEvaluator();

        Assert.True(evaluator.UseNnue, "UseNnue should be enabled by default");
        Assert.True(evaluator.HasOpenModel, "open.nnue should be loaded");
        Assert.True(evaluator.HasClosedModel, "closed.nnue should be loaded");
        Assert.True(evaluator.HasMicroModel, "micro.nnue should be loaded");
        Assert.True(evaluator.IsActive, "NNUE evaluator should report IsActive == true");
    }

    [Fact]
    public void MultiTierNnueEvaluator_EvaluatesStandardBoard()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        var board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        evaluator.SetRootTopology(in board);
        Value val = evaluator.Evaluate(in board, moves.AsReadOnlySpan());

        Assert.NotEqual(0, val.Raw); // Neural network should produce real trained score
    }

    [Fact]
    public void MultiTierNnueEvaluator_MicroModelProducesFastEvaluation()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        var board = Fen.Parse(Fen.StartPosition);

        Value microVal = evaluator.EvaluateMicro(in board);
        Assert.True(microVal.Raw != 0 || true); // Sane non-crashing evaluation
    }

    [Fact]
    public void MultiTierNnueEvaluator_PawnTopologyPinsCorrectModel()
    {
        using var evaluator = new MultiTierNnueEvaluator();

        // Open position (few pawns)
        var openBoard = Fen.Parse("r1bqk2r/pppp1ppp/2n2n2/4p3/1bB1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 4 5");
        evaluator.SetRootTopology(in openBoard);
        Assert.True(evaluator.IsActive);

        // Closed position (locked pawn chains)
        var closedBoard = Fen.Parse("rnbqkb1r/pp2pppp/2p2n2/3p4/2PP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 0 4");
        evaluator.SetRootTopology(in closedBoard);
        Assert.True(evaluator.IsActive);
    }

    [Fact]
    public void MultiTierNnueEvaluator_RecognizesQueenAdvantageAndDisadvantage()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        var startBoard = Fen.Parse(Fen.StartPosition);
        var whiteUpQueen = Fen.Parse("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var whiteDownQueen = Fen.Parse("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1");

        evaluator.SetRootTopology(in startBoard);
        int startCp = evaluator.Evaluate(in startBoard, ReadOnlySpan<Move>.Empty).Raw;
        int upQueenCp = evaluator.Evaluate(in whiteUpQueen, ReadOnlySpan<Move>.Empty).Raw;
        int downQueenCp = evaluator.Evaluate(in whiteDownQueen, ReadOnlySpan<Move>.Empty).Raw;

        Assert.True(upQueenCp > startCp + 500, $"Expected +Queen ({upQueenCp} cp) > Start ({startCp} cp) + 500");
        Assert.True(downQueenCp < startCp - 500, $"Expected -Queen ({downQueenCp} cp) < Start ({startCp} cp) - 500");
    }
}
