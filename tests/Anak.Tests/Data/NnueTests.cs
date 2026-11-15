using System.Buffers.Binary;
using System.IO;
using Anak.Core;
using Anak.Data.Nnue;
using Xunit;

namespace Anak.Tests.Data;

public class NnueTests
{
    [Fact]
    public unsafe void NnueWeights_LoadsArchMoE_WithSixExpertHeads_AndEvaluatesAndBlends()
    {
        int hiddenDim = NnueConstants.StandardHiddenDim; // 512
        int l1Dim = NnueConstants.StandardL1Dim;         // 64
        int buckets = NnueConstants.KingBuckets;         // 16
        int experts = NnueConstants.ExpertCount;         // 6

        int ftBytes = (buckets * NnueConstants.RawFeatureCount * hiddenDim) + (hiddenDim * sizeof(short));
        int perExpertBytes = (hiddenDim * 2 * l1Dim) + (l1Dim * sizeof(short)) + (l1Dim * sizeof(short)) + sizeof(int);
        byte[] bytes = new byte[16 + ftBytes + (experts * perExpertBytes)];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), NnueConstants.MagicHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), NnueConstants.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), NnueConstants.ArchMoE);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), (ushort)hiddenDim);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), (ushort)l1Dim);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), (ushort)((experts << 8) | buckets));

        // Give each expert head e a distinct OutputBias = (e + 1) * 4096 -> (e + 1) * 100 cp
        for (int e = 0; e < experts; e++)
        {
            int biasOffset = 16 + ftBytes + ((e + 1) * perExpertBytes) - sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(biasOffset, sizeof(int)), (e + 1) * 4096);
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"anak_test_moe_{Guid.NewGuid():N}.nnue");
        try
        {
            System.IO.File.WriteAllBytes(tempPath, bytes);
            using var weights = NnueWeights.LoadFromFile(tempPath);
            Assert.NotNull(weights);
            Assert.Equal(NnueConstants.ArchMoE, weights.ArchId);
            Assert.True(weights.IsMoE);
            Assert.Equal(hiddenDim, weights.HiddenDim);
            Assert.Equal(l1Dim, weights.L1Dim);
            Assert.Equal(buckets, weights.Buckets);
            Assert.Equal(experts, weights.Experts);

            Board board = Fen.Parse(Fen.StartPosition);
            Span<short> whiteAcc = stackalloc short[hiddenDim];
            Span<short> blackAcc = stackalloc short[hiddenDim];
            fixed (short* w = whiteAcc, b = blackAcc)
            {
                NnueAccumulator.ComputeFull(in board, weights, w, b);
                for (int e = 0; e < experts; e++)
                {
                    int score = NnueInference.EvaluateMoE(w, b, weights, board.SideToMove, e);
                    Assert.Equal((e + 1) * 100, score);
                }

                // Smooth 50/50 blend between expert 0 (100 cp) and expert 1 (200 cp) -> 150 cp
                int blended = NnueInference.EvaluateMoEBlended(w, b, weights, board.SideToMove, 0, 1, 128);
                Assert.Equal(150, blended);
            }
        }
        finally
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
        }
    }

    [Fact]
    public void MultiTierNnueEvaluator_LoadsModels()
    {
        using var evaluator = new MultiTierNnueEvaluator();

        Assert.True(evaluator.UseNnue, "UseNnue should be enabled by default");
        Assert.True(evaluator.HasOpenModel, "open.nnue or anak.nnue should be loaded");
        Assert.True(evaluator.HasClosedModel, "closed.nnue or anak.nnue should be loaded");
        Assert.True(evaluator.IsActive, "NNUE evaluator should report IsActive == true");
    }

    [Fact]
    public void MultiTierNnueEvaluator_FallsBackToEmbeddedWeightsWhenDirectoryNotFound()
    {
        string dummyDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var evaluator = new MultiTierNnueEvaluator(dummyDir);

        Assert.True(evaluator.UseNnue);
        Assert.True(evaluator.HasOpenModel, "open.nnue or anak.nnue should be loaded from embedded resource");
        Assert.True(evaluator.HasClosedModel, "closed.nnue or anak.nnue should be loaded from embedded resource");
        Assert.True(evaluator.IsActive, "NNUE evaluator should report IsActive == true when using embedded weights");
        Assert.Equal("<embedded>", evaluator.NnueDirectory);

        var board = Fen.Parse(Fen.StartPosition);
        evaluator.SetRootTopology(in board);
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);
        Value val = evaluator.Evaluate(in board, moves.AsReadOnlySpan());
        Assert.NotEqual(0, val.Raw);
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

        if (evaluator.IsUnifiedMoE)
        {
            Assert.NotNull(evaluator.ActiveWeights);
            Assert.True(evaluator.ActiveWeights.IsMoE);
            Assert.Equal(6, evaluator.ActiveWeights.Experts);
            return;
        }

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

        float blendLow = evaluator.Routing.OpenThreshold - 0.08f;
        float alpha = (openness - blendLow) / 0.16f;
        int actualCp = evaluator.Evaluate(in boundaryBoard, ReadOnlySpan<Move>.Empty).Raw;

        if (evaluator.IsUnifiedMoE)
        {
            NnueWeights moeWeights = evaluator.ActiveWeights!;
            unsafe
            {
                short* wAcc = stackalloc short[moeWeights.HiddenDim];
                short* bAcc = stackalloc short[moeWeights.HiddenDim];
                NnueAccumulator.ComputeFull(in boundaryBoard, moeWeights, wAcc, bAcc);
                int thresholdPct = (int)MathF.Round(evaluator.Routing.OpenThreshold * 100.0f);
                int low = thresholdPct - 8;
                int opennessPct = PawnTopology.CalculateOpennessPercent(in boundaryBoard);
                int alpha256 = ((opennessPct - low) * 256) / 16;
                int expectedBlendedCp = NnueInference.EvaluateMoEBlended(
                    wAcc, bAcc, moeWeights, boundaryBoard.SideToMove,
                    (int)NnueExpertType.Closed, (int)NnueExpertType.Open, alpha256);
                Assert.Equal(expectedBlendedCp, actualCp);
            }
        }
        else
        {
            NnueWeights openWeights = evaluator.GetExpertWeights(NnueExpertType.Open, in boundaryBoard)!;
            NnueWeights closedWeights = evaluator.GetExpertWeights(NnueExpertType.Closed, in boundaryBoard)!;

            int openCp = MultiTierNnueEvaluator.EvaluateWithWeights(in boundaryBoard, openWeights);
            int closedCp = MultiTierNnueEvaluator.EvaluateWithWeights(in boundaryBoard, closedWeights);
            int expectedBlendedCp = (int)MathF.Round(((1.0f - alpha) * closedCp) + (alpha * openCp));
            Assert.Equal(expectedBlendedCp, actualCp);
        }
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
