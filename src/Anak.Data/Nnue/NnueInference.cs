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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeHiddenAndOutputLayers(
        byte* pAct,
        sbyte* hiddenWeights,
        short* hiddenBiases,
        short* outWeights,
        int inputDim,
        int l1Dim)
    {
        if (AvxVnni.IsSupported)
        {
            Vector128<int> vZero128 = Vector128<int>.Zero;
            Vector128<int> vMax128 = Vector128.Create((int)NnueConstants.ClippedReluMax);
            Vector128<int> outAcc = Vector128<int>.Zero;

            for (int k = 0; k < l1Dim; k += 4)
            {
                sbyte* wCol0 = hiddenWeights + (k * inputDim);
                sbyte* wCol1 = wCol0 + inputDim;
                sbyte* wCol2 = wCol1 + inputDim;
                sbyte* wCol3 = wCol2 + inputDim;

                Vector256<int> acc0 = Vector256<int>.Zero;
                Vector256<int> acc1 = Vector256<int>.Zero;
                Vector256<int> acc2 = Vector256<int>.Zero;
                Vector256<int> acc3 = Vector256<int>.Zero;

                for (int j = 0; j < inputDim; j += 32)
                {
                    Vector256<byte> vAct = Vector256.Load(pAct + j);
                    acc0 = AvxVnni.MultiplyWideningAndAdd(acc0, vAct, Vector256.Load(wCol0 + j));
                    acc1 = AvxVnni.MultiplyWideningAndAdd(acc1, vAct, Vector256.Load(wCol1 + j));
                    acc2 = AvxVnni.MultiplyWideningAndAdd(acc2, vAct, Vector256.Load(wCol2 + j));
                    acc3 = AvxVnni.MultiplyWideningAndAdd(acc3, vAct, Vector256.Load(wCol3 + j));
                }

                outAcc += ReduceAndAccumulateOutput(acc0, acc1, acc2, acc3, hiddenBiases + k, outWeights + k, vZero128, vMax128);
            }

            return Vector128.Sum(outAcc);
        }
        else if (Avx512BW.IsSupported)
        {
            Vector512<short> vOnes = Vector512<short>.One;
            Vector128<int> vZero128 = Vector128<int>.Zero;
            Vector128<int> vMax128 = Vector128.Create((int)NnueConstants.ClippedReluMax);
            Vector128<int> outAcc = Vector128<int>.Zero;

            for (int k = 0; k < l1Dim; k += 4)
            {
                sbyte* wCol0 = hiddenWeights + (k * inputDim);
                sbyte* wCol1 = wCol0 + inputDim;
                sbyte* wCol2 = wCol1 + inputDim;
                sbyte* wCol3 = wCol2 + inputDim;

                Vector512<int> acc0 = Vector512<int>.Zero;
                Vector512<int> acc1 = Vector512<int>.Zero;
                Vector512<int> acc2 = Vector512<int>.Zero;
                Vector512<int> acc3 = Vector512<int>.Zero;

                for (int j = 0; j < inputDim; j += 64)
                {
                    Vector512<byte> vAct = Vector512.Load(pAct + j);
                    acc0 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wCol0 + j)), vOnes);
                    acc1 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wCol1 + j)), vOnes);
                    acc2 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wCol2 + j)), vOnes);
                    acc3 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(vAct, Vector512.Load(wCol3 + j)), vOnes);
                }

                outAcc += ReduceAndAccumulateOutput(
                    acc0.GetLower() + acc0.GetUpper(),
                    acc1.GetLower() + acc1.GetUpper(),
                    acc2.GetLower() + acc2.GetUpper(),
                    acc3.GetLower() + acc3.GetUpper(),
                    hiddenBiases + k,
                    outWeights + k,
                    vZero128,
                    vMax128);
            }

            return Vector128.Sum(outAcc);
        }
        else if (Avx2.IsSupported)
        {
            Vector256<short> vOnes = Vector256<short>.One;
            Vector128<int> vZero128 = Vector128<int>.Zero;
            Vector128<int> vMax128 = Vector128.Create((int)NnueConstants.ClippedReluMax);
            Vector128<int> outAcc = Vector128<int>.Zero;

            for (int k = 0; k < l1Dim; k += 4)
            {
                sbyte* wCol0 = hiddenWeights + (k * inputDim);
                sbyte* wCol1 = wCol0 + inputDim;
                sbyte* wCol2 = wCol1 + inputDim;
                sbyte* wCol3 = wCol2 + inputDim;

                Vector256<int> acc0 = Vector256<int>.Zero;
                Vector256<int> acc1 = Vector256<int>.Zero;
                Vector256<int> acc2 = Vector256<int>.Zero;
                Vector256<int> acc3 = Vector256<int>.Zero;

                for (int j = 0; j < inputDim; j += 32)
                {
                    Vector256<byte> vAct = Vector256.Load(pAct + j);
                    acc0 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wCol0 + j)), vOnes);
                    acc1 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wCol1 + j)), vOnes);
                    acc2 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wCol2 + j)), vOnes);
                    acc3 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(vAct, Vector256.Load(wCol3 + j)), vOnes);
                }

                outAcc += ReduceAndAccumulateOutput(acc0, acc1, acc2, acc3, hiddenBiases + k, outWeights + k, vZero128, vMax128);
            }

            return Vector128.Sum(outAcc);
        }
        else
        {
            int outSum = 0;
            for (int k = 0; k < l1Dim; k++)
            {
                int sum = 0;
                sbyte* wCol = hiddenWeights + (k * inputDim);

                for (int j = 0; j < inputDim; j++)
                {
                    sum += pAct[j] * wCol[j];
                }

                int clamped = Math.Clamp(hiddenBiases[k] + (sum >> 6), 0, (int)NnueConstants.ClippedReluMax);
                int hiddenVal = (clamped * clamped) >> 6;
                outSum += hiddenVal * outWeights[k];
            }
            return outSum;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ReduceAndAccumulateOutput(
        Vector256<int> acc0,
        Vector256<int> acc1,
        Vector256<int> acc2,
        Vector256<int> acc3,
        short* hiddenBiases,
        short* outWeights,
        Vector128<int> vZero128,
        Vector128<int> vMax128)
    {
        Vector256<int> h01 = Avx2.HorizontalAdd(acc0, acc1);
        Vector256<int> h23 = Avx2.HorizontalAdd(acc2, acc3);
        Vector256<int> h0123 = Avx2.HorizontalAdd(h01, h23);
        Vector128<int> sums = h0123.GetLower() + h0123.GetUpper();

        Vector128<int> vBias = Sse41.ConvertToVector128Int32(hiddenBiases);
        Vector128<int> vClamped = Vector128.Min(Vector128.Max(vBias + Vector128.ShiftRightArithmetic(sums, 6), vZero128), vMax128);
        Vector128<int> vHidden = Vector128.ShiftRightLogical(vClamped * vClamped, 6);
        Vector128<int> vOutW = Sse41.ConvertToVector128Int32(outWeights);
        return vHidden * vOutW;
    }
}
