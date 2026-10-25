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
        int rank = sq >> 3;
        int foldedFile = file >= 4 ? 7 - file : file; // 0..3
        int rankGroup = rank >> 1;                     // 0..3 (ranks 0-1 -> 0, 2-3 -> 1, 4-5 -> 2, 6-7 -> 3)
        return (rankGroup << 2) | foldedFile;          // 0..15
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

    /// <summary>
    /// Incrementally updates White and Black accumulators for a move.
    /// Handles king-bucket refresh when a king crosses bucket boundaries.
    /// </summary>
    public static void UpdateMove(
        Move move,
        Piece movedPiece,
        Piece capturedPiece,
        NnueWeights weights,
        short* whiteAcc,
        short* blackAcc,
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
            SubFeature(wFrom, ftWeights, whiteAcc, dim);
            SubFeature(bFrom, ftWeights, blackAcc, dim);

            if (capturedPiece != Piece.None)
            {
                Square capSq = move.Type == MoveType.EnPassant
                    ? SquareExtensions.Create(to.File(), from.Rank())
                    : to;
                PieceType capType = capturedPiece.Type();
                int wCap = GetWhiteFeatureIndex(capType, them, capSq);
                int bCap = GetBlackFeatureIndex(capType, them, capSq);
                SubFeature(wCap, ftWeights, whiteAcc, dim);
                SubFeature(bCap, ftWeights, blackAcc, dim);
            }

            PieceType addedType = move.IsPromotion ? move.PromotionPieceType : movedType;
            int wTo = GetWhiteFeatureIndex(addedType, us, to);
            int bTo = GetBlackFeatureIndex(addedType, us, to);
            AddFeature(wTo, ftWeights, whiteAcc, dim);
            AddFeature(bTo, ftWeights, blackAcc, dim);

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

                SubFeature(wRFrom, ftWeights, whiteAcc, dim);
                SubFeature(bRFrom, ftWeights, blackAcc, dim);
                AddFeature(wRTo, ftWeights, whiteAcc, dim);
                AddFeature(bRTo, ftWeights, blackAcc, dim);
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

                // Black's king didn't move -> incremental update for Black
                int bFrom = GetBlackFeatureIndex(movedType, us, from);
                SubFeature(bFrom, bWeights, blackAcc, dim);

                if (capturedPiece != Piece.None)
                {
                    int bCap = GetBlackFeatureIndex(capturedPiece.Type(), them, to);
                    SubFeature(bCap, bWeights, blackAcc, dim);
                }

                int bTo = GetBlackFeatureIndex(movedType, us, to);
                AddFeature(bTo, bWeights, blackAcc, dim);

                if (move.IsCastling)
                {
                    Square rFrom = to > from ? Square.H1 : Square.A1;
                    Square rTo = to > from ? Square.F1 : Square.D1;
                    int bRFrom = GetBlackFeatureIndex(PieceType.Rook, us, rFrom);
                    int bRTo = GetBlackFeatureIndex(PieceType.Rook, us, rTo);
                    SubFeature(bRFrom, bWeights, blackAcc, dim);
                    AddFeature(bRTo, bWeights, blackAcc, dim);
                }

                // White king moved: check bucket change
                if (oldBucket != newBucket || move.IsCastling)
                {
                    RefreshWhite(in boardAfter, weights, whiteAcc);
                }
                else
                {
                    int wFrom = GetWhiteFeatureIndex(movedType, us, from);
                    SubFeature(wFrom, wWeights, whiteAcc, dim);
                    if (capturedPiece != Piece.None)
                    {
                        int wCap = GetWhiteFeatureIndex(capturedPiece.Type(), them, to);
                        SubFeature(wCap, wWeights, whiteAcc, dim);
                    }
                    int wTo = GetWhiteFeatureIndex(movedType, us, to);
                    AddFeature(wTo, wWeights, whiteAcc, dim);
                }
            }
            else // us == Color.Black
            {
                int oldBucket = GetKingBucket((Square)((int)from ^ 56));
                int newBucket = GetKingBucket((Square)((int)to ^ 56));

                // White's king didn't move -> incremental update for White
                int wFrom = GetWhiteFeatureIndex(movedType, us, from);
                SubFeature(wFrom, wWeights, whiteAcc, dim);

                if (capturedPiece != Piece.None)
                {
                    int wCap = GetWhiteFeatureIndex(capturedPiece.Type(), them, to);
                    SubFeature(wCap, wWeights, whiteAcc, dim);
                }

                int wTo = GetWhiteFeatureIndex(movedType, us, to);
                AddFeature(wTo, wWeights, whiteAcc, dim);

                if (move.IsCastling)
                {
                    Square rFrom = to > from ? Square.H8 : Square.A8;
                    Square rTo = to > from ? Square.F8 : Square.D8;
                    int wRFrom = GetWhiteFeatureIndex(PieceType.Rook, us, rFrom);
                    int wRTo = GetWhiteFeatureIndex(PieceType.Rook, us, rTo);
                    SubFeature(wRFrom, wWeights, whiteAcc, dim);
                    AddFeature(wRTo, wWeights, whiteAcc, dim);
                }

                // Black king moved: check bucket change
                if (oldBucket != newBucket || move.IsCastling)
                {
                    RefreshBlack(in boardAfter, weights, blackAcc);
                }
                else
                {
                    int bFrom = GetBlackFeatureIndex(movedType, us, from);
                    SubFeature(bFrom, bWeights, blackAcc, dim);
                    if (capturedPiece != Piece.None)
                    {
                        int bCap = GetBlackFeatureIndex(capturedPiece.Type(), them, to);
                        SubFeature(bCap, bWeights, blackAcc, dim);
                    }
                    int bTo = GetBlackFeatureIndex(movedType, us, to);
                    AddFeature(bTo, bWeights, blackAcc, dim);
                }
            }
        }
        else
        {
            // Non-king moves: pure fast SIMD incremental update for both perspectives
            int wFrom = GetWhiteFeatureIndex(movedType, us, from);
            int bFrom = GetBlackFeatureIndex(movedType, us, from);
            SubFeature(wFrom, wWeights, whiteAcc, dim);
            SubFeature(bFrom, bWeights, blackAcc, dim);

            if (capturedPiece != Piece.None)
            {
                Square capSq = move.Type == MoveType.EnPassant
                    ? SquareExtensions.Create(to.File(), from.Rank())
                    : to;
                PieceType capType = capturedPiece.Type();
                int wCap = GetWhiteFeatureIndex(capType, them, capSq);
                int bCap = GetBlackFeatureIndex(capType, them, capSq);
                SubFeature(wCap, wWeights, whiteAcc, dim);
                SubFeature(bCap, bWeights, blackAcc, dim);
            }

            PieceType addedType = move.IsPromotion ? move.PromotionPieceType : movedType;
            int wTo = GetWhiteFeatureIndex(addedType, us, to);
            int bTo = GetBlackFeatureIndex(addedType, us, to);
            AddFeature(wTo, wWeights, whiteAcc, dim);
            AddFeature(bTo, bWeights, blackAcc, dim);
        }
    }
}
