using System.Buffers.Binary;
using Anak.Core;
using Anak.Data.Nnue;
using Xunit;

namespace Anak.Tests.Data;

public unsafe class NnueV2Tests
{
    private static byte[] BuildSyntheticV2Network(int hiddenDim = 1024, int kingBuckets = 16, int outputBuckets = 8, int seed = 12345, int actFlags = NnueConstants.V2ActScrelu)
    {
        var rng = new Random(seed);
        int featureCount = kingBuckets * NnueConstants.RawFeatureCount;
        int ftWeightsBytes = featureCount * hiddenDim * sizeof(short);
        int ftBiasesBytes = hiddenDim * sizeof(short);
        int outWeightsPerBucket = actFlags == NnueConstants.V2ActPairwise ? hiddenDim : (hiddenDim * 2);
        int perBucketOutBytes = (outWeightsPerBucket * sizeof(short)) + sizeof(int);
        int totalBytes = 16 + ftWeightsBytes + ftBiasesBytes + (outputBuckets * perBucketOutBytes);

        byte[] data = new byte[totalBytes];
        Span<byte> span = data;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], NnueConstants.MagicHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], NnueConstants.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], NnueConstants.ArchV2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..12], (ushort)hiddenDim);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..14], (ushort)outputBuckets);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..16], (ushort)(((actFlags & 0xFF) << 8) | (kingBuckets & 0xFF)));

        int offset = 16;
        for (int i = 0; i < featureCount * hiddenDim; i++)
        {
            short val = (short)rng.Next(-40, 41);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset, 2), val);
            offset += 2;
        }
        for (int i = 0; i < hiddenDim; i++)
        {
            short val = (short)rng.Next(-30, 80);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset, 2), val);
            offset += 2;
        }
        for (int b = 0; b < outputBuckets; b++)
        {
            for (int i = 0; i < outWeightsPerBucket; i++)
            {
                short val = (short)rng.Next(-127, 128);
                BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset, 2), val);
                offset += 2;
            }
            int bias = rng.Next(-2000, 2001);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), bias);
            offset += 4;
        }

        return data;
    }

    [Fact]
    public void LoadV2_ParsesHeaderAndWeightsCorrectly()
    {
        byte[] bytes = BuildSyntheticV2Network(hiddenDim: 1024, kingBuckets: 16, outputBuckets: 8);
        using NnueWeights? weights = NnueWeights.LoadFromSpan(bytes);

        Assert.NotNull(weights);
        Assert.True(weights!.IsV2);
        Assert.Equal(NnueConstants.ArchV2, weights.ArchId);
        Assert.Equal(1024, weights.HiddenDim);
        Assert.Equal(16, weights.Buckets);
        Assert.Equal(8, weights.OutputBuckets);
    }

    [Fact]
    public void V2_HorizontalMirroring_ProducesExactSameAccumulatorForMirroredBoards()
    {
        byte[] bytes = BuildSyntheticV2Network(hiddenDim: 256, kingBuckets: 16, outputBuckets: 8);
        using NnueWeights weights = NnueWeights.LoadFromSpan(bytes)!;

        // Kingside castled vs horizontally mirrored Queenside castled
        Board kingside = Fen.Parse("5rk1/5ppp/8/8/8/8/5PPP/5RK1 w - - 0 1");
        Board queensideMirrored = Fen.Parse("1kr5/ppp5/8/8/8/8/PPP5/1KR5 w - - 0 1");

        int evalKs = MultiTierNnueEvaluator.EvaluateWithWeights(in kingside, weights);
        int evalQs = MultiTierNnueEvaluator.EvaluateWithWeights(in queensideMirrored, weights);

        Assert.Equal(evalKs, evalQs);
    }

    [Fact]
    public void V2_IncrementalUpdate_MatchesComputeFullAcrossAllMoveTypesAndKingMirrorCrossings()
    {
        byte[] bytes = BuildSyntheticV2Network(hiddenDim: 1024, kingBuckets: 16, outputBuckets: 8);
        using NnueWeights weights = NnueWeights.LoadFromSpan(bytes)!;

        // Positions covering: startpos, castling both sides, en passant, promotions, and king crossing d/e file boundary
        string[] fens =
        [
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
            "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
            "4k3/8/8/8/8/8/8/4K3 w - - 0 1", // Ke1 -> Kd1 crosses E/D mirror boundary!
            "3k4/8/8/8/8/8/8/3K4 b - - 0 1"  // Kd8 -> Ke8 crosses D/E mirror boundary!
        ];

        short[] rootW = new short[weights.HiddenDim];
        short[] rootB = new short[weights.HiddenDim];
        short[] incW = new short[weights.HiddenDim];
        short[] incB = new short[weights.HiddenDim];
        short[] fullW = new short[weights.HiddenDim];
        short[] fullB = new short[weights.HiddenDim];

        foreach (string fen in fens)
        {
            Board board = Fen.Parse(fen);
            fixed (short* pRootW = rootW, pRootB = rootB, pIncW = incW, pIncB = incB, pFullW = fullW, pFullB = fullB)
            {
                NnueAccumulator.ComputeFull(in board, weights, pRootW, pRootB);

                MoveList moves = new();
                MoveGenerator.GenerateLegalMoves(in board, ref moves);

                for (int m = 0; m < moves.Count; m++)
                {
                    Move move = moves[m];
                    Board next = board;
                    Piece movedPiece = next.GetPieceAt(move.From);
                    Assert.True(next.MakeMove(move, out BoardState state));

                    NnueAccumulator.UpdateMove(move, movedPiece, state.CapturedPiece, weights, pRootW, pRootB, pIncW, pIncB, in next);
                    NnueAccumulator.ComputeFull(in next, weights, pFullW, pFullB);

                    for (int i = 0; i < weights.HiddenDim; i++)
                    {
                        Assert.Equal(pFullW[i], pIncW[i]);
                        Assert.Equal(pFullB[i], pIncB[i]);
                    }

                    int bucket = NnueInference.GetV2OutputBucket(in next, weights.OutputBuckets);
                    int incEval = NnueInference.EvaluateV2(pIncW, pIncB, weights, next.SideToMove, bucket);
                    int fullEval = NnueInference.EvaluateV2(pFullW, pFullB, weights, next.SideToMove, bucket);
                    Assert.Equal(fullEval, incEval);
                }
            }
        }
    }

    [Fact]
    public void V2_EvaluateV2_SimdMatchesScalarReference()
    {
        byte[] bytes = BuildSyntheticV2Network(hiddenDim: 1024, kingBuckets: 16, outputBuckets: 8, seed: 99);
        using NnueWeights weights = NnueWeights.LoadFromSpan(bytes)!;

        Board board = Fen.Parse("r1bq1rk1/ppp2ppp/2np1n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQ1RK1 w - - 0 7");
        short[] wAcc = new short[weights.HiddenDim];
        short[] bAcc = new short[weights.HiddenDim];

        fixed (short* pW = wAcc, pB = bAcc)
        {
            NnueAccumulator.ComputeFull(in board, weights, pW, pB);
            int bucket = NnueInference.GetV2OutputBucket(in board, weights.OutputBuckets);
            int simdEval = NnueInference.EvaluateV2(pW, pB, weights, board.SideToMove, bucket);

            short* outW = weights.GetV2OutputWeights(bucket);
            long scalarSum = 0;
            for (int i = 0; i < weights.HiddenDim; i++)
            {
                int vo = Math.Clamp(pW[i], (short)0, NnueConstants.V2QA);
                scalarSum += (long)(short)(vo * outW[i]) * vo;
                int vt = Math.Clamp(pB[i], (short)0, NnueConstants.V2QA);
                scalarSum += (long)(short)(vt * outW[weights.HiddenDim + i]) * vt;
            }
            long scaled = (scalarSum / NnueConstants.V2QA) + weights.GetV2OutputBias(bucket);
            int scalarEval = (int)((scaled * NnueConstants.V2OutputScale) / (NnueConstants.V2QA * NnueConstants.V2QB));

            Assert.Equal(scalarEval, simdEval);
        }
    }

    [Fact]
    public void V2_PairwiseMul_SimdMatchesScalarReference()
    {
        byte[] bytes = BuildSyntheticV2Network(hiddenDim: 1024, kingBuckets: 16, outputBuckets: 8, seed: 777, actFlags: NnueConstants.V2ActPairwise);
        using NnueWeights weights = NnueWeights.LoadFromSpan(bytes)!;
        Assert.True(weights.IsV2Pairwise);

        Board board = Fen.Parse("r1bq1rk1/ppp2ppp/2np1n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQ1RK1 w - - 0 7");
        short[] wAcc = new short[weights.HiddenDim];
        short[] bAcc = new short[weights.HiddenDim];

        fixed (short* pW = wAcc, pB = bAcc)
        {
            NnueAccumulator.ComputeFull(in board, weights, pW, pB);
            int bucket = NnueInference.GetV2OutputBucket(in board, weights.OutputBuckets);
            int simdEval = NnueInference.EvaluateV2(pW, pB, weights, board.SideToMove, bucket);

            short* outW = weights.GetV2OutputWeights(bucket);
            int half = weights.HiddenDim / 2;
            long scalarSum = 0;
            for (int i = 0; i < half; i++)
            {
                int ao = Math.Clamp(pW[i], (short)0, NnueConstants.V2QA);
                int bo = Math.Clamp(pW[half + i], (short)0, NnueConstants.V2QA);
                scalarSum += (long)(short)(ao * outW[i]) * bo;

                int at = Math.Clamp(pB[i], (short)0, NnueConstants.V2QA);
                int bt = Math.Clamp(pB[half + i], (short)0, NnueConstants.V2QA);
                scalarSum += (long)(short)(at * outW[half + i]) * bt;
            }
            long scaled = (scalarSum / NnueConstants.V2QA) + weights.GetV2OutputBias(bucket);
            int scalarEval = (int)((scaled * NnueConstants.V2OutputScale) / (NnueConstants.V2QA * NnueConstants.V2QB));

            Assert.Equal(scalarEval, simdEval);
        }
    }

    [Fact]
    public void V2_FinnyTable_MatchesComputeFullAcrossSequentialRefreshes()
    {
        byte[] bytes = BuildSyntheticV2Network(hiddenDim: 1024, kingBuckets: 16, outputBuckets: 8, seed: 314);
        using NnueWeights weights = NnueWeights.LoadFromSpan(bytes)!;
        using var finny = new NnueFinnyTable();

        string[] fens =
        [
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R4RK1 b kq - 1 1",
            "2kr3r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R4RK1 w - - 2 2",
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            "4k3/8/8/8/8/8/8/3K4 b - - 0 1",
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"
        ];

        short[] finnyW = new short[weights.HiddenDim];
        short[] finnyB = new short[weights.HiddenDim];
        short[] fullW = new short[weights.HiddenDim];
        short[] fullB = new short[weights.HiddenDim];

        foreach (string fen in fens)
        {
            Board board = Fen.Parse(fen);
            fixed (short* pFW = finnyW, pFB = finnyB, pFullW = fullW, pFullB = fullB)
            {
                finny.RefreshWhite(in board, weights, pFW);
                finny.RefreshBlack(in board, weights, pFB);
                NnueAccumulator.ComputeFull(in board, weights, pFullW, pFullB);

                for (int i = 0; i < weights.HiddenDim; i++)
                {
                    Assert.Equal(pFullW[i], pFW[i]);
                    Assert.Equal(pFullB[i], pFB[i]);
                }
            }
        }
    }
}
