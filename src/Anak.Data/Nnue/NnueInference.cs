using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Ultra-fast zero-allocation SIMD inference kernels (AVX-512BW / AVX2 / scalar)
/// for Anak v1.2+ ArchV2 INT16 single-layer networks (Pairwise-Mul and SCReLU).
/// </summary>
[SkipLocalsInit]
public static unsafe class NnueInference
{
    /// <summary>
    /// Computes the material-phase output bucket index in [0..outputBuckets-1].
    /// For 8 buckets: (pieceCount - 2) / 4 maps 2..32 pieces smoothly across buckets 0..7.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetV2OutputBucket(in Board board, int outputBuckets)
    {
        if (outputBuckets == 8)
        {
            int idx = (BitOperations.PopCount(board.Occupied) - 2) >> 2;
            return (uint)idx < 8u ? idx : (idx < 0 ? 0 : 7);
        }
        if (outputBuckets <= 1) return 0;
        int pieces = BitOperations.PopCount(board.Occupied);
        int divisor = (32 + outputBuckets - 1) / outputBuckets;
        return Math.Clamp((pieces - 2) / divisor, 0, outputBuckets - 1);
    }

    /// <summary>
    /// Evaluates an ArchV2 wide INT16 single-layer network (Pairwise-Mul or SCReLU) for the given output bucket.
    /// Zero intermediate shift truncation, overflow-safe int64 reduction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int EvaluateV2(short* whiteAcc, short* blackAcc, NnueWeights weights, Color sideToMove, int outputBucket)
    {
        short* ourAcc = sideToMove == Color.White ? whiteAcc : blackAcc;
        short* theirAcc = sideToMove == Color.White ? blackAcc : whiteAcc;

        int dim = weights.HiddenDim;
        short* outW = weights.GetV2OutputWeights(outputBucket);
        long sum;
        if (weights.IsV2Pairwise)
        {
            int halfDim = dim >> 1;
            sum = DotPairwiseDualV2(ourAcc, theirAcc, outW, halfDim);
        }
        else
        {
            sum = DotScreluV2(ourAcc, outW, dim) + DotScreluV2(theirAcc, outW + dim, dim);
        }

        long scaled = (sum / NnueConstants.V2QA) + weights.GetV2OutputBias(outputBucket);
        return (int)((scaled * NnueConstants.V2OutputScale) / (NnueConstants.V2QA * NnueConstants.V2QB));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long DotPairwiseDualV2(short* ourAcc, short* theirAcc, short* outW, int halfDim)
    {
        short* ourB = ourAcc + halfDim;
        short* theirB = theirAcc + halfDim;
        short* wTheir = outW + halfDim;
        long total = 0;
        int i = 0;

        if (Avx512BW.IsSupported && halfDim >= 64)
        {
            Vector512<short> vZero = Vector512<short>.Zero;
            Vector512<short> vMax = Vector512.Create(NnueConstants.V2QA);
            Vector512<int> sum0 = Vector512<int>.Zero;
            Vector512<int> sum1 = Vector512<int>.Zero;
            Vector512<int> sum2 = Vector512<int>.Zero;
            Vector512<int> sum3 = Vector512<int>.Zero;

            for (; i <= halfDim - 64; i += 64)
            {
                Vector512<short> oa0 = Vector512.Min(Vector512.Max(Vector512.Load(ourAcc + i), vZero), vMax);
                Vector512<short> ob0 = Vector512.Min(Vector512.Max(Vector512.Load(ourB + i), vZero), vMax);
                Vector512<short> oa1 = Vector512.Min(Vector512.Max(Vector512.Load(ourAcc + i + 32), vZero), vMax);
                Vector512<short> ob1 = Vector512.Min(Vector512.Max(Vector512.Load(ourB + i + 32), vZero), vMax);
                sum0 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyLow(oa0, Vector512.Load(outW + i)), ob0);
                sum1 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyLow(oa1, Vector512.Load(outW + i + 32)), ob1);

                Vector512<short> ta0 = Vector512.Min(Vector512.Max(Vector512.Load(theirAcc + i), vZero), vMax);
                Vector512<short> tb0 = Vector512.Min(Vector512.Max(Vector512.Load(theirB + i), vZero), vMax);
                Vector512<short> ta1 = Vector512.Min(Vector512.Max(Vector512.Load(theirAcc + i + 32), vZero), vMax);
                Vector512<short> tb1 = Vector512.Min(Vector512.Max(Vector512.Load(theirB + i + 32), vZero), vMax);
                sum2 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyLow(ta0, Vector512.Load(wTheir + i)), tb0);
                sum3 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyLow(ta1, Vector512.Load(wTheir + i + 32)), tb1);
            }

            (Vector512<long> lo0, Vector512<long> hi0) = Vector512.Widen(sum0);
            (Vector512<long> lo1, Vector512<long> hi1) = Vector512.Widen(sum1);
            (Vector512<long> lo2, Vector512<long> hi2) = Vector512.Widen(sum2);
            (Vector512<long> lo3, Vector512<long> hi3) = Vector512.Widen(sum3);
            total = Vector512.Sum((lo0 + hi0) + (lo1 + hi1) + (lo2 + hi2) + (lo3 + hi3));
        }
        else if (Avx2.IsSupported && halfDim >= 32)
        {
            Vector256<short> vZero = Vector256<short>.Zero;
            Vector256<short> vMax = Vector256.Create(NnueConstants.V2QA);
            Vector256<int> sum0 = Vector256<int>.Zero;
            Vector256<int> sum1 = Vector256<int>.Zero;
            Vector256<int> sum2 = Vector256<int>.Zero;
            Vector256<int> sum3 = Vector256<int>.Zero;

            for (; i <= halfDim - 32; i += 32)
            {
                Vector256<short> oa0 = Vector256.Min(Vector256.Max(Vector256.Load(ourAcc + i), vZero), vMax);
                Vector256<short> ob0 = Vector256.Min(Vector256.Max(Vector256.Load(ourB + i), vZero), vMax);
                Vector256<short> oa1 = Vector256.Min(Vector256.Max(Vector256.Load(ourAcc + i + 16), vZero), vMax);
                Vector256<short> ob1 = Vector256.Min(Vector256.Max(Vector256.Load(ourB + i + 16), vZero), vMax);
                sum0 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(oa0, Vector256.Load(outW + i)), ob0);
                sum1 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(oa1, Vector256.Load(outW + i + 16)), ob1);

                Vector256<short> ta0 = Vector256.Min(Vector256.Max(Vector256.Load(theirAcc + i), vZero), vMax);
                Vector256<short> tb0 = Vector256.Min(Vector256.Max(Vector256.Load(theirB + i), vZero), vMax);
                Vector256<short> ta1 = Vector256.Min(Vector256.Max(Vector256.Load(theirAcc + i + 16), vZero), vMax);
                Vector256<short> tb1 = Vector256.Min(Vector256.Max(Vector256.Load(theirB + i + 16), vZero), vMax);
                sum2 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(ta0, Vector256.Load(wTheir + i)), tb0);
                sum3 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(ta1, Vector256.Load(wTheir + i + 16)), tb1);
            }

            (Vector256<long> lo0, Vector256<long> hi0) = Vector256.Widen(sum0);
            (Vector256<long> lo1, Vector256<long> hi1) = Vector256.Widen(sum1);
            (Vector256<long> lo2, Vector256<long> hi2) = Vector256.Widen(sum2);
            (Vector256<long> lo3, Vector256<long> hi3) = Vector256.Widen(sum3);
            total = Vector256.Sum((lo0 + hi0) + (lo1 + hi1) + (lo2 + hi2) + (lo3 + hi3));
        }

        for (; i < halfDim; i++)
        {
            int ova = Math.Clamp(ourAcc[i], (short)0, NnueConstants.V2QA);
            int ovb = Math.Clamp(ourB[i], (short)0, NnueConstants.V2QA);
            total += (long)(short)(ova * outW[i]) * ovb;

            int tva = Math.Clamp(theirAcc[i], (short)0, NnueConstants.V2QA);
            int tvb = Math.Clamp(theirB[i], (short)0, NnueConstants.V2QA);
            total += (long)(short)(tva * wTheir[i]) * tvb;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long DotScreluV2(short* acc, short* w, int dim)
    {
        long total = 0;
        int i = 0;

        if (Avx512BW.IsSupported && dim >= 64)
        {
            Vector512<short> vZero = Vector512<short>.Zero;
            Vector512<short> vMax = Vector512.Create(NnueConstants.V2QA);
            Vector512<int> sum0 = Vector512<int>.Zero;
            Vector512<int> sum1 = Vector512<int>.Zero;

            for (; i <= dim - 64; i += 64)
            {
                Vector512<short> v0 = Vector512.Min(Vector512.Max(Vector512.Load(acc + i), vZero), vMax);
                Vector512<short> v1 = Vector512.Min(Vector512.Max(Vector512.Load(acc + i + 32), vZero), vMax);
                sum0 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyLow(v0, Vector512.Load(w + i)), v0);
                sum1 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyLow(v1, Vector512.Load(w + i + 32)), v1);
            }

            (Vector512<long> lo0, Vector512<long> hi0) = Vector512.Widen(sum0);
            (Vector512<long> lo1, Vector512<long> hi1) = Vector512.Widen(sum1);
            total = Vector512.Sum(lo0 + hi0 + lo1 + hi1);
        }
        else if (Avx2.IsSupported && dim >= 32)
        {
            Vector256<short> vZero = Vector256<short>.Zero;
            Vector256<short> vMax = Vector256.Create(NnueConstants.V2QA);
            Vector256<int> sum0 = Vector256<int>.Zero;
            Vector256<int> sum1 = Vector256<int>.Zero;

            for (; i <= dim - 32; i += 32)
            {
                Vector256<short> v0 = Vector256.Min(Vector256.Max(Vector256.Load(acc + i), vZero), vMax);
                Vector256<short> v1 = Vector256.Min(Vector256.Max(Vector256.Load(acc + i + 16), vZero), vMax);
                sum0 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(v0, Vector256.Load(w + i)), v0);
                sum1 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyLow(v1, Vector256.Load(w + i + 16)), v1);
            }

            (Vector256<long> lo0, Vector256<long> hi0) = Vector256.Widen(sum0);
            (Vector256<long> lo1, Vector256<long> hi1) = Vector256.Widen(sum1);
            total = Vector256.Sum(lo0 + hi0 + lo1 + hi1);
        }

        for (; i < dim; i++)
        {
            int v = Math.Clamp(acc[i], (short)0, NnueConstants.V2QA);
            total += (long)(short)(v * w[i]) * v;
        }

        return total;
    }
}
