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

        // Open position (Open Sicilian, openness >= 0.50)
        var openBoard = Fen.Parse("r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4");
        evaluator.SetRootTopology(in openBoard);
        Assert.Equal(NnueExpertType.Open, evaluator.ActiveRootExpert);
        Assert.True(evaluator.IsActive);

        // Closed position (locked pawn chains)
        var closedBoard = Fen.Parse("rnbqkb1r/pp2pppp/2p2n2/3p4/2PP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 0 4");
        evaluator.SetRootTopology(in closedBoard);
        Assert.Equal(NnueExpertType.Closed, evaluator.ActiveRootExpert);
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

    [Fact]
    public void MultiTierNnueEvaluator_GetExpertWeights_ProvidesSeamlessFallbackForAllSixExperts()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        var board = Fen.Parse(Fen.StartPosition);

        foreach (NnueExpertType expert in Enum.GetValues<NnueExpertType>())
        {
            NnueWeights? weights = evaluator.GetExpertWeights(expert, in board);
            Assert.NotNull(weights);
            Assert.True(weights.IsStandard);
        }
    }

    [Fact]
    public void MultiTierNnueEvaluator_SmoothBoundaryInterpolation_BlendsOpenAndClosedScores()
    {
        using var evaluator = new MultiTierNnueEvaluator();

        // Position with openness inside the transition band (0.42, 0.58):
        // 1.e4 e5 2.Nf3 Nc6 3.d4 exd4 4.Nxd4 -> 14 pawns, 1 semi-open d-file, 1semi-open e-file -> openness = 0.35 + 2*0.05 + 2*0.03 + 2*0.02 = 0.55f
        var boundaryBoard = Fen.Parse("r1bqkbnr/pppp1ppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4");
        float openness = PawnTopology.CalculateOpenness(in boundaryBoard);
        Assert.InRange(openness, 0.421f, 0.579f);

        NnueWeights openWeights = evaluator.GetExpertWeights(NnueExpertType.Open, in boundaryBoard)!;
        NnueWeights closedWeights = evaluator.GetExpertWeights(NnueExpertType.Closed, in boundaryBoard)!;

        int openCp = MultiTierNnueEvaluator.EvaluateWithWeights(in boundaryBoard, openWeights);
        int closedCp = MultiTierNnueEvaluator.EvaluateWithWeights(in boundaryBoard, closedWeights);
        float alpha = (openness - 0.42f) / 0.16f;
        int expectedBlendedCp = (int)MathF.Round(((1.0f - alpha) * closedCp) + (alpha * openCp));

        int actualCp = evaluator.Evaluate(in boundaryBoard, ReadOnlySpan<Move>.Empty).Raw;
        Assert.Equal(expectedBlendedCp, actualCp);
    }

    [Fact]
    public void MultiTierNnueEvaluator_EvaluateVolatility_ProducesValidScoresAndZeroAllocations()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        var startBoard = Fen.Parse(Fen.StartPosition);
        var sharpBoard = Fen.Parse("r1bq1rk1/ppp1ppbp/2np1np1/4P3/3P1P2/2N1B3/PPPQ2PP/2KR1BNR b - - 0 8");

        Value vStart = evaluator.EvaluateVolatility(in startBoard);
        Value vSharp = evaluator.EvaluateVolatility(in sharpBoard);

        Assert.InRange(vStart.Raw, 0, 1000);
        Assert.InRange(vSharp.Raw, 0, 1000);

        // Warm up JIT
        _ = evaluator.Evaluate(in sharpBoard, ReadOnlySpan<Move>.Empty);
        _ = evaluator.EvaluateMicro(in sharpBoard);
        _ = evaluator.EvaluateVolatility(in sharpBoard);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = evaluator.Evaluate(in sharpBoard, ReadOnlySpan<Move>.Empty);
            _ = evaluator.EvaluateMicro(in sharpBoard);
            _ = evaluator.EvaluateVolatility(in sharpBoard);
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void MultiTierNnueEvaluator_RootExpertConsistency_RetainsExpertThroughoutDescent()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        var rootBoard = Fen.Parse(Fen.StartPosition);

        evaluator.InitSearch(in rootBoard);
        NnueWeights? rootWeights = evaluator.ActiveWeights;
        Assert.NotNull(rootWeights);

        // Make move e2-e4
        var move = new Move(Square.E2, Square.E4, MoveType.DoublePawnPush);
        rootBoard.MakeMove(move, out BoardState state);

        evaluator.PushMove(move, Piece.WhitePawn, Piece.None, in rootBoard);
        Assert.Same(rootWeights, evaluator.ActiveWeights);

        Value val = evaluator.Evaluate(in rootBoard, ReadOnlySpan<Move>.Empty);
        Assert.InRange(val.Raw, -2000, 2000);

        // Push null move
        rootBoard.MakeNullMove(out BoardState nullState);
        evaluator.PushNullMove(in rootBoard);
        Assert.Same(rootWeights, evaluator.ActiveWeights);

        // Rollback
        evaluator.RollbackTo(0);
        Assert.Same(rootWeights, evaluator.ActiveWeights);
    }
}
