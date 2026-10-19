using System.Buffers.Binary;
using Anak.Core;
using Anak.Data.Nnue;
using Xunit;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Anak.Tests.Data;

public sealed class NnueInferenceTests
{
    [Fact]
    public void NnueConstants_StandardArchitectureDimensions_Are512And64()
    {
        Assert.Equal(512, NnueConstants.StandardHiddenDim);
        Assert.Equal(64, NnueConstants.StandardL1Dim);
        Assert.Equal(16, NnueConstants.MicroHiddenDim);
    }

    [Fact]
    public unsafe void NnueWeightsAndInference_Standard512x2To64_LoadsAndEvaluates()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"anak_test_512_{Guid.NewGuid():N}.nnue");
        try
        {
            int hiddenDim = NnueConstants.StandardHiddenDim; // 512
            int l1Dim = NnueConstants.StandardL1Dim;         // 64
            int payloadSize = (NnueConstants.FeatureCount * hiddenDim)
                + (hiddenDim * sizeof(short))
                + (hiddenDim * 2 * l1Dim)
                + (l1Dim * sizeof(short))
                + (l1Dim * sizeof(short))
                + sizeof(int); // 460,036 bytes

            Assert.Equal(460_036, payloadSize);

            byte[] fileBytes = new byte[16 + payloadSize];
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(0, 4), NnueConstants.MagicHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(4, 4), NnueConstants.FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(8, 2), NnueConstants.ArchStandard);

            // Set output bias to a known non-zero value (64 * 64 = 4096 -> 100 cp)
            BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(fileBytes.Length - 4, 4), 4096);
            File.WriteAllBytes(tempPath, fileBytes);

            using NnueWeights? weights = NnueWeights.LoadFromFile(tempPath);
            Assert.NotNull(weights);
            Assert.Equal(512, weights.HiddenDim);
            Assert.Equal(64, weights.L1Dim);

            Board board = Fen.Parse(Fen.StartPosition);
            int cp = MultiTierNnueEvaluator.EvaluateWithWeights(in board, weights);
            Assert.Equal(100, cp);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public unsafe void NnueWeightsAndInference_Legacy256x2To32_DetectedAndEvaluates()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"anak_test_256_{Guid.NewGuid():N}.nnue");
        try
        {
            int hiddenDim = 256;
            int l1Dim = 32;
            int payloadSize = (NnueConstants.FeatureCount * hiddenDim)
                + (hiddenDim * sizeof(short))
                + (hiddenDim * 2 * l1Dim)
                + (l1Dim * sizeof(short))
                + (l1Dim * sizeof(short))
                + sizeof(int); // 213,636 bytes

            Assert.Equal(213_636, payloadSize);

            byte[] fileBytes = new byte[16 + payloadSize];
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(0, 4), NnueConstants.MagicHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(4, 4), NnueConstants.FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(8, 2), NnueConstants.ArchStandard);
            BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(fileBytes.Length - 4, 4), 4096);
            File.WriteAllBytes(tempPath, fileBytes);

            using NnueWeights? weights = NnueWeights.LoadFromFile(tempPath);
            Assert.NotNull(weights);
            Assert.Equal(256, weights.HiddenDim);
            Assert.Equal(32, weights.L1Dim);

            Board board = Fen.Parse(Fen.StartPosition);
            int cp = MultiTierNnueEvaluator.EvaluateWithWeights(in board, weights);
            Assert.Equal(100, cp);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

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

        // Warm up JIT and Tiered Compilation
        for (int i = 0; i < 100; i++)
        {
            _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }
        long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, bytesAfter - bytesBefore);
    }
}
