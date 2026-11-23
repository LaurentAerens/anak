using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Per-worker 64-byte aligned Finny Table (King-Bucket Accumulator Cache) for ArchV2.
/// Caches [2 perspectives x 32 (kingBucket, mirrorFlag) slots] so king-bucket refreshes
/// only diff changed pieces (~2-6 features) instead of recomputing all 24-32 pieces from scratch.
/// </summary>
[SkipLocalsInit]
public sealed unsafe class NnueFinnyTable : IDisposable
{
    private const int SlotsPerSide = 32; // 16 king buckets x 2 horizontal mirror states
    private const int TotalSlots = 2 * SlotsPerSide;

    private struct SlotState
    {
        public ulong White;
        public ulong Black;
        public ulong Pawns;
        public ulong Knights;
        public ulong Bishops;
        public ulong Rooks;
        public ulong Queens;
        public ulong Kings;
    }

    private short* _accBlock;
    private int _capacityDim;
    private NnueWeights? _boundWeights;
    private readonly SlotState[] _slots = new SlotState[TotalSlots];
    private bool _disposed;

    public void EnsureInitialized(NnueWeights weights)
    {
        if (ReferenceEquals(_boundWeights, weights) && _accBlock != null && _capacityDim >= weights.HiddenDim)
        {
            return;
        }

        int dim = weights.HiddenDim;
        if (_accBlock == null || _capacityDim < dim)
        {
            if (_accBlock != null) NativeMemory.AlignedFree(_accBlock);
            nuint bytes = (nuint)(TotalSlots * dim * sizeof(short));
            _accBlock = (short*)NativeMemory.AlignedAlloc(bytes, 64);
            _capacityDim = dim;
        }

        Array.Clear(_slots, 0, TotalSlots);
        long biasBytes = (long)dim * sizeof(short);
        for (int s = 0; s < TotalSlots; s++)
        {
            Buffer.MemoryCopy(weights.FeatureBiases, _accBlock + (s * dim), biasBytes, biasBytes);
        }

        _boundWeights = weights;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RefreshWhite(in Board board, NnueWeights weights, short* dstWhiteAcc)
    {
        EnsureInitialized(weights);
        int dim = weights.HiddenDim;
        int wKingSq = NnueAccumulator.GetWhiteKingSquare(in board);
        int wBucket = weights.Buckets == 1 ? 0 : NnueAccumulator.GetKingBucket((Square)wKingSq);
        int wFlip = weights.Buckets == 1 ? 0 : ((wKingSq & 4) != 0 ? 7 : 0);
        int slotIdx = (wBucket << 1) | (wFlip != 0 ? 1 : 0);

        ref SlotState st = ref _slots[slotIdx];
        short* cachedAcc = _accBlock + (slotIdx * dim);
        short* wWeights = weights.FeatureWeights16 + (wBucket * NnueConstants.RawFeatureCount * dim);

        DiffPerspective(in board, ref st, wWeights, cachedAcc, dim, wFlip, isBlackPerspective: false);
        Buffer.MemoryCopy(cachedAcc, dstWhiteAcc, (long)dim * sizeof(short), (long)dim * sizeof(short));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RefreshBlack(in Board board, NnueWeights weights, short* dstBlackAcc)
    {
        EnsureInitialized(weights);
        int dim = weights.HiddenDim;
        int bKingSq = NnueAccumulator.GetBlackKingSquare(in board);
        int bBucket = weights.Buckets == 1 ? 0 : NnueAccumulator.GetKingBucket((Square)(bKingSq ^ 56));
        int bFlip = weights.Buckets == 1 ? 0 : ((bKingSq & 4) != 0 ? 7 : 0);
        int slotIdx = SlotsPerSide + ((bBucket << 1) | (bFlip != 0 ? 1 : 0));

        ref SlotState st = ref _slots[slotIdx];
        short* cachedAcc = _accBlock + (slotIdx * dim);
        short* bWeights = weights.FeatureWeights16 + (bBucket * NnueConstants.RawFeatureCount * dim);

        DiffPerspective(in board, ref st, bWeights, cachedAcc, dim, bFlip, isBlackPerspective: true);
        Buffer.MemoryCopy(cachedAcc, dstBlackAcc, (long)dim * sizeof(short), (long)dim * sizeof(short));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DiffPerspective(
        in Board board,
        ref SlotState st,
        short* weights,
        short* acc,
        int dim,
        int flip,
        bool isBlackPerspective)
    {
        DiffPieceType(board.White & board.Pawns, st.White & st.Pawns, PieceType.Pawn, Color.White, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.Black & board.Pawns, st.Black & st.Pawns, PieceType.Pawn, Color.Black, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.White & board.Knights, st.White & st.Knights, PieceType.Knight, Color.White, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.Black & board.Knights, st.Black & st.Knights, PieceType.Knight, Color.Black, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.White & board.Bishops, st.White & st.Bishops, PieceType.Bishop, Color.White, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.Black & board.Bishops, st.Black & st.Bishops, PieceType.Bishop, Color.Black, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.White & board.Rooks, st.White & st.Rooks, PieceType.Rook, Color.White, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.Black & board.Rooks, st.Black & st.Rooks, PieceType.Rook, Color.Black, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.White & board.Queens, st.White & st.Queens, PieceType.Queen, Color.White, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.Black & board.Queens, st.Black & st.Queens, PieceType.Queen, Color.Black, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.White & board.Kings, st.White & st.Kings, PieceType.King, Color.White, weights, acc, dim, flip, isBlackPerspective);
        DiffPieceType(board.Black & board.Kings, st.Black & st.Kings, PieceType.King, Color.Black, weights, acc, dim, flip, isBlackPerspective);

        st.White = board.White;
        st.Black = board.Black;
        st.Pawns = board.Pawns;
        st.Knights = board.Knights;
        st.Bishops = board.Bishops;
        st.Rooks = board.Rooks;
        st.Queens = board.Queens;
        st.Kings = board.Kings;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DiffPieceType(
        ulong curBB,
        ulong oldBB,
        PieceType pt,
        Color color,
        short* weights,
        short* acc,
        int dim,
        int flip,
        bool isBlackPerspective)
    {
        if (curBB == oldBB) return;

        ulong added = curBB & ~oldBB;
        ulong removed = oldBB & ~curBB;

        while (added != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(added);
            added &= added - 1;
            int idx = isBlackPerspective
                ? NnueAccumulator.GetBlackFeatureIndexMirrored(pt, color, sq, flip)
                : NnueAccumulator.GetWhiteFeatureIndexMirrored(pt, color, sq, flip);
            NnueAccumulator.AddFeature16(idx, weights, acc, dim);
        }

        while (removed != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(removed);
            removed &= removed - 1;
            int idx = isBlackPerspective
                ? NnueAccumulator.GetBlackFeatureIndexMirrored(pt, color, sq, flip)
                : NnueAccumulator.GetWhiteFeatureIndexMirrored(pt, color, sq, flip);
            NnueAccumulator.SubFeature16(idx, weights, acc, dim);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_accBlock != null)
            {
                NativeMemory.AlignedFree(_accBlock);
                _accBlock = null;
            }
            _disposed = true;
        }
    }
}
