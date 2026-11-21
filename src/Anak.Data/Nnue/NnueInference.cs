using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Ultra-fast zero-allocation SIMD inference kernels with AVX-512, AVX-512 VNNI, AVX2 VNNI,
/// and AVX2 hardware acceleration for Standard and Shared-FT Multi-Expert (MoE) NNUE models.
/// </summary>
[SkipLocalsInit]
public static unsafe class NnueInference
{
    /// <summary>
    /// Evaluates the network using pre-accumulated features relative to the side to move.
    /// Returns the evaluation in centipawns.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Evaluate(short* whiteAcc, short* blackAcc, NnueWeights weights, Color sideToMove)
    {
        short* ourAcc = sideToMove == Color.White ? whiteAcc : blackAcc;
        short* theirAcc = sideToMove == Color.White ? blackAcc : whiteAcc;

        return EvaluateStandard(ourAcc, theirAcc, weights);
    }

    /// <summary>
    /// Evaluates a specific expert head of a Shared-FT Multi-Expert (ArchMoE) network.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int EvaluateMoE(short* whiteAcc, short* blackAcc, NnueWeights weights, Color sideToMove, int expertIndex)
    {
        short* ourAcc = sideToMove == Color.White ? whiteAcc : blackAcc;
        short* theirAcc = sideToMove == Color.White ? blackAcc : whiteAcc;

        int halfDim = weights.HiddenDim;
        int inputDim = halfDim * 2;
        int l1Dim = weights.L1Dim;
        byte* pAct = stackalloc byte[inputDim];

        ActivateFeatures(ourAcc, theirAcc, pAct, halfDim);

        int finalSum = weights.GetOutputBias(expertIndex) + ComputeHiddenAndOutputLayers(
            pAct,
            weights.GetHiddenWeights(expertIndex),
            weights.GetHiddenBiases(expertIndex),
            weights.GetOutputWeights(expertIndex),
            inputDim,
            l1Dim);

        return (finalSum * 25) / 1024;
    }

    /// <summary>
    /// Evaluates and smoothly blends two expert heads (<paramref name="expertA"/> and <paramref name="expertB"/>)
    /// using a single shared SCReLU feature activation pass.
    /// <paramref name="alpha256"/> is the fixed-point blend weight in [0..256] (0 = 100% expertA, 256 = 100% expertB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int EvaluateMoEBlended(
        short* whiteAcc,
        short* blackAcc,
        NnueWeights weights,
        Color sideToMove,
        int expertA,
        int expertB,
        int alpha256)
    {
        short* ourAcc = sideToMove == Color.White ? whiteAcc : blackAcc;
        short* theirAcc = sideToMove == Color.White ? blackAcc : whiteAcc;

        int halfDim = weights.HiddenDim;
        int inputDim = halfDim * 2;
        int l1Dim = weights.L1Dim;
        byte* pAct = stackalloc byte[inputDim];

        ActivateFeatures(ourAcc, theirAcc, pAct, halfDim);

        int sumA = weights.GetOutputBias(expertA) + ComputeHiddenAndOutputLayers(
            pAct,
            weights.GetHiddenWeights(expertA),
            weights.GetHiddenBiases(expertA),
            weights.GetOutputWeights(expertA),
            inputDim,
            l1Dim);

        int sumB = weights.GetOutputBias(expertB) + ComputeHiddenAndOutputLayers(
            pAct,
            weights.GetHiddenWeights(expertB),
            weights.GetHiddenBiases(expertB),
            weights.GetOutputWeights(expertB),
            inputDim,
            l1Dim);

        int cpA = (sumA * 25) / 1024;
        int cpB = (sumB * 25) / 1024;
        return (((256 - alpha256) * cpA) + (alpha256 * cpB) + 128) >> 8;
    }

    /// <summary>
    /// Inference kernel for Standard NNUE (768 -> 512x2 -> 64 -> 1, plus legacy 768 -> 256x2 -> 32 -> 1).
    /// Accelerated with AVX-512, AVX2 VNNI, and AVX2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int EvaluateStandard(short* ourAcc, short* theirAcc, NnueWeights weights)
    {
        int halfDim = weights.HiddenDim;
        int inputDim = halfDim * 2;
        int l1Dim = weights.L1Dim;
        byte* pAct = stackalloc byte[inputDim];

        // 1. SCReLU activation for feature transformer output: clamp to [0..64], square >> 6
        ActivateFeatures(ourAcc, theirAcc, pAct, halfDim);

        // 2 & 3. Fused 4-way Hidden Layer + Output Layer dot product
        int finalSum = weights.OutputBias + ComputeHiddenAndOutputLayers(
            pAct,
            weights.HiddenWeights,
            weights.HiddenBiases,
            weights.OutputWeights,
            inputDim,
            l1Dim);

        return (finalSum * 25) / 1024;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ActivateFeatures(short* ourAcc, short* theirAcc, byte* pAct, int halfDim)
    {
        if (Avx2.IsSupported)
        {
            Vector256<short> vZero = Vector256<short>.Zero;
            Vector256<short> vMax = Vector256.Create((short)NnueConstants.ClippedReluMax);

            for (int i = 0; i < halfDim; i += 32)
            {
                ActivatePerspective32(ourAcc + i, pAct + i, vZero, vMax);
                ActivatePerspective32(theirAcc + i, pAct + halfDim + i, vZero, vMax);
            }
        }
        else
        {
            for (int i = 0; i < halfDim; i++)
            {
                int oClamped = Math.Clamp(ourAcc[i], (short)0, NnueConstants.ClippedReluMax);
                pAct[i] = (byte)((oClamped * oClamped) >> 6);

                int tClamped = Math.Clamp(theirAcc[i], (short)0, NnueConstants.ClippedReluMax);
                pAct[halfDim + i] = (byte)((tClamped * tClamped) >> 6);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ActivatePerspective32(short* src, byte* dst, Vector256<short> vZero, Vector256<short> vMax)
    {
        Vector256<short> v0 = Vector256.Min(Vector256.Max(Vector256.Load(src), vZero), vMax);
        Vector256<short> v1 = Vector256.Min(Vector256.Max(Vector256.Load(src + 16), vZero), vMax);
        Vector256<short> v0Act = Avx2.ShiftRightLogical(Avx2.MultiplyLow(v0, v0), 6);
        Vector256<short> v1Act = Avx2.ShiftRightLogical(Avx2.MultiplyLow(v1, v1), 6);
        Vector256.AsByte(Vector256.Narrow(v0Act, v1Act)).Store(dst);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ComputeHiddenAndOutputLayers(
        byte* pAct,
        sbyte* hiddenWeights,
        short* hiddenBiases,
        short* outWeights,
        int inputDim,
        int l1Dim)
    {
        int numChunks = inputDim >> 2;
        uint* pAct32 = (uint*)pAct;

        if (Avx512BW.IsSupported && l1Dim == 64)
        {
            ushort* nnz = stackalloc ushort[numChunks];
            int nnzCount = CollectNnzChunks(pAct32, numChunks, nnz);

            Vector512<short> vOnes = Vector512<short>.One;
            Vector512<int> acc0 = Vector512<int>.Zero;
            Vector512<int> acc1 = Vector512<int>.Zero;
            Vector512<int> acc2 = Vector512<int>.Zero;
            Vector512<int> acc3 = Vector512<int>.Zero;

            for (int idx = 0; idx < nnzCount; idx++)
            {
                int chunk = nnz[idx];
                Vector512<byte> vAct = Vector512.Create(pAct32[chunk]).AsByte();
                sbyte* wRow = hiddenWeights + (chunk * 256);
                acc0 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wRow)), vOnes);
                acc1 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wRow + 64)), vOnes);
                acc2 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wRow + 128)), vOnes);
                acc3 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wRow + 192)), vOnes);
            }

            Vector512<int> vZero512 = Vector512<int>.Zero;
            Vector512<int> vMax512 = Vector512.Create((int)NnueConstants.ClippedReluMax);
            Vector512<int> outAcc =
                ActivateAndMulOut512(acc0, hiddenBiases, outWeights, vZero512, vMax512) +
                ActivateAndMulOut512(acc1, hiddenBiases + 16, outWeights + 16, vZero512, vMax512) +
                ActivateAndMulOut512(acc2, hiddenBiases + 32, outWeights + 32, vZero512, vMax512) +
                ActivateAndMulOut512(acc3, hiddenBiases + 48, outWeights + 48, vZero512, vMax512);
            return Vector512.Sum(outAcc);
        }
        else if (AvxVnni.IsSupported && l1Dim == 64)
        {
            ushort* nnz = stackalloc ushort[numChunks];
            int nnzCount = CollectNnzChunks(pAct32, numChunks, nnz);

            Vector256<int> acc0 = Vector256<int>.Zero;
            Vector256<int> acc1 = Vector256<int>.Zero;
            Vector256<int> acc2 = Vector256<int>.Zero;
            Vector256<int> acc3 = Vector256<int>.Zero;
            Vector256<int> acc4 = Vector256<int>.Zero;
            Vector256<int> acc5 = Vector256<int>.Zero;
            Vector256<int> acc6 = Vector256<int>.Zero;
            Vector256<int> acc7 = Vector256<int>.Zero;

            for (int idx = 0; idx < nnzCount; idx++)
            {
                int chunk = nnz[idx];
                Vector256<byte> vAct = Vector256.Create(pAct32[chunk]).AsByte();
                sbyte* wRow = hiddenWeights + (chunk * 256);
                acc0 = AvxVnni.MultiplyWideningAndAdd(acc0, vAct, Vector256.Load(wRow));
                acc1 = AvxVnni.MultiplyWideningAndAdd(acc1, vAct, Vector256.Load(wRow + 32));
                acc2 = AvxVnni.MultiplyWideningAndAdd(acc2, vAct, Vector256.Load(wRow + 64));
                acc3 = AvxVnni.MultiplyWideningAndAdd(acc3, vAct, Vector256.Load(wRow + 96));
                acc4 = AvxVnni.MultiplyWideningAndAdd(acc4, vAct, Vector256.Load(wRow + 128));
                acc5 = AvxVnni.MultiplyWideningAndAdd(acc5, vAct, Vector256.Load(wRow + 160));
                acc6 = AvxVnni.MultiplyWideningAndAdd(acc6, vAct, Vector256.Load(wRow + 192));
                acc7 = AvxVnni.MultiplyWideningAndAdd(acc7, vAct, Vector256.Load(wRow + 224));
            }

            Vector256<int> vZero256 = Vector256<int>.Zero;
            Vector256<int> vMax256 = Vector256.Create((int)NnueConstants.ClippedReluMax);
            Vector256<int> outAcc =
                ActivateAndMulOut256(acc0, hiddenBiases, outWeights, vZero256, vMax256) +
                ActivateAndMulOut256(acc1, hiddenBiases + 8, outWeights + 8, vZero256, vMax256) +
                ActivateAndMulOut256(acc2, hiddenBiases + 16, outWeights + 16, vZero256, vMax256) +
                ActivateAndMulOut256(acc3, hiddenBiases + 24, outWeights + 24, vZero256, vMax256) +
                ActivateAndMulOut256(acc4, hiddenBiases + 32, outWeights + 32, vZero256, vMax256) +
                ActivateAndMulOut256(acc5, hiddenBiases + 40, outWeights + 40, vZero256, vMax256) +
                ActivateAndMulOut256(acc6, hiddenBiases + 48, outWeights + 48, vZero256, vMax256) +
                ActivateAndMulOut256(acc7, hiddenBiases + 56, outWeights + 56, vZero256, vMax256);
            return Vector256.Sum(outAcc);
        }
        else if (Avx2.IsSupported && (l1Dim & 7) == 0)
        {
            ushort* nnz = stackalloc ushort[numChunks];
            int nnzCount = CollectNnzChunks(pAct32, numChunks, nnz);

            Vector256<short> vOnes = Vector256<short>.One;
            Vector256<int> vZero256 = Vector256<int>.Zero;
            Vector256<int> vMax256 = Vector256.Create((int)NnueConstants.ClippedReluMax);

            if (l1Dim == 64)
            {
                Vector256<int> acc0 = Vector256<int>.Zero;
                Vector256<int> acc1 = Vector256<int>.Zero;
                Vector256<int> acc2 = Vector256<int>.Zero;
                Vector256<int> acc3 = Vector256<int>.Zero;
                Vector256<int> acc4 = Vector256<int>.Zero;
                Vector256<int> acc5 = Vector256<int>.Zero;
                Vector256<int> acc6 = Vector256<int>.Zero;
                Vector256<int> acc7 = Vector256<int>.Zero;

                for (int idx = 0; idx < nnzCount; idx++)
                {
                    int chunk = nnz[idx];
                    Vector256<byte> vAct = Vector256.Create(pAct32[chunk]).AsByte();
                    sbyte* wRow = hiddenWeights + (chunk * 256);
                    acc0 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow)), vOnes);
                    acc1 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow + 32)), vOnes);
                    acc2 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow + 64)), vOnes);
                    acc3 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow + 96)), vOnes);
                    acc4 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow + 128)), vOnes);
                    acc5 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow + 160)), vOnes);
                    acc6 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow + 192)), vOnes);
                    acc7 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wRow + 224)), vOnes);
                }

                Vector256<int> outAcc =
                    ActivateAndMulOut256(acc0, hiddenBiases, outWeights, vZero256, vMax256) +
                    ActivateAndMulOut256(acc1, hiddenBiases + 8, outWeights + 8, vZero256, vMax256) +
                    ActivateAndMulOut256(acc2, hiddenBiases + 16, outWeights + 16, vZero256, vMax256) +
                    ActivateAndMulOut256(acc3, hiddenBiases + 24, outWeights + 24, vZero256, vMax256) +
                    ActivateAndMulOut256(acc4, hiddenBiases + 32, outWeights + 32, vZero256, vMax256) +
                    ActivateAndMulOut256(acc5, hiddenBiases + 40, outWeights + 40, vZero256, vMax256) +
                    ActivateAndMulOut256(acc6, hiddenBiases + 48, outWeights + 48, vZero256, vMax256) +
                    ActivateAndMulOut256(acc7, hiddenBiases + 56, outWeights + 56, vZero256, vMax256);
                return Vector256.Sum(outAcc);
            }
            else
            {
                Vector256<int> outAcc = Vector256<int>.Zero;
                int rowStride = l1Dim * 4;
                for (int k = 0; k < l1Dim; k += 8)
                {
                    Vector256<int> acc = Vector256<int>.Zero;
                    sbyte* wBase = hiddenWeights + (k * 4);
                    for (int idx = 0; idx < nnzCount; idx++)
                    {
                        int chunk = nnz[idx];
                        Vector256<byte> vAct = Vector256.Create(pAct32[chunk]).AsByte();
                        acc += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wBase + (chunk * rowStride))), vOnes);
                    }
                    outAcc += ActivateAndMulOut256(acc, hiddenBiases + k, outWeights + k, vZero256, vMax256);
                }
                return Vector256.Sum(outAcc);
            }
        }
        else
        {
            int outSum = 0;
            int rowStride = l1Dim * 4;
            for (int k = 0; k < l1Dim; k++)
            {
                int sum = 0;
                sbyte* wCol = hiddenWeights + (k * 4);
                for (int c = 0; c < numChunks; c++)
                {
                    if (pAct32[c] == 0) continue;
                    int j = c << 2;
                    sbyte* w = wCol + (c * rowStride);
                    sum += pAct[j] * w[0] + pAct[j + 1] * w[1] + pAct[j + 2] * w[2] + pAct[j + 3] * w[3];
                }

                int clamped = Math.Clamp(hiddenBiases[k] + (sum >> 6), 0, (int)NnueConstants.ClippedReluMax);
                int hiddenVal = (clamped * clamped) >> 6;
                outSum += hiddenVal * outWeights[k];
            }
            return outSum;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CollectNnzChunks(uint* pAct32, int numChunks, ushort* nnz)
    {
        int nnzCount = 0;
        Vector256<uint> vZero = Vector256<uint>.Zero;
        int c = 0;
        for (; c + 8 <= numChunks; c += 8)
        {
            Vector256<uint> v = Vector256.Load(pAct32 + c);
            uint eqMask = (uint)Avx.MoveMask(Vector256.Equals(v, vZero).AsSingle());
            uint nnzMask = (~eqMask) & 0xFFu;
            while (nnzMask != 0)
            {
                int bit = BitOperations.TrailingZeroCount(nnzMask);
                nnzMask &= nnzMask - 1;
                nnz[nnzCount++] = (ushort)(c + bit);
            }
        }
        for (; c < numChunks; c++)
        {
            if (pAct32[c] != 0) nnz[nnzCount++] = (ushort)c;
        }
        return nnzCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ActivateAndMulOut256(
        Vector256<int> acc,
        short* hiddenBiases,
        short* outWeights,
        Vector256<int> vZero,
        Vector256<int> vMax)
    {
        Vector256<int> vBias = Avx2.ConvertToVector256Int32(Vector128.Load(hiddenBiases));
        Vector256<int> vClamped = Vector256.Min(Vector256.Max(vBias + Vector256.ShiftRightArithmetic(acc, 6), vZero), vMax);
        Vector256<int> vHidden = Vector256.ShiftRightLogical(vClamped * vClamped, 6);
        Vector256<int> vOutW = Avx2.ConvertToVector256Int32(Vector128.Load(outWeights));
        return vHidden * vOutW;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<int> ActivateAndMulOut512(
        Vector512<int> acc,
        short* hiddenBiases,
        short* outWeights,
        Vector512<int> vZero,
        Vector512<int> vMax)
    {
        Vector512<int> vBias = Avx512BW.ConvertToVector512Int32(Vector256.Load(hiddenBiases));
        Vector512<int> vClamped = Vector512.Min(Vector512.Max(vBias + Vector512.ShiftRightArithmetic(acc, 6), vZero), vMax);
        Vector512<int> vHidden = Vector512.ShiftRightLogical(vClamped * vClamped, 6);
        Vector512<int> vOutW = Avx512BW.ConvertToVector512Int32(Vector256.Load(outWeights));
        return vHidden * vOutW;
    }
}
