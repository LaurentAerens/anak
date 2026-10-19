using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Ultra-fast zero-allocation SIMD inference kernels for Standard and Micro NNUE models.
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

        if (Avx2.IsSupported)
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

            Vector256<int> total = prodOur + prodTheir;
            sum += total.GetElement(0) + total.GetElement(1) + total.GetElement(2) + total.GetElement(3)
                 + total.GetElement(4) + total.GetElement(5) + total.GetElement(6) + total.GetElement(7);
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

        return sum / NnueConstants.OutputScale;
    }

    /// <summary>
    /// Inference kernel for Standard NNUE (768 -> 256x2 -> 32 -> 1).
    /// </summary>
    public static int EvaluateStandard(short* ourAcc, short* theirAcc, NnueWeights weights)
    {
        const int inputDim = NnueConstants.StandardHiddenDim * 2; // 512
        Span<byte> activated = stackalloc byte[inputDim];

        // 1. ClippedReLU activation for feature transformer output: clamp to [0..127]
        for (int i = 0; i < NnueConstants.StandardHiddenDim; i++)
        {
            activated[i] = (byte)Math.Clamp(ourAcc[i], (short)0, NnueConstants.ClippedReluMax);
            activated[NnueConstants.StandardHiddenDim + i] = (byte)Math.Clamp(theirAcc[i], (short)0, NnueConstants.ClippedReluMax);
        }

        // 2. Hidden Layer: 512 inputs -> 32 outputs
        Span<short> hiddenOutput = stackalloc short[32];
        sbyte* hiddenWeights = weights.HiddenWeights;
        short* hiddenBiases = weights.HiddenBiases;

        fixed (byte* pAct = activated)
        {
            for (int k = 0; k < 32; k++)
            {
                int dot = hiddenBiases[k] * NnueConstants.WeightScale;
                sbyte* wCol = hiddenWeights + (k * inputDim);

                for (int j = 0; j < inputDim; j++)
                {
                    dot += pAct[j] * wCol[j];
                }

                // Intermediate activation: ClippedReLU(dot / WeightScale)
                hiddenOutput[k] = (short)Math.Clamp(dot / (NnueConstants.WeightScale * NnueConstants.WeightScale), 0, (int)NnueConstants.ClippedReluMax);
            }
        }

        // 3. Output Layer: 32 inputs -> 1 scalar output
        short* outWeights = weights.OutputWeights;
        int finalSum = weights.OutputBias;

        for (int k = 0; k < 32; k++)
        {
            finalSum += hiddenOutput[k] * outWeights[k];
        }

        return finalSum / NnueConstants.OutputScale;
    }
}
