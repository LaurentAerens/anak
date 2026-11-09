using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;

namespace Anak.Data.Nnue;

/// <summary>
/// Dual-perspective King-Bucketed HalfKP accumulator management for NNUE inference.
/// Incremental updates during move execution and fast full refresh on king-bucket transitions.
/// </summary>
[SkipLocalsInit]
public static unsafe class NnueAccumulator
{
    /// <summary>
    /// Computes the 16-bucket king safety zone index with horizontal symmetry (files E-H mirrored to D-A).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetKingBucket(Square square)
    {
        int sq = (int)square;
        int file = sq & 7;
        return ((sq >> 2) & 12) | ((file ^ -(file >> 2)) & 3);
    }

    /// <summary>
    /// Computes White king bucket from board bitboard.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWhiteKingBucket(in Board board)
    {
        ulong kings = board.White & board.Kings;
        if (kings == 0) return 3; // StartPos e1
        Square sq = (Square)BitOperations.TrailingZeroCount(kings);
        return GetKingBucket(sq);
    }

    /// <summary>
    /// Computes Black king bucket from board bitboard (mirrored rank).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBlackKingBucket(in Board board)
    {
        ulong kings = board.Black & board.Kings;
        if (kings == 0) return 3; // StartPos e8 (mirrored to e1)
        Square sq = (Square)BitOperations.TrailingZeroCount(kings);
        return GetKingBucket((Square)((int)sq ^ 56));
    }

    /// <summary>
    /// Computes the HalfKP feature index from White's perspective: [0..767].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWhiteFeatureIndex(PieceType pieceType, Color color, Square square)
    {
        return ((((int)color * 6) + (int)pieceType - 1) << 6) | (int)square;
    }

    /// <summary>
    /// Computes the HalfKP feature index from Black's perspective: [0..767] (mirrored rank and opposite color).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBlackFeatureIndex(PieceType pieceType, Color color, Square square)
    {
        return ((((1 ^ (int)color) * 6) + (int)pieceType - 1) << 6) | ((int)square ^ 56);
    }

    /// <summary>
    /// Computes full accumulator from scratch for White and Black perspectives.
    /// Uses active king buckets for 16-bucket architecture.
    /// </summary>
    public static void ComputeFull(in Board board, NnueWeights weights, short* whiteAcc, short* blackAcc)
    {
        int dim = weights.HiddenDim;
        short* ftBiases = weights.FeatureBiases;

        // 1. Initialize with biases
        Buffer.MemoryCopy(ftBiases, whiteAcc, dim * sizeof(short), dim * sizeof(short));
        Buffer.MemoryCopy(ftBiases, blackAcc, dim * sizeof(short), dim * sizeof(short));

        if (weights.Buckets == 1)
        {
            sbyte* ftWeights = weights.FeatureWeights;
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
        else
        {
            int wBucket = GetWhiteKingBucket(in board);
            int bBucket = GetBlackKingBucket(in board);

            sbyte* wWeights = weights.FeatureWeights + (wBucket * NnueConstants.RawFeatureCount * dim);
            sbyte* bWeights = weights.FeatureWeights + (bBucket * NnueConstants.RawFeatureCount * dim);

            ulong occupied = board.Occupied;
            while (occupied != 0)
            {
                Square sq = (Square)BitOperations.TrailingZeroCount(occupied);
                Piece piece = board.GetPieceAt(sq);
                if (piece != Piece.None)
                {
                    int whiteIdx = GetWhiteFeatureIndex(piece.Type(), piece.Color(), sq);
                    int blackIdx = GetBlackFeatureIndex(piece.Type(), piece.Color(), sq);

                    AddFeature(whiteIdx, wWeights, whiteAcc, dim);
                    AddFeature(blackIdx, bWeights, blackAcc, dim);
                }
                occupied &= occupied - 1;
            }
        }
    }

    /// <summary>
    /// Fully refreshes the White accumulator from current board pieces and White king bucket.
    /// </summary>
    public static void RefreshWhite(in Board board, NnueWeights weights, short* whiteAcc)
    {
        int dim = weights.HiddenDim;
        Buffer.MemoryCopy(weights.FeatureBiases, whiteAcc, dim * sizeof(short), dim * sizeof(short));

        int wBucket = weights.Buckets == 1 ? 0 : GetWhiteKingBucket(in board);
        sbyte* wWeights = weights.FeatureWeights + (wBucket * NnueConstants.RawFeatureCount * dim);

        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(occupied);
            Piece piece = board.GetPieceAt(sq);
            if (piece != Piece.None)
            {
                int whiteIdx = GetWhiteFeatureIndex(piece.Type(), piece.Color(), sq);
                AddFeature(whiteIdx, wWeights, whiteAcc, dim);
            }
            occupied &= occupied - 1;
        }
    }

    /// <summary>
    /// Fully refreshes the Black accumulator from current board pieces and Black king bucket.
    /// </summary>
    public static void RefreshBlack(in Board board, NnueWeights weights, short* blackAcc)
    {
        int dim = weights.HiddenDim;
        Buffer.MemoryCopy(weights.FeatureBiases, blackAcc, dim * sizeof(short), dim * sizeof(short));

        int bBucket = weights.Buckets == 1 ? 0 : GetBlackKingBucket(in board);
        sbyte* bWeights = weights.FeatureWeights + (bBucket * NnueConstants.RawFeatureCount * dim);

        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            Square sq = (Square)BitOperations.TrailingZeroCount(occupied);
            Piece piece = board.GetPieceAt(sq);
            if (piece != Piece.None)
            {
                int blackIdx = GetBlackFeatureIndex(piece.Type(), piece.Color(), sq);
                AddFeature(blackIdx, bWeights, blackAcc, dim);
            }
            occupied &= occupied - 1;
        }
    }

    /// <summary>
    /// Adds a feature to an accumulator: acc[i] += weights[featureIdx * dim + i].
    /// Vectorized with AVX2 (32 shorts/iter) and Vector128 SIMD instructions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddFeature(int featureIdx, sbyte* weights, short* acc, int dim)
    {
        sbyte* wPtr = weights + (featureIdx * dim);

        int i = 0;
        if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                Vector256<short> w0 = Avx2.ConvertToVector256Int16(Vector128.Load(wPtr + i));
                Vector256<short> w1 = Avx2.ConvertToVector256Int16(Vector128.Load(wPtr + i + 16));

                (Vector256.Load(acc + i) + w0).Store(acc + i);
                (Vector256.Load(acc + i + 16) + w1).Store(acc + i + 16);
            }
        }
        else if (Vector128.IsHardwareAccelerated && dim >= 16)
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
        if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                Vector256<short> w0 = Avx2.ConvertToVector256Int16(Vector128.Load(wPtr + i));
                Vector256<short> w1 = Avx2.ConvertToVector256Int16(Vector128.Load(wPtr + i + 16));

                (Vector256.Load(acc + i) - w0).Store(acc + i);
                (Vector256.Load(acc + i + 16) - w1).Store(acc + i + 16);
            }
        }
        else if (Vector128.IsHardwareAccelerated && dim >= 16)
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

    /// <summary>
    /// Fused single-pass accumulator update for quiet moves: dst[i] = src[i] - weights[subIdx] + weights[addIdx].
    /// Avoids a separate memory copy and reduces memory round-trips from 3 passes to 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubAddFeature(int subIdx, int addIdx, sbyte* weights, short* srcAcc, short* dstAcc, int dim)
    {
        sbyte* wSub = weights + (subIdx * dim);
        sbyte* wAdd = weights + (addIdx * dim);

        int i = 0;
        if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                Vector256<short> s0 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub + i));
                Vector256<short> s1 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub + i + 16));
                Vector256<short> a0 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd + i));
                Vector256<short> a1 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd + i + 16));

                (Vector256.Load(srcAcc + i) - s0 + a0).Store(dstAcc + i);
                (Vector256.Load(srcAcc + i + 16) - s1 + a1).Store(dstAcc + i + 16);
            }
        }

        for (; i < dim; i++)
        {
            dstAcc[i] = (short)(srcAcc[i] - wSub[i] + wAdd[i]);
        }
    }

    /// <summary>
    /// Fused single-pass accumulator update for capture moves: dst[i] = src[i] - weights[sub1Idx] - weights[sub2Idx] + weights[addIdx].
    /// Avoids a separate memory copy and reduces memory round-trips from 4 passes to 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubSubAddFeature(int sub1Idx, int sub2Idx, int addIdx, sbyte* weights, short* srcAcc, short* dstAcc, int dim)
    {
        sbyte* wSub1 = weights + (sub1Idx * dim);
        sbyte* wSub2 = weights + (sub2Idx * dim);
        sbyte* wAdd = weights + (addIdx * dim);

        int i = 0;
        if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                Vector256<short> s10 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub1 + i));
                Vector256<short> s11 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub1 + i + 16));
                Vector256<short> s20 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub2 + i));
                Vector256<short> s21 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub2 + i + 16));
                Vector256<short> a0 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd + i));
                Vector256<short> a1 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd + i + 16));

                (Vector256.Load(srcAcc + i) - s10 - s20 + a0).Store(dstAcc + i);
                (Vector256.Load(srcAcc + i + 16) - s11 - s21 + a1).Store(dstAcc + i + 16);
            }
        }

        for (; i < dim; i++)
        {
            dstAcc[i] = (short)(srcAcc[i] - wSub1[i] - wSub2[i] + wAdd[i]);
        }
    }

    /// <summary>
    /// Fused single-pass accumulator update for castling moves: dst[i] = src[i] - weights[subKing] - weights[subRook] + weights[addKing] + weights[addRook].
    /// Updates both King and Rook features simultaneously in a single SIMD pass from src to dst.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubSubAddAddFeature(int sub1Idx, int sub2Idx, int add1Idx, int add2Idx, sbyte* weights, short* srcAcc, short* dstAcc, int dim)
    {
        sbyte* wSub1 = weights + (sub1Idx * dim);
        sbyte* wSub2 = weights + (sub2Idx * dim);
        sbyte* wAdd1 = weights + (add1Idx * dim);
        sbyte* wAdd2 = weights + (add2Idx * dim);

        int i = 0;
        if (Avx2.IsSupported && dim >= 32)
        {
            for (; i <= dim - 32; i += 32)
            {
                Vector256<short> s10 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub1 + i));
                Vector256<short> s11 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub1 + i + 16));
                Vector256<short> s20 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub2 + i));
                Vector256<short> s21 = Avx2.ConvertToVector256Int16(Vector128.Load(wSub2 + i + 16));

                Vector256<short> a10 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd1 + i));
                Vector256<short> a11 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd1 + i + 16));
                Vector256<short> a20 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd2 + i));
                Vector256<short> a21 = Avx2.ConvertToVector256Int16(Vector128.Load(wAdd2 + i + 16));

                (Vector256.Load(srcAcc + i) - s10 - s20 + a10 + a20).Store(dstAcc + i);
                (Vector256.Load(srcAcc + i + 16) - s11 - s21 + a11 + a21).Store(dstAcc + i + 16);
            }
        }

        for (; i < dim; i++)
        {
            dstAcc[i] = (short)(srcAcc[i] - wSub1[i] - wSub2[i] + wAdd1[i] + wAdd2[i]);
        }
    }

    /// <summary>
    /// Incrementally updates White and Black accumulators in-place for a move.
    /// Handles king-bucket refresh when a king crosses bucket boundaries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateMove(
        Move move,
        Piece movedPiece,
        Piece capturedPiece,
        NnueWeights weights,
        short* whiteAcc,
        short* blackAcc,
        in Board boardAfter)
    {
        UpdateMove(move, movedPiece, capturedPiece, weights, whiteAcc, blackAcc, whiteAcc, blackAcc, in boardAfter);
    }

    /// <summary>
    /// Incrementally updates White and Black accumulators from <paramref name="srcWhiteAcc"/>/<paramref name="srcBlackAcc"/>
    /// into <paramref name="dstWhiteAcc"/>/<paramref name="dstBlackAcc"/> in a single fused pass.
    /// Handles king-bucket refresh when a king crosses bucket boundaries.
    /// </summary>
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
        in Board boardAfter)
    {
        int dim = weights.HiddenDim;
        Color us = movedPiece.Color();
        Color them = us.Opponent();
        PieceType movedType = movedPiece.Type();
        Square from = move.From;
        Square to = move.To;

        if (weights.Buckets == 1)
        {
            sbyte* ftWeights = weights.FeatureWeights;
            int wFrom = GetWhiteFeatureIndex(movedType, us, from);
            int bFrom = GetBlackFeatureIndex(movedType, us, from);
            PieceType addedType = move.IsPromotion ? move.PromotionPieceType : movedType;
            int wTo = GetWhiteFeatureIndex(addedType, us, to);
            int bTo = GetBlackFeatureIndex(addedType, us, to);

            if (capturedPiece != Piece.None)
            {
                Square capSq = move.Type == MoveType.EnPassant
                    ? SquareExtensions.Create(to.File(), from.Rank())
                    : to;
                PieceType capType = capturedPiece.Type();
                int wCap = GetWhiteFeatureIndex(capType, them, capSq);
                int bCap = GetBlackFeatureIndex(capType, them, capSq);
                SubSubAddFeature(wFrom, wCap, wTo, ftWeights, srcWhiteAcc, dstWhiteAcc, dim);
                SubSubAddFeature(bFrom, bCap, bTo, ftWeights, srcBlackAcc, dstBlackAcc, dim);
            }
            else
            {
                SubAddFeature(wFrom, wTo, ftWeights, srcWhiteAcc, dstWhiteAcc, dim);
                SubAddFeature(bFrom, bTo, ftWeights, srcBlackAcc, dstBlackAcc, dim);
            }

            if (move.IsCastling)
            {
                Square rFrom, rTo;
                if (to > from)
                {
                    rFrom = us == Color.White ? Square.H1 : Square.H8;
                    rTo = us == Color.White ? Square.F1 : Square.F8;
                }
                else
                {
                    rFrom = us == Color.White ? Square.A1 : Square.A8;
                    rTo = us == Color.White ? Square.D1 : Square.D8;
                }

                int wRFrom = GetWhiteFeatureIndex(PieceType.Rook, us, rFrom);
                int bRFrom = GetBlackFeatureIndex(PieceType.Rook, us, rFrom);
                int wRTo = GetWhiteFeatureIndex(PieceType.Rook, us, rTo);
                int bRTo = GetBlackFeatureIndex(PieceType.Rook, us, rTo);

                SubAddFeature(wRFrom, wRTo, ftWeights, dstWhiteAcc, dstWhiteAcc, dim);
                SubAddFeature(bRFrom, bRTo, ftWeights, dstBlackAcc, dstBlackAcc, dim);
            }
            return;
        }

        // --- 16-Bucket King Architecture ---
        int wBucket = GetWhiteKingBucket(in boardAfter);
        int bBucket = GetBlackKingBucket(in boardAfter);
        sbyte* wWeights = weights.FeatureWeights + (wBucket * NnueConstants.RawFeatureCount * dim);
        sbyte* bWeights = weights.FeatureWeights + (bBucket * NnueConstants.RawFeatureCount * dim);

        if (movedType == PieceType.King)
        {
            if (us == Color.White)
            {
                int oldBucket = GetKingBucket(from);
                int newBucket = GetKingBucket(to);

                // Black's king didn't move -> single-pass incremental update for Black
                int bFrom = GetBlackFeatureIndex(movedType, us, from);
                int bTo = GetBlackFeatureIndex(movedType, us, to);

                if (move.IsCastling)
                {
                    Square rFrom = to > from ? Square.H1 : Square.A1;
                    Square rTo = to > from ? Square.F1 : Square.D1;
                    int bRFrom = GetBlackFeatureIndex(PieceType.Rook, us, rFrom);
                    int bRTo = GetBlackFeatureIndex(PieceType.Rook, us, rTo);
                    SubSubAddAddFeature(bFrom, bRFrom, bTo, bRTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                }
                else if (capturedPiece != Piece.None)
                {
                    int bCap = GetBlackFeatureIndex(capturedPiece.Type(), them, to);
                    SubSubAddFeature(bFrom, bCap, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                }
                else
                {
                    SubAddFeature(bFrom, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                }

                // White king moved: check bucket change
                if (oldBucket != newBucket || move.IsCastling)
                {
                    RefreshWhite(in boardAfter, weights, dstWhiteAcc);
                }
                else
                {
                    int wFrom = GetWhiteFeatureIndex(movedType, us, from);
                    int wTo = GetWhiteFeatureIndex(movedType, us, to);
                    if (capturedPiece != Piece.None)
                    {
                        int wCap = GetWhiteFeatureIndex(capturedPiece.Type(), them, to);
                        SubSubAddFeature(wFrom, wCap, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                    }
                    else
                    {
                        SubAddFeature(wFrom, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                    }
                }
            }
            else // us == Color.Black
            {
                int oldBucket = GetKingBucket((Square)((int)from ^ 56));
                int newBucket = GetKingBucket((Square)((int)to ^ 56));

                // White's king didn't move -> single-pass incremental update for White
                int wFrom = GetWhiteFeatureIndex(movedType, us, from);
                int wTo = GetWhiteFeatureIndex(movedType, us, to);

                if (move.IsCastling)
                {
                    Square rFrom = to > from ? Square.H8 : Square.A8;
                    Square rTo = to > from ? Square.F8 : Square.D8;
                    int wRFrom = GetWhiteFeatureIndex(PieceType.Rook, us, rFrom);
                    int wRTo = GetWhiteFeatureIndex(PieceType.Rook, us, rTo);
                    SubSubAddAddFeature(wFrom, wRFrom, wTo, wRTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                }
                else if (capturedPiece != Piece.None)
                {
                    int wCap = GetWhiteFeatureIndex(capturedPiece.Type(), them, to);
                    SubSubAddFeature(wFrom, wCap, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                }
                else
                {
                    SubAddFeature(wFrom, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                }

                // Black king moved: check bucket change
                if (oldBucket != newBucket || move.IsCastling)
                {
                    RefreshBlack(in boardAfter, weights, dstBlackAcc);
                }
                else
                {
                    int bFrom = GetBlackFeatureIndex(movedType, us, from);
                    int bTo = GetBlackFeatureIndex(movedType, us, to);
                    if (capturedPiece != Piece.None)
                    {
                        int bCap = GetBlackFeatureIndex(capturedPiece.Type(), them, to);
                        SubSubAddFeature(bFrom, bCap, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                    }
                    else
                    {
                        SubAddFeature(bFrom, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
                    }
                }
            }
        }
        else
        {
            // Non-king moves: single-pass fused SIMD incremental update for both perspectives
            int wFrom = GetWhiteFeatureIndex(movedType, us, from);
            int bFrom = GetBlackFeatureIndex(movedType, us, from);
            PieceType addedType = move.IsPromotion ? move.PromotionPieceType : movedType;
            int wTo = GetWhiteFeatureIndex(addedType, us, to);
            int bTo = GetBlackFeatureIndex(addedType, us, to);

            if (capturedPiece != Piece.None)
            {
                Square capSq = move.Type == MoveType.EnPassant
                    ? SquareExtensions.Create(to.File(), from.Rank())
                    : to;
                PieceType capType = capturedPiece.Type();
                int wCap = GetWhiteFeatureIndex(capType, them, capSq);
                int bCap = GetBlackFeatureIndex(capType, them, capSq);
                SubSubAddFeature(wFrom, wCap, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                SubSubAddFeature(bFrom, bCap, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
            }
            else
            {
                SubAddFeature(wFrom, wTo, wWeights, srcWhiteAcc, dstWhiteAcc, dim);
                SubAddFeature(bFrom, bTo, bWeights, srcBlackAcc, dstBlackAcc, dim);
            }
        }
    }
}
