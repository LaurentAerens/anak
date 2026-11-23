using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Dual-perspective INT16 King-Bucketed HalfKP accumulator with True Horizontal Mirroring (ArchV2).
/// Supports fused single-pass SIMD incremental updates (AVX-512BW / AVX2 / Vector128) and Finny Table refreshes.
/// </summary>
[SkipLocalsInit]
public static unsafe class NnueAccumulator
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetKingBucket(Square square)
    {
        int sq = (int)square;
        int file = sq & 7;
        return ((sq >> 2) & 12) | ((file ^ -(file >> 2)) & 3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWhiteKingSquare(in Board board)
    {
        ulong kings = board.White & board.Kings;
        return kings == 0 ? 4 : BitOperations.TrailingZeroCount(kings);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBlackKingSquare(in Board board)
    {
        ulong kings = board.Black & board.Kings;
        return kings == 0 ? 60 : BitOperations.TrailingZeroCount(kings);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWhiteFeatureIndexMirrored(PieceType pieceType, Color color, Square square, int flip)
    {
        return ((((int)color * 6) + (int)pieceType - 1) << 6) | ((int)square ^ flip);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBlackFeatureIndexMirrored(PieceType pieceType, Color color, Square square, int flip)
    {
        return ((((1 ^ (int)color) * 6) + (int)pieceType - 1) << 6) | (((int)square ^ 56) ^ flip);
    }

    public static void ComputeFull(in Board board, NnueWeights weights, short* whiteAcc, short* blackAcc)
    {
        int dim = weights.HiddenDim;
        Buffer.MemoryCopy(weights.FeatureBiases, whiteAcc, dim * sizeof(short), dim * sizeof(short));
        Buffer.MemoryCopy(weights.FeatureBiases, blackAcc, dim * sizeof(short), dim * sizeof(short));

        int wKingSq = GetWhiteKingSquare(in board);
        int bKingSq = GetBlackKingSquare(in board);
        int wBucket = weights.Buckets == 1 ? 0 : GetKingBucket((Square)wKingSq);
        int bBucket = weights.Buckets == 1 ? 0 : GetKingBucket((Square)(bKingSq ^ 56));
        int wFlip = weights.Buckets == 1 ? 0 : ((wKingSq & 4) != 0 ? 7 : 0);
        int bFlip = weights.Buckets == 1 ? 0 : ((bKingSq & 4) != 0 ? 7 : 0);

        short* wWeights = weights.FeatureWeights16 + (wBucket * NnueConstants.RawFeatureCount * dim);
        short* bWeights = weights.FeatureWeights16 + (bBucket * NnueConstants.RawFeatureCount * dim);

        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(occupied);
            Piece piece = board.GetPieceAt(sq);
            AddFeature16(GetWhiteFeatureIndexMirrored(piece.Type(), piece.Color(), sq, wFlip), wWeights, whiteAcc, dim);
            AddFeature16(GetBlackFeatureIndexMirrored(piece.Type(), piece.Color(), sq, bFlip), bWeights, blackAcc, dim);
            occupied &= occupied - 1;
        }
    }

    public static void RefreshWhite(in Board board, NnueWeights weights, short* whiteAcc)
    {
        int dim = weights.HiddenDim;
        Buffer.MemoryCopy(weights.FeatureBiases, whiteAcc, dim * sizeof(short), dim * sizeof(short));

        int wKingSq = GetWhiteKingSquare(in board);
        int wBucket = weights.Buckets == 1 ? 0 : GetKingBucket((Square)wKingSq);
        int wFlip = weights.Buckets == 1 ? 0 : ((wKingSq & 4) != 0 ? 7 : 0);
        short* wWeights = weights.FeatureWeights16 + (wBucket * NnueConstants.RawFeatureCount * dim);

        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(occupied);
            Piece piece = board.GetPieceAt(sq);
            if (piece != Piece.None)
            {
                AddFeature16(GetWhiteFeatureIndexMirrored(piece.Type(), piece.Color(), sq, wFlip), wWeights, whiteAcc, dim);
            }
            occupied &= occupied - 1;
        }
    }

    public static void RefreshBlack(in Board board, NnueWeights weights, short* blackAcc)
    {
        int dim = weights.HiddenDim;
        Buffer.MemoryCopy(weights.FeatureBiases, blackAcc, dim * sizeof(short), dim * sizeof(short));

        int bKingSq = GetBlackKingSquare(in board);
        int bBucket = weights.Buckets == 1 ? 0 : GetKingBucket((Square)(bKingSq ^ 56));
        int bFlip = weights.Buckets == 1 ? 0 : ((bKingSq & 4) != 0 ? 7 : 0);
        short* bWeights = weights.FeatureWeights16 + (bBucket * NnueConstants.RawFeatureCount * dim);

        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(occupied);
            Piece piece = board.GetPieceAt(sq);
            if (piece != Piece.None)
            {
                AddFeature16(GetBlackFeatureIndexMirrored(piece.Type(), piece.Color(), sq, bFlip), bWeights, blackAcc, dim);
            }
            occupied &= occupied - 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateMove(
        Move move,
        Piece movedPiece,
        Piece capturedPiece,
        NnueWeights weights,
        short* whiteAcc,
        short* blackAcc,
        in Board boardAfter,
        NnueFinnyTable? finny = null)
    {
        UpdateMove(move, movedPiece, capturedPiece, weights, whiteAcc, blackAcc, whiteAcc, blackAcc, in boardAfter, finny);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void UpdateMove(
        Move move,
        Piece movedPiece,
        Piece capturedPiece,
        NnueWeights weights,
        short* srcWhiteAcc,
        short* srcBlackAcc,
        short* dstWhiteAcc,
        short* dstBlackAcc,
        in Board boardAfter,
        NnueFinnyTable? finny = null)
    {
        int dim = weights.HiddenDim;
        Color us = movedPiece.Color();
        Color them = us.Opponent();
        PieceType movedType = movedPiece.Type();
        Square from = move.From;
        Square to = move.To;

        int wKingSq = GetWhiteKingSquare(in boardAfter);
        int bKingSq = GetBlackKingSquare(in boardAfter);
        int wBucket = weights.Buckets == 1 ? 0 : GetKingBucket((Square)wKingSq);
        int bBucket = weights.Buckets == 1 ? 0 : GetKingBucket((Square)(bKingSq ^ 56));
        int wFlip = weights.Buckets == 1 ? 0 : ((wKingSq & 4) != 0 ? 7 : 0);
        int bFlip = weights.Buckets == 1 ? 0 : ((bKingSq & 4) != 0 ? 7 : 0);

        short* wWeights = weights.FeatureWeights16 + (wBucket * NnueConstants.RawFeatureCount * dim);
        short* bWeights = weights.FeatureWeights16 + (bBucket * NnueConstants.RawFeatureCount * dim);

        if (movedType == PieceType.King && weights.Buckets > 1)
        {
            if (us == Color.White)
            {
                int bFrom = GetBlackFeatureIndexMirrored(movedType, us, from, bFlip);
                int bTo = GetBlackFeatureIndexMirrored(movedType, us, to, bFlip);

                if (move.IsCastling)
                {
                    Square rFrom = to > from ? Square.H1 : Square.A1;
                    Square rTo = to > from ? Square.F1 : Square.D1;
                    SubSubAddAddFeature16(bFrom, GetBlackFeatureIndexMirrored(PieceType.Rook, us, rFrom, bFlip),
                        bTo, GetBlackFeatureIndexMirrored(PieceType.Rook, us, rTo, bFlip), bWeights, srcBlackAcc, dstBlackAcc, dim);
                }
                else if (capturedPiece != Piece.None)
                {
                    SubSubAddFeature16(bFrom, GetBlackFeatureIndexMirrored(capturedPiece.Type(), them, to, bFlip),
                        bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                }
                else
                {
                    SubAddFeature16(bFrom, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                }

                bool crossedHalf = (((int)from ^ (int)to) & 4) != 0;
                if (GetKingBucket(from) != GetKingBucket(to) || crossedHalf || move.IsCastling)
                {
                    if (finny != null) finny.RefreshWhite(in boardAfter, weights, dstWhiteAcc);
                    else RefreshWhite(in boardAfter, weights, dstWhiteAcc);
                }
                else
                {
                    int wFrom = GetWhiteFeatureIndexMirrored(movedType, us, from, wFlip);
                    int wTo = GetWhiteFeatureIndexMirrored(movedType, us, to, wFlip);
                    if (capturedPiece != Piece.None)
                    {
                        SubSubAddFeature16(wFrom, GetWhiteFeatureIndexMirrored(capturedPiece.Type(), them, to, wFlip),
                            wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                    }
                    else
                    {
                        SubAddFeature16(wFrom, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                    }
                }
            }
            else
            {
                int wFrom = GetWhiteFeatureIndexMirrored(movedType, us, from, wFlip);
                int wTo = GetWhiteFeatureIndexMirrored(movedType, us, to, wFlip);

                if (move.IsCastling)
                {
                    Square rFrom = to > from ? Square.H8 : Square.A8;
                    Square rTo = to > from ? Square.F8 : Square.D8;
                    SubSubAddAddFeature16(wFrom, GetWhiteFeatureIndexMirrored(PieceType.Rook, us, rFrom, wFlip),
                        wTo, GetWhiteFeatureIndexMirrored(PieceType.Rook, us, rTo, wFlip), wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                }
                else if (capturedPiece != Piece.None)
                {
                    SubSubAddFeature16(wFrom, GetWhiteFeatureIndexMirrored(capturedPiece.Type(), them, to, wFlip),
                        wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                }
                else
                {
                    SubAddFeature16(wFrom, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                }

                bool crossedHalf = (((int)from ^ (int)to) & 4) != 0;
                if (GetKingBucket((Square)((int)from ^ 56)) != GetKingBucket((Square)((int)to ^ 56)) || crossedHalf || move.IsCastling)
                {
                    if (finny != null) finny.RefreshBlack(in boardAfter, weights, dstBlackAcc);
                    else RefreshBlack(in boardAfter, weights, dstBlackAcc);
                }
                else
                {
                    int bFrom = GetBlackFeatureIndexMirrored(movedType, us, from, bFlip);
                    int bTo = GetBlackFeatureIndexMirrored(movedType, us, to, bFlip);
                    if (capturedPiece != Piece.None)
                    {
                        SubSubAddFeature16(bFrom, GetBlackFeatureIndexMirrored(capturedPiece.Type(), them, to, bFlip),
                            bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                    }
                    else
                    {
                        SubAddFeature16(bFrom, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                    }
                }
            }
            return;
        }

        int wfFrom = GetWhiteFeatureIndexMirrored(movedType, us, from, wFlip);
        int bfFrom = GetBlackFeatureIndexMirrored(movedType, us, from, bFlip);
        PieceType addedType = move.IsPromotion ? move.PromotionPieceType : movedType;
        int wfTo = GetWhiteFeatureIndexMirrored(addedType, us, to, wFlip);
        int bfTo = GetBlackFeatureIndexMirrored(addedType, us, to, bFlip);

        if (capturedPiece != Piece.None)
        {
            Square capSq = move.Type == MoveType.EnPassant
                ? SquareExtensions.Create(to.File(), from.Rank())
                : to;
            PieceType capType = capturedPiece.Type();
            SubSubAddFeature16(wfFrom, GetWhiteFeatureIndexMirrored(capType, them, capSq, wFlip), wfTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
            SubSubAddFeature16(bfFrom, GetBlackFeatureIndexMirrored(capType, them, capSq, bFlip), bfTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
        }
        else if (move.IsCastling)
        {
            int rankBase = (int)us * 56;
            bool kingCastle = to > from;
            Square rFrom = (Square)(rankBase + (kingCastle ? 7 : 0));
            Square rTo = (Square)(rankBase + (kingCastle ? 5 : 3));
            SubSubAddAddFeature16(wfFrom, GetWhiteFeatureIndexMirrored(PieceType.Rook, us, rFrom, wFlip),
                wfTo, GetWhiteFeatureIndexMirrored(PieceType.Rook, us, rTo, wFlip), wWeights, srcWhiteAcc, dstWhiteAcc, dim);
            SubSubAddAddFeature16(bfFrom, GetBlackFeatureIndexMirrored(PieceType.Rook, us, rFrom, bFlip),
                bfTo, GetBlackFeatureIndexMirrored(PieceType.Rook, us, rTo, bFlip), bWeights, srcBlackAcc, dstBlackAcc, dim);
        }
        else
        {
            SubAddFeature16(wfFrom, wfTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
            SubAddFeature16(bfFrom, bfTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddFeature16(int featureIdx, short* weights, short* acc, int dim)
    {
        short* wPtr = weights + (featureIdx * dim);
        int i = 0;
        if (Avx512BW.IsSupported && dim >= 64)
        {
            for (; i <= dim - 64; i += 64)
            {
                (Vector512.Load(acc + i) + Vector512.Load(wPtr + i)).Store(acc + i);
                (Vector512.Load(acc + i + 32) + Vector512.Load(wPtr + i + 32)).Store(acc + i + 32);
            }
        }
        else if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                (Vector256.Load(acc + i) + Vector256.Load(wPtr + i)).Store(acc + i);
                (Vector256.Load(acc + i + 16) + Vector256.Load(wPtr + i + 16)).Store(acc + i + 16);
            }
        }
        else if (Vector128.IsHardwareAccelerated && dim >= 16)
        {
            for (; i <= dim - 16; i += 16)
            {
                (Vector128.Load(acc + i) + Vector128.Load(wPtr + i)).Store(acc + i);
                (Vector128.Load(acc + i + 8) + Vector128.Load(wPtr + i + 8)).Store(acc + i + 8);
            }
        }
        for (; i < dim; i++) acc[i] += wPtr[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubFeature16(int featureIdx, short* weights, short* acc, int dim)
    {
        short* wPtr = weights + (featureIdx * dim);
        int i = 0;
        if (Avx512BW.IsSupported && dim >= 64)
        {
            for (; i <= dim - 64; i += 64)
            {
                (Vector512.Load(acc + i) - Vector512.Load(wPtr + i)).Store(acc + i);
                (Vector512.Load(acc + i + 32) - Vector512.Load(wPtr + i + 32)).Store(acc + i + 32);
            }
        }
        else if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                (Vector256.Load(acc + i) - Vector256.Load(wPtr + i)).Store(acc + i);
                (Vector256.Load(acc + i + 16) - Vector256.Load(wPtr + i + 16)).Store(acc + i + 16);
            }
        }
        else if (Vector128.IsHardwareAccelerated && dim >= 16)
        {
            for (; i <= dim - 16; i += 16)
            {
                (Vector128.Load(acc + i) - Vector128.Load(wPtr + i)).Store(acc + i);
                (Vector128.Load(acc + i + 8) - Vector128.Load(wPtr + i + 8)).Store(acc + i + 8);
            }
        }
        for (; i < dim; i++) acc[i] -= wPtr[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubAddFeature16(int subIdx, int addIdx, short* weights, short* srcAcc, short* dstAcc, int dim)
    {
        short* wSub = weights + (subIdx * dim);
        short* wAdd = weights + (addIdx * dim);
        int i = 0;
        if (Avx512BW.IsSupported && dim >= 64)
        {
            for (; i <= dim - 64; i += 64)
            {
                (Vector512.Load(srcAcc + i) - Vector512.Load(wSub + i) + Vector512.Load(wAdd + i)).Store(dstAcc + i);
                (Vector512.Load(srcAcc + i + 32) - Vector512.Load(wSub + i + 32) + Vector512.Load(wAdd + i + 32)).Store(dstAcc + i + 32);
            }
        }
        else if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                (Vector256.Load(srcAcc + i) - Vector256.Load(wSub + i) + Vector256.Load(wAdd + i)).Store(dstAcc + i);
                (Vector256.Load(srcAcc + i + 16) - Vector256.Load(wSub + i + 16) + Vector256.Load(wAdd + i + 16)).Store(dstAcc + i + 16);
            }
        }
        else if (Vector128.IsHardwareAccelerated && dim >= 16)
        {
            for (; i <= dim - 16; i += 16)
            {
                (Vector128.Load(srcAcc + i) - Vector128.Load(wSub + i) + Vector128.Load(wAdd + i)).Store(dstAcc + i);
                (Vector128.Load(srcAcc + i + 8) - Vector128.Load(wSub + i + 8) + Vector128.Load(wAdd + i + 8)).Store(dstAcc + i + 8);
            }
        }
        for (; i < dim; i++) dstAcc[i] = (short)(srcAcc[i] - wSub[i] + wAdd[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubSubAddFeature16(int sub1Idx, int sub2Idx, int addIdx, short* weights, short* srcAcc, short* dstAcc, int dim)
    {
        short* wSub1 = weights + (sub1Idx * dim);
        short* wSub2 = weights + (sub2Idx * dim);
        short* wAdd = weights + (addIdx * dim);
        int i = 0;
        if (Avx512BW.IsSupported && dim >= 64)
        {
            for (; i <= dim - 64; i += 64)
            {
                (Vector512.Load(srcAcc + i) - Vector512.Load(wSub1 + i) - Vector512.Load(wSub2 + i) + Vector512.Load(wAdd + i)).Store(dstAcc + i);
                (Vector512.Load(srcAcc + i + 32) - Vector512.Load(wSub1 + i + 32) - Vector512.Load(wSub2 + i + 32) + Vector512.Load(wAdd + i + 32)).Store(dstAcc + i + 32);
            }
        }
        else if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                (Vector256.Load(srcAcc + i) - Vector256.Load(wSub1 + i) - Vector256.Load(wSub2 + i) + Vector256.Load(wAdd + i)).Store(dstAcc + i);
                (Vector256.Load(srcAcc + i + 16) - Vector256.Load(wSub1 + i + 16) - Vector256.Load(wSub2 + i + 16) + Vector256.Load(wAdd + i + 16)).Store(dstAcc + i + 16);
            }
        }
        else if (Vector128.IsHardwareAccelerated && dim >= 16)
        {
            for (; i <= dim - 16; i += 16)
            {
                (Vector128.Load(srcAcc + i) - Vector128.Load(wSub1 + i) - Vector128.Load(wSub2 + i) + Vector128.Load(wAdd + i)).Store(dstAcc + i);
                (Vector128.Load(srcAcc + i + 8) - Vector128.Load(wSub1 + i + 8) - Vector128.Load(wSub2 + i + 8) + Vector128.Load(wAdd + i + 8)).Store(dstAcc + i + 8);
            }
        }
        for (; i < dim; i++) dstAcc[i] = (short)(srcAcc[i] - wSub1[i] - wSub2[i] + wAdd[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubSubAddAddFeature16(int sub1Idx, int sub2Idx, int add1Idx, int add2Idx, short* weights, short* srcAcc, short* dstAcc, int dim)
    {
        short* wSub1 = weights + (sub1Idx * dim);
        short* wSub2 = weights + (sub2Idx * dim);
        short* wAdd1 = weights + (add1Idx * dim);
        short* wAdd2 = weights + (add2Idx * dim);
        int i = 0;
        if (Avx512BW.IsSupported && dim >= 64)
        {
            for (; i <= dim - 64; i += 64)
            {
                (Vector512.Load(srcAcc + i) - Vector512.Load(wSub1 + i) - Vector512.Load(wSub2 + i) + Vector512.Load(wAdd1 + i) + Vector512.Load(wAdd2 + i)).Store(dstAcc + i);
                (Vector512.Load(srcAcc + i + 32) - Vector512.Load(wSub1 + i + 32) - Vector512.Load(wSub2 + i + 32) + Vector512.Load(wAdd1 + i + 32) + Vector512.Load(wAdd2 + i + 32)).Store(dstAcc + i + 32);
            }
        }
        else if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                (Vector256.Load(srcAcc + i) - Vector256.Load(wSub1 + i) - Vector256.Load(wSub2 + i) + Vector256.Load(wAdd1 + i) + Vector256.Load(wAdd2 + i)).Store(dstAcc + i);
                (Vector256.Load(srcAcc + i + 16) - Vector256.Load(wSub1 + i + 16) - Vector256.Load(wSub2 + i + 16) + Vector256.Load(wAdd1 + i + 16) + Vector256.Load(wAdd2 + i + 16)).Store(dstAcc + i + 16);
            }
        }
        else if (Vector128.IsHardwareAccelerated && dim >= 16)
        {
            for (; i <= dim - 16; i += 16)
            {
                (Vector128.Load(srcAcc + i) - Vector128.Load(wSub1 + i) - Vector128.Load(wSub2 + i) + Vector128.Load(wAdd1 + i) + Vector128.Load(wAdd2 + i)).Store(dstAcc + i);
                (Vector128.Load(srcAcc + i + 8) - Vector128.Load(wSub1 + i + 8) - Vector128.Load(wSub2 + i + 8) + Vector128.Load(wAdd1 + i + 8) + Vector128.Load(wAdd2 + i + 8)).Store(dstAcc + i + 8);
            }
        }
        for (; i < dim; i++) dstAcc[i] = (short)(srcAcc[i] - wSub1[i] - wSub2[i] + wAdd1[i] + wAdd2[i]);
    }
}
