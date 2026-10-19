using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Dual-perspective HalfKP accumulator management for NNUE inference.
/// Incremental updates during move execution and fast full refresh on demand.
/// </summary>
public static unsafe class NnueAccumulator
{
    /// <summary>
    /// Computes the HalfKP feature index from White's perspective: [0..767].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWhiteFeatureIndex(PieceType pieceType, Color color, Square square)
    {
        int pIdx = (int)pieceType - 1; // Pawn=0, Knight=1, Bishop=2, Rook=3, Queen=4, King=5
        int cIdx = (int)color;         // White=0, Black=1
        return ((cIdx * 6) + pIdx) * 64 + (int)square;
    }

    /// <summary>
    /// Computes the HalfKP feature index from Black's perspective: [0..767] (mirrored rank and opposite color).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBlackFeatureIndex(PieceType pieceType, Color color, Square square)
    {
        int pIdx = (int)pieceType - 1;
        int cIdx = 1 - (int)color; // Invert color
        int mirroredSquare = (int)square ^ 56; // Mirror rank (rank 0 <-> rank 7)
        return ((cIdx * 6) + pIdx) * 64 + mirroredSquare;
    }

    /// <summary>
    /// Computes full accumulator from scratch for White and Black perspectives.
    /// </summary>
    public static void ComputeFull(in Board board, NnueWeights weights, short* whiteAcc, short* blackAcc)
    {
        int dim = weights.HiddenDim;
        sbyte* ftWeights = weights.FeatureWeights;
        short* ftBiases = weights.FeatureBiases;

        // 1. Initialize with biases
        Buffer.MemoryCopy(ftBiases, whiteAcc, dim * sizeof(short), dim * sizeof(short));
        Buffer.MemoryCopy(ftBiases, blackAcc, dim * sizeof(short), dim * sizeof(short));

        // 2. Accumulate features for every piece on the board
        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(occupied);
            Piece piece = board.GetPieceAt(sq);
            if (piece != Piece.None)
            {
                int whiteIdx = GetWhiteFeatureIndex(piece.Type(), piece.Color(), sq);
                int blackIdx = GetBlackFeatureIndex(piece.Type(), piece.Color(), sq);

                AddFeature(whiteIdx, ftWeights, whiteAcc, dim);
                AddFeature(blackIdx, ftWeights, blackAcc, dim);
            }
            occupied &= occupied - 1;
        }
    }

    /// <summary>
    /// Adds a feature to an accumulator: acc[i] += weights[featureIdx * dim + i].
    /// Vectorized with SIMD instructions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddFeature(int featureIdx, sbyte* weights, short* acc, int dim)
    {
        sbyte* wPtr = weights + (featureIdx * dim);

        int i = 0;
        if (Vector128.IsHardwareAccelerated && dim >= 16)
        {
            for (; i <= dim - 16; i += 16)
            {
                Vector128<sbyte> w128 = Vector128.Load(wPtr + i);
                Vector128<short> wLo = Vector128.WidenLower(w128);
                Vector128<short> wHi = Vector128.WidenUpper(w128);

                Vector128<short> aLo = Vector128.Load(acc + i);
                Vector128<short> aHi = Vector128.Load(acc + i + 8);

                (aLo + wLo).Store(acc + i);
                (aHi + wHi).Store(acc + i + 8);
            }
        }

        for (; i < dim; i++)
        {
            acc[i] += wPtr[i];
        }
    }

    /// <summary>
    /// Subtracts a feature from an accumulator: acc[i] -= weights[featureIdx * dim + i].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubFeature(int featureIdx, sbyte* weights, short* acc, int dim)
    {
        sbyte* wPtr = weights + (featureIdx * dim);

        int i = 0;
        if (Vector128.IsHardwareAccelerated && dim >= 16)
        {
            for (; i <= dim - 16; i += 16)
            {
                Vector128<sbyte> w128 = Vector128.Load(wPtr + i);
                Vector128<short> wLo = Vector128.WidenLower(w128);
                Vector128<short> wHi = Vector128.WidenUpper(w128);

                Vector128<short> aLo = Vector128.Load(acc + i);
                Vector128<short> aHi = Vector128.Load(acc + i + 8);

                (aLo - wLo).Store(acc + i);
                (aHi - wHi).Store(acc + i + 8);
            }
        }

        for (; i < dim; i++)
        {
            acc[i] -= wPtr[i];
        }
    }
}
