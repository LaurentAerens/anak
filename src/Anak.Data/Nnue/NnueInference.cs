using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Ultra-fast zero-allocation SIMD inference kernels with AVX-512, AVX-512 VNNI, AVX2 VNNI,
/// and AVX2 hardware acceleration for Standard and Micro NNUE models.
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

        return weights.IsMicro
            ? EvaluateMicro(ourAcc, theirAcc, weights)
            : EvaluateStandard(ourAcc, theirAcc, weights);
    }

    /// <summary>
    /// Inference kernel for Micro NNUE (768 -> 16x2 -> 1).
    /// Concatenates 16+16=32 inputs directly into a single SIMD dot product.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EvaluateMicro(short* ourAcc, short* theirAcc, NnueWeights weights)
    {
        short* outputWeights = weights.OutputWeights;
        int sum = weights.OutputBias;

        if (Avx512BW.IsSupported)
        {
            Vector512<short> vAcc = Vector512.Create(Vector256.Load(ourAcc), Vector256.Load(theirAcc));
            Vector512<short> vZero = Vector512<short>.Zero;
            Vector512<short> vMax = Vector512.Create((short)NnueConstants.ClippedReluMax);

            Vector512<short> vClipped = Vector512.Min(Vector512.Max(vAcc, vZero), vMax);
            Vector512<short> w = Vector512.Load(outputWeights);

            Vector512<int> prod = Avx512BW.MultiplyAddAdjacent(vClipped, w);
            sum += Vector512.Sum(prod);
        }
        else if (Avx2.IsSupported)
        {
            // Load 16 shorts from ourAcc and 16 shorts from theirAcc
            Vector256<short> vOur = Vector256.Load(ourAcc);
            Vector256<short> vTheir = Vector256.Load(theirAcc);

            // ClippedReLU: clamp(x, 0, 127)
            Vector256<short> vZero = Vector256<short>.Zero;
            Vector256<short> vMax = Vector256.Create((short)NnueConstants.ClippedReluMax);

            Vector256<short> vOurClipped = Vector256.Min(Vector256.Max(vOur, vZero), vMax);
            Vector256<short> vTheirClipped = Vector256.Min(Vector256.Max(vTheir, vZero), vMax);

            // Dot product with output weights
            Vector256<short> wOur = Vector256.Load(outputWeights);
            Vector256<short> wTheir = Vector256.Load(outputWeights + 16);

            // Multiply and horizontal add to 32-bit integers
            Vector256<int> prodOur = Avx2.MultiplyAddAdjacent(vOurClipped, wOur);
            Vector256<int> prodTheir = Avx2.MultiplyAddAdjacent(vTheirClipped, wTheir);

            sum += Vector256.Sum(prodOur + prodTheir);
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                short clipped = Math.Clamp(ourAcc[i], (short)0, NnueConstants.ClippedReluMax);
                sum += clipped * outputWeights[i];
            }
            for (int i = 0; i < 16; i++)
            {
                short clipped = Math.Clamp(theirAcc[i], (short)0, NnueConstants.ClippedReluMax);
                sum += clipped * outputWeights[16 + i];
            }
        }

        return (sum * 25) / 1024;
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
                // Process ourAcc 32 shorts -> 32 bytes with SCReLU: (clamp(x, 0, 64)^2) >> 6
                Vector256<short> o0 = Vector256.Min(Vector256.Max(Vector256.Load(ourAcc + i), vZero), vMax);
                Vector256<short> o1 = Vector256.Min(Vector256.Max(Vector256.Load(ourAcc + i + 16), vZero), vMax);
                Vector256<short> o0Sq = Avx2.MultiplyLow(o0, o0);
                Vector256<short> o1Sq = Avx2.MultiplyLow(o1, o1);
                Vector256<short> o0Act = Avx2.ShiftRightLogical(o0Sq, 6);
                Vector256<short> o1Act = Avx2.ShiftRightLogical(o1Sq, 6);
                Vector256<byte> oBytes = Vector256.AsByte(Vector256.Narrow(o0Act, o1Act));
                oBytes.Store(pAct + i);

                // Process theirAcc 32 shorts -> 32 bytes with SCReLU: (clamp(x, 0, 64)^2) >> 6
                Vector256<short> t0 = Vector256.Min(Vector256.Max(Vector256.Load(theirAcc + i), vZero), vMax);
                Vector256<short> t1 = Vector256.Min(Vector256.Max(Vector256.Load(theirAcc + i + 16), vZero), vMax);
                Vector256<short> t0Sq = Avx2.MultiplyLow(t0, t0);
                Vector256<short> t1Sq = Avx2.MultiplyLow(t1, t1);
                Vector256<short> t0Act = Avx2.ShiftRightLogical(t0Sq, 6);
                Vector256<short> t1Act = Avx2.ShiftRightLogical(t1Sq, 6);
                Vector256<byte> tBytes = Vector256.AsByte(Vector256.Narrow(t0Act, t1Act));
                tBytes.Store(pAct + halfDim + i);
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

                Vector256<int> h01 = Avx2.HorizontalAdd(acc0, acc1);
                Vector256<int> h23 = Avx2.HorizontalAdd(acc2, acc3);
                Vector256<int> h0123 = Avx2.HorizontalAdd(h01, h23);
                Vector128<int> sums = h0123.GetLower() + h0123.GetUpper();

                Vector128<int> vBias = Sse41.ConvertToVector128Int32(hiddenBiases + k);
                Vector128<int> vClamped = Vector128.Min(Vector128.Max(vBias + Vector128.ShiftRightArithmetic(sums, 6), vZero128), vMax128);
                Vector128<int> vHidden = Vector128.ShiftRightLogical(vClamped * vClamped, 6);
                Vector128<int> vOutW = Sse41.ConvertToVector128Int32(outWeights + k);
                outAcc += vHidden * vOutW;
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

                Vector256<int> a0 = acc0.GetLower() + acc0.GetUpper();
                Vector256<int> a1 = acc1.GetLower() + acc1.GetUpper();
                Vector256<int> a2 = acc2.GetLower() + acc2.GetUpper();
                Vector256<int> a3 = acc3.GetLower() + acc3.GetUpper();

                Vector256<int> h01 = Avx2.HorizontalAdd(a0, a1);
                Vector256<int> h23 = Avx2.HorizontalAdd(a2, a3);
                Vector256<int> h0123 = Avx2.HorizontalAdd(h01, h23);
                Vector128<int> sums = h0123.GetLower() + h0123.GetUpper();

                Vector128<int> vBias = Sse41.ConvertToVector128Int32(hiddenBiases + k);
                Vector128<int> vClamped = Vector128.Min(Vector128.Max(vBias + Vector128.ShiftRightArithmetic(sums, 6), vZero128), vMax128);
                Vector128<int> vHidden = Vector128.ShiftRightLogical(vClamped * vClamped, 6);
                Vector128<int> vOutW = Sse41.ConvertToVector128Int32(outWeights + k);
                outAcc += vHidden * vOutW;
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

                Vector256<int> h01 = Avx2.HorizontalAdd(acc0, acc1);
                Vector256<int> h23 = Avx2.HorizontalAdd(acc2, acc3);
                Vector256<int> h0123 = Avx2.HorizontalAdd(h01, h23);
                Vector128<int> sums = h0123.GetLower() + h0123.GetUpper();

                Vector128<int> vBias = Sse41.ConvertToVector128Int32(hiddenBiases + k);
                Vector128<int> vClamped = Vector128.Min(Vector128.Max(vBias + Vector128.ShiftRightArithmetic(sums, 6), vZero128), vMax128);
                Vector128<int> vHidden = Vector128.ShiftRightLogical(vClamped * vClamped, 6);
                Vector128<int> vOutW = Sse41.ConvertToVector128Int32(outWeights + k);
                outAcc += vHidden * vOutW;
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
}
