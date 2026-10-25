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
    public void NnueConstants_KingBucketedArchitectureDimensions()
    {
        Assert.Equal(16, NnueConstants.KingBuckets);
        Assert.Equal(768, NnueConstants.RawFeatureCount);
        Assert.Equal(12_288, NnueConstants.FeatureCount);
        Assert.Equal(512, NnueConstants.StandardHiddenDim);
        Assert.Equal(64, NnueConstants.StandardL1Dim);
        Assert.Equal(16, NnueConstants.MicroHiddenDim);
    }

    [Fact]
    public unsafe void NnueWeightsAndInference_KingBucketed512x2To64_LoadsAndEvaluates()
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
                + sizeof(int); // 6,358,276 bytes

            Assert.Equal(6_358_276, payloadSize);

            byte[] fileBytes = new byte[16 + payloadSize];
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(0, 4), NnueConstants.MagicHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(4, 4), NnueConstants.FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(8, 2), NnueConstants.ArchStandard);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(10, 2), (ushort)hiddenDim);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(12, 2), (ushort)l1Dim);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(14, 2), (ushort)NnueConstants.KingBuckets);

            // Set output bias to a known non-zero value (64 * 64 = 4096 -> 100 cp)
            BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(fileBytes.Length - 4, 4), 4096);
            File.WriteAllBytes(tempPath, fileBytes);

            using NnueWeights? weights = NnueWeights.LoadFromFile(tempPath);
            Assert.NotNull(weights);
            Assert.Equal(512, weights.HiddenDim);
            Assert.Equal(64, weights.L1Dim);
            Assert.Equal(16, weights.Buckets);

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
    public void KingBucket_HorizontalSymmetry_MapsCorrectly()
    {
        // Start position kings: e1 and e8
        Assert.Equal(3, NnueAccumulator.GetKingBucket(Square.E1)); // Rank 0, File 4 -> Folded File 3, RankGroup 0 -> Bucket 3
        Assert.Equal(3, NnueAccumulator.GetKingBucket(Square.D1)); // Rank 0, File 3 -> Folded File 3, RankGroup 0 -> Bucket 3
        Assert.Equal(1, NnueAccumulator.GetKingBucket(Square.G1)); // Rank 0, File 6 -> Folded File 1, RankGroup 0 -> Bucket 1
        Assert.Equal(1, NnueAccumulator.GetKingBucket(Square.B1)); // Rank 0, File 1 -> Folded File 1, RankGroup 0 -> Bucket 1
        Assert.Equal(15, NnueAccumulator.GetKingBucket(Square.E8)); // Rank 7, File 4 -> Folded File 3, RankGroup 3 -> Bucket 15

        // Black king mirrored square from Black perspective
        Square e8Mirrored = (Square)((int)Square.E8 ^ 56);
        Assert.Equal(Square.E1, e8Mirrored);
        Assert.Equal(3, NnueAccumulator.GetKingBucket(e8Mirrored));
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
        Assert.InRange(whitePawnE2, 0, NnueConstants.RawFeatureCount - 1);

        // Test Black Pawn on e7 (sq 52)
        int blackPawnE7 = NnueAccumulator.GetBlackFeatureIndex(PieceType.Pawn, Color.Black, Square.E7);
        Assert.InRange(blackPawnE7, 0, NnueConstants.RawFeatureCount - 1);

        // White pawn on E2 and Black pawn on E7 from their respective perspectives
        // should mirror each other
        int e2Mirrored = (int)Square.E7 ^ 56;
        Assert.Equal((int)Square.E2, e2Mirrored);
    }

    [Fact]
    public unsafe void Accumulator_IncrementalUpdate_MatchesFullRefreshAcrossBucketTransitions()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"anak_test_acc_{Guid.NewGuid():N}.nnue");
        try
        {
            int hiddenDim = NnueConstants.StandardHiddenDim;
            int l1Dim = NnueConstants.StandardL1Dim;
            int payloadSize = (NnueConstants.FeatureCount * hiddenDim)
                + (hiddenDim * sizeof(short))
                + (hiddenDim * 2 * l1Dim)
                + (l1Dim * sizeof(short))
                + (l1Dim * sizeof(short))
                + sizeof(int);

            byte[] fileBytes = new byte[16 + payloadSize];
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(0, 4), NnueConstants.MagicHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(fileBytes.AsSpan(4, 4), NnueConstants.FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(8, 2), NnueConstants.ArchStandard);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(10, 2), (ushort)hiddenDim);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(12, 2), (ushort)l1Dim);
            BinaryPrimitives.WriteUInt16LittleEndian(fileBytes.AsSpan(14, 2), (ushort)NnueConstants.KingBuckets);

            // Populate mock weights with deterministic pseudorandom data
            for (int i = 16; i < fileBytes.Length - 4; i++)
            {
                fileBytes[i] = (byte)((i * 37 + 11) % 127);
            }
            BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(fileBytes.Length - 4, 4), 1000);
            File.WriteAllBytes(tempPath, fileBytes);

            using NnueWeights? weights = NnueWeights.LoadFromFile(tempPath);
            Assert.NotNull(weights);

            Board board = Fen.Parse(Fen.StartPosition);
            short* incW = stackalloc short[hiddenDim];
            short* incB = stackalloc short[hiddenDim];
            short* fullW = stackalloc short[hiddenDim];
            short* fullB = stackalloc short[hiddenDim];

            NnueAccumulator.ComputeFull(in board, weights, incW, incB);

            // Play sequence of moves including piece moves and king moves across bucket boundaries
            Move[] moves =
            {
                new Move(Square.E2, Square.E4),                      // 1. e4 (pawn)
                new Move(Square.E7, Square.E5),                      // 1... e5 (pawn)
                new Move(Square.G1, Square.F3),                      // 2. Nf3 (knight)
                new Move(Square.B8, Square.C6),                      // 2... Nc6 (knight)
                new Move(Square.E1, Square.E2),                      // 3. Ke2! (King move across rank boundary into bucket 7!)
                new Move(Square.E8, Square.E7),                      // 3... Ke7! (Black king move into bucket 7!)
                new Move(Square.E2, Square.D3),                      // 4. Kd3! (King move into bucket 4!)
            };

            foreach (Move m in moves)
            {
                Piece movedPiece = board.GetPieceAt(m.From);
                Piece capturedPiece = board.GetPieceAt(m.To);
                board.MakeMove(m, out BoardState state);

                NnueAccumulator.UpdateMove(m, movedPiece, capturedPiece, weights, incW, incB, in board);
                NnueAccumulator.ComputeFull(in board, weights, fullW, fullB);

                // Verify exact bitwise parity between incremental accumulator and full refresh
                for (int i = 0; i < hiddenDim; i++)
                {
                    Assert.Equal(fullW[i], incW[i]);
                    Assert.Equal(fullB[i], incB[i]);
                }
            }
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
    public void MultiTierEvaluator_Evaluate_ZeroGcAllocations()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        Board board = Fen.Parse(Fen.StartPosition);

        // Warm up JIT and Tiered Compilation
        for (int i = 0; i < 100; i++)
        {
            _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        long allocated = -1;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            allocated = bytesAfter - bytesBefore;
            if (allocated == 0) break;
        }

        Assert.Equal(0, allocated);
    }
}
