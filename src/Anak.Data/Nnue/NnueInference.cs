using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Ultra-fast zero-allocation SIMD inference kernels with AVX-512, AVX-512 VNNI, AVX2 VNNI,
/// and AVX2 hardware acceleration for Standard and Micro NNUE models.
/// </summary>
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

        return (int)((long)sum * NnueConstants.OutputScale / (NnueConstants.WeightScale * NnueConstants.WeightScale));
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
        Span<byte> activated = stackalloc byte[inputDim];

        // 1. ClippedReLU activation for feature transformer output: clamp to [0..64]
        fixed (byte* pAct = activated)
        {
            ActivateFeatures(ourAcc, theirAcc, pAct, halfDim);

            // 2. Hidden Layer: inputDim inputs -> l1Dim outputs
            Span<short> hiddenOutput = stackalloc short[l1Dim];
            sbyte* hiddenWeights = weights.HiddenWeights;
            short* hiddenBiases = weights.HiddenBiases;

            ComputeHiddenLayer(pAct, hiddenWeights, hiddenBiases, hiddenOutput, inputDim, l1Dim);

            // 3. Output Layer: l1Dim inputs -> 1 scalar output
            short* outWeights = weights.OutputWeights;
            int finalSum = weights.OutputBias;

            fixed (short* pHidden = hiddenOutput)
            {
                if (Avx512BW.IsSupported)
                {
                    Vector512<int> prod = Vector512<int>.Zero;
                    for (int k = 0; k <= l1Dim - 32; k += 32)
                    {
                        Vector512<short> vHidden = Vector512.Load(pHidden + k);
                        Vector512<short> vOutW = Vector512.Load(outWeights + k);
                        prod += Avx512BW.MultiplyAddAdjacent(vHidden, vOutW);
                    }
                    finalSum += Vector512.Sum(prod);
                }
                else if (Avx2.IsSupported)
                {
                    Vector256<int> prod = Vector256<int>.Zero;
                    for (int k = 0; k <= l1Dim - 16; k += 16)
                    {
                        Vector256<short> vH = Vector256.Load(pHidden + k);
                        Vector256<short> vW = Vector256.Load(outWeights + k);
                        prod += Avx2.MultiplyAddAdjacent(vH, vW);
                    }
                    finalSum += Vector256.Sum(prod);
                }
                else
                {
                    for (int k = 0; k < l1Dim; k++)
                    {
                        finalSum += hiddenOutput[k] * outWeights[k];
                    }
                }
            }

            return (int)((long)finalSum * NnueConstants.OutputScale / (NnueConstants.WeightScale * NnueConstants.WeightScale));
        }
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
    private static void ComputeHiddenLayer(byte* pAct, sbyte* hiddenWeights, short* hiddenBiases, Span<short> hiddenOutput, int inputDim, int l1Dim)
    {
        if (AvxVnni.IsSupported)
        {
            for (int k = 0; k < l1Dim; k++)
            {
                sbyte* wCol = hiddenWeights + (k * inputDim);
                Vector256<int> acc = Vector256<int>.Zero;

                for (int j = 0; j < inputDim; j += 32)
                {
                    Vector256<byte> vAct = Vector256.Load(pAct + j);
                    Vector256<sbyte> vW = Vector256.Load(wCol + j);
                    acc = AvxVnni.MultiplyWideningAndAdd(acc, vAct, vW);
                }

                int dot = (hiddenBiases[k] * NnueConstants.WeightScale) + Vector256.Sum(acc);
                int clamped = Math.Clamp(dot / NnueConstants.WeightScale, 0, (int)NnueConstants.ClippedReluMax);
                hiddenOutput[k] = (short)((clamped * clamped) >> 6);
            }
        }
        else if (Avx512BW.IsSupported)
        {
            for (int k = 0; k < l1Dim; k++)
            {
                sbyte* wCol = hiddenWeights + (k * inputDim);
                Vector512<int> acc = Vector512<int>.Zero;

                for (int j = 0; j < inputDim; j += 32)
                {
                    Vector256<short> act16 = Avx2.ConvertToVector256Int16(Vector128.Load(pAct + j));
                    Vector256<short> act16_high = Avx2.ConvertToVector256Int16(Vector128.Load(pAct + j + 16));
                    Vector512<short> act32 = Vector512.Create(act16, act16_high);

                    Vector256<short> w16 = Avx2.ConvertToVector256Int16(Vector128.Load(wCol + j));
                    Vector256<short> w16_high = Avx2.ConvertToVector256Int16(Vector128.Load(wCol + j + 16));
                    Vector512<short> w32 = Vector512.Create(w16, w16_high);

                    acc += Avx512BW.MultiplyAddAdjacent(act32, w32);
                }

                int dot = (hiddenBiases[k] * NnueConstants.WeightScale) + Vector512.Sum(acc);
                int clamped = Math.Clamp(dot / NnueConstants.WeightScale, 0, (int)NnueConstants.ClippedReluMax);
                hiddenOutput[k] = (short)((clamped * clamped) >> 6);
            }
        }
        else if (Avx2.IsSupported)
        {
            for (int k = 0; k < l1Dim; k++)
            {
                sbyte* wCol = hiddenWeights + (k * inputDim);
                Vector256<int> acc = Vector256<int>.Zero;

                for (int j = 0; j < inputDim; j += 16)
                {
                    Vector256<short> act16 = Avx2.ConvertToVector256Int16(Vector128.Load(pAct + j));
                    Vector256<short> w16 = Avx2.ConvertToVector256Int16(Vector128.Load(wCol + j));
                    acc += Avx2.MultiplyAddAdjacent(act16, w16);
                }

                int dot = (hiddenBiases[k] * NnueConstants.WeightScale) + Vector256.Sum(acc);
                int clamped = Math.Clamp(dot / NnueConstants.WeightScale, 0, (int)NnueConstants.ClippedReluMax);
                hiddenOutput[k] = (short)((clamped * clamped) >> 6);
            }
        }
        else
        {
            for (int k = 0; k < l1Dim; k++)
            {
                int dot = hiddenBiases[k] * NnueConstants.WeightScale;
                sbyte* wCol = hiddenWeights + (k * inputDim);

                for (int j = 0; j < inputDim; j++)
                {
                    dot += pAct[j] * wCol[j];
                }

                int clamped = Math.Clamp(dot / NnueConstants.WeightScale, 0, (int)NnueConstants.ClippedReluMax);
                hiddenOutput[k] = (short)((clamped * clamped) >> 6);
            }
        }
    }
}
