using System.Numerics;
using System.Runtime.CompilerServices;

namespace Anak.Core;

[SkipLocalsInit]
public static class MoveGenerator
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void GenerateLegalMoves(in Board board, ref MoveList list)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();

        ulong ourPieces = board.GetPieces(us);
        ulong enemyPieces = board.GetPieces(them);
        ulong occupied = board.Occupied;

        ulong kingBb = board.Kings & ourPieces;
        if (kingBb == 0) return;
        Square kingSq = Bitboard.Lsb(kingBb);

        // 1. Identify Checkers
        ulong checkers = 0UL;

        // Pawn checkers
        ulong pawnAttackers = us == Color.White
            ? Bitboard.WhitePawnAttacks[(int)kingSq]
            : Bitboard.BlackPawnAttacks[(int)kingSq];
        checkers |= pawnAttackers & board.Pawns & enemyPieces;

        // Knight checkers
        checkers |= Bitboard.KnightAttacks[(int)kingSq] & board.Knights & enemyPieces;

        // Bishop / Queen checkers
        ulong bAttacks = MagicBitboards.GetBishopAttacks(kingSq, occupied);
        checkers |= bAttacks & (board.Bishops | board.Queens) & enemyPieces;

        // Rook / Queen checkers
        ulong rAttacks = MagicBitboards.GetRookAttacks(kingSq, occupied);
        checkers |= rAttacks & (board.Rooks | board.Queens) & enemyPieces;

        // 2. King Moves (always generated, in check or not)
        ulong kingTargets = Bitboard.KingAttacks[(int)kingSq] & ~ourPieces;
        ulong occWithoutKing = occupied ^ (1UL << (int)kingSq);

        while (kingTargets != 0)
        {
            Square to = Bitboard.PopLsb(ref kingTargets);
            if (!IsSquareAttackedCustom(board, to, them, occWithoutKing))
            {
                bool isCapture = (enemyPieces & (1UL << (int)to)) != 0;
                list.Add(new Move(kingSq, to, isCapture ? MoveType.Capture : MoveType.Quiet));
            }
        }

        if ((checkers & (checkers - 1)) != 0)
        {
            // In double check (2 or more bits set), only king moves can escape check
            return;
        }

        int numCheckers = checkers != 0 ? 1 : 0;

        // 3. Setup Target Mask for remaining pieces
        ulong targetMask;
        if (numCheckers == 1)
        {
            Square checkerSq = Bitboard.Lsb(checkers);
            ulong captureMask = 1UL << (int)checkerSq;
            ulong pushMask = 0UL;
            Piece checkerPiece = board.GetPieceAt(checkerSq);
            PieceType checkerType = checkerPiece.Type();
            if (checkerType is PieceType.Bishop or PieceType.Rook or PieceType.Queen)
            {
                pushMask = Bitboard.Between[(int)kingSq, (int)checkerSq];
            }
            targetMask = captureMask | pushMask;
        }
        else
        {
            targetMask = ~ourPieces;
        }

        // 4. Calculate Pinned Pieces and Pin Rays
        ulong pinnedPieces = 0UL;
        Span<ulong> pinRays = stackalloc ulong[64];

        ulong pinners =
            ((MagicBitboards.GetBishopAttacks(kingSq, enemyPieces) & (board.Bishops | board.Queens)) |
             (MagicBitboards.GetRookAttacks(kingSq, enemyPieces) & (board.Rooks | board.Queens))) & enemyPieces;

        while (pinners != 0)
        {
            Square pinnerSq = Bitboard.PopLsb(ref pinners);
            ulong blockers = Bitboard.Between[(int)kingSq, (int)pinnerSq] & occupied;
            if (blockers != 0 && (blockers & (blockers - 1)) == 0 && (blockers & ourPieces) != 0)
            {
                Square pinnedSq = Bitboard.Lsb(blockers);
                pinnedPieces |= blockers;
                pinRays[(int)pinnedSq] = Bitboard.Line[(int)kingSq, (int)pinnerSq];
            }
        }

        // 5. Castling (only legal when not in check)
        if (numCheckers == 0)
        {
            if (us == Color.White)
            {
                if ((board.Castling & CastlingRights.WhiteKingside) != 0)
                {
                    const ulong emptyMask = (1UL << (int)Square.F1) | (1UL << (int)Square.G1);
                    if ((occupied & emptyMask) == 0 &&
                        !board.IsSquareAttacked(Square.F1, Color.Black) &&
                        !board.IsSquareAttacked(Square.G1, Color.Black))
                    {
                        list.Add(new Move(Square.E1, Square.G1, MoveType.KingCastle));
                    }
                }
                if ((board.Castling & CastlingRights.WhiteQueenside) != 0)
                {
                    const ulong emptyMask = (1UL << (int)Square.B1) | (1UL << (int)Square.C1) | (1UL << (int)Square.D1);
                    if ((occupied & emptyMask) == 0 &&
                        !board.IsSquareAttacked(Square.D1, Color.Black) &&
                        !board.IsSquareAttacked(Square.C1, Color.Black))
                    {
                        list.Add(new Move(Square.E1, Square.C1, MoveType.QueenCastle));
                    }
                }
            }
            else
            {
                if ((board.Castling & CastlingRights.BlackKingside) != 0)
                {
                    const ulong emptyMask = (1UL << (int)Square.F8) | (1UL << (int)Square.G8);
                    if ((occupied & emptyMask) == 0 &&
                        !board.IsSquareAttacked(Square.F8, Color.White) &&
                        !board.IsSquareAttacked(Square.G8, Color.White))
                    {
                        list.Add(new Move(Square.E8, Square.G8, MoveType.KingCastle));
                    }
                }
                if ((board.Castling & CastlingRights.BlackQueenside) != 0)
                {
                    const ulong emptyMask = (1UL << (int)Square.B8) | (1UL << (int)Square.C8) | (1UL << (int)Square.D8);
                    if ((occupied & emptyMask) == 0 &&
                        !board.IsSquareAttacked(Square.D8, Color.White) &&
                        !board.IsSquareAttacked(Square.C8, Color.White))
                    {
                        list.Add(new Move(Square.E8, Square.C8, MoveType.QueenCastle));
                    }
                }
            }
        }

        // 6. Knight Moves (pinned knights can never move)
        ulong knights = board.Knights & ourPieces & ~pinnedPieces;
        while (knights != 0)
        {
            Square from = Bitboard.PopLsb(ref knights);
            ulong moves = Bitboard.KnightAttacks[(int)from] & targetMask;
            while (moves != 0)
            {
                Square to = Bitboard.PopLsb(ref moves);
                bool isCap = (enemyPieces & (1UL << (int)to)) != 0;
                list.Add(new Move(from, to, isCap ? MoveType.Capture : MoveType.Quiet));
            }
        }

        // 7. Bishop & Queen Diagonal Moves
        ulong diagSliders = (board.Bishops | board.Queens) & ourPieces;
        while (diagSliders != 0)
        {
            Square from = Bitboard.PopLsb(ref diagSliders);
            ulong moves = MagicBitboards.GetBishopAttacks(from, occupied) & targetMask;
            if ((pinnedPieces & (1UL << (int)from)) != 0)
            {
                moves &= pinRays[(int)from];
            }
            while (moves != 0)
            {
                Square to = Bitboard.PopLsb(ref moves);
                bool isCap = (enemyPieces & (1UL << (int)to)) != 0;
                list.Add(new Move(from, to, isCap ? MoveType.Capture : MoveType.Quiet));
            }
        }

        // 8. Rook & Queen Orthogonal Moves
        ulong orthoSliders = (board.Rooks | board.Queens) & ourPieces;
        while (orthoSliders != 0)
        {
            Square from = Bitboard.PopLsb(ref orthoSliders);
            ulong moves = MagicBitboards.GetRookAttacks(from, occupied) & targetMask;
            if ((pinnedPieces & (1UL << (int)from)) != 0)
            {
                moves &= pinRays[(int)from];
            }
            while (moves != 0)
            {
                Square to = Bitboard.PopLsb(ref moves);
                bool isCap = (enemyPieces & (1UL << (int)to)) != 0;
                list.Add(new Move(from, to, isCap ? MoveType.Capture : MoveType.Quiet));
            }
        }

        // 9. Pawn Moves
        if (us == Color.White)
        {
            ulong pawns = board.Pawns & ourPieces;
            while (pawns != 0)
            {
                Square from = Bitboard.PopLsb(ref pawns);
                bool isPinned = (pinnedPieces & (1UL << (int)from)) != 0;
                ulong allowedMask = isPinned ? (targetMask & pinRays[(int)from]) : targetMask;

                int r = (int)from.Rank();
                int f = (int)from.File();

                // Single push
                Square to1 = (Square)((int)from + 8);
                if ((occupied & (1UL << (int)to1)) == 0)
                {
                    if (r == 6) // Promotion
                    {
                        if (((1UL << (int)to1) & allowedMask) != 0)
                        {
                            list.Add(new Move(from, to1, MoveType.QueenPromotion));
                            list.Add(new Move(from, to1, MoveType.RookPromotion));
                            list.Add(new Move(from, to1, MoveType.BishopPromotion));
                            list.Add(new Move(from, to1, MoveType.KnightPromotion));
                        }
                    }
                    else
                    {
                        if (((1UL << (int)to1) & allowedMask) != 0)
                        {
                            list.Add(new Move(from, to1, MoveType.Quiet));
                        }

                        // Double push (from rank 2)
                        if (r == 1)
                        {
                            Square to2 = (Square)((int)from + 16);
                            if ((occupied & (1UL << (int)to2)) == 0 &&
                                (((1UL << (int)to2) & allowedMask) != 0))
                            {
                                list.Add(new Move(from, to2, MoveType.DoublePawnPush));
                            }
                        }
                    }
                }

                // Captures
                // West capture: file > 0 (from + 7)
                if (f > 0)
                {
                    Square capWest = (Square)((int)from + 7);
                    if (((1UL << (int)capWest) & enemyPieces & allowedMask) != 0)
                    {
                        if (r == 6) // Promotion Capture
                        {
                            list.Add(new Move(from, capWest, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capWest, MoveType.Capture));
                        }
                    }
                }

                // East capture: file < 7 (from + 9)
                if (f < 7)
                {
                    Square capEast = (Square)((int)from + 9);
                    if (((1UL << (int)capEast) & enemyPieces & allowedMask) != 0)
                    {
                        if (r == 6) // Promotion Capture
                        {
                            list.Add(new Move(from, capEast, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capEast, MoveType.Capture));
                        }
                    }
                }
            }

            // En Passant
            if (board.EpSquare != Square.None)
            {
                Square epTo = board.EpSquare;
                int epFile = (int)epTo.File();
                Square epCapSq = (Square)((int)epTo - 8);

                if (epFile > 0)
                {
                    Square from = (Square)((int)epCapSq - 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, numCheckers, checkers, enemyPieces, occupied, ref list);
                    }
                }
                if (epFile < 7)
                {
                    Square from = (Square)((int)epCapSq + 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, numCheckers, checkers, enemyPieces, occupied, ref list);
                    }
                }
            }
        }
        else // Black Pawns
        {
            ulong pawns = board.Pawns & ourPieces;
            while (pawns != 0)
            {
                Square from = Bitboard.PopLsb(ref pawns);
                bool isPinned = (pinnedPieces & (1UL << (int)from)) != 0;
                ulong allowedMask = isPinned ? (targetMask & pinRays[(int)from]) : targetMask;

                int r = (int)from.Rank();
                int f = (int)from.File();

                // Single push
                Square to1 = (Square)((int)from - 8);
                if ((occupied & (1UL << (int)to1)) == 0)
                {
                    if (r == 1) // Promotion
                    {
                        if (((1UL << (int)to1) & allowedMask) != 0)
                        {
                            list.Add(new Move(from, to1, MoveType.QueenPromotion));
                            list.Add(new Move(from, to1, MoveType.RookPromotion));
                            list.Add(new Move(from, to1, MoveType.BishopPromotion));
                            list.Add(new Move(from, to1, MoveType.KnightPromotion));
                        }
                    }
                    else
                    {
                        if (((1UL << (int)to1) & allowedMask) != 0)
                        {
                            list.Add(new Move(from, to1, MoveType.Quiet));
                        }

                        // Double push (from rank 7)
                        if (r == 6)
                        {
                            Square to2 = (Square)((int)from - 16);
                            if ((occupied & (1UL << (int)to2)) == 0 &&
                                (((1UL << (int)to2) & allowedMask) != 0))
                            {
                                list.Add(new Move(from, to2, MoveType.DoublePawnPush));
                            }
                        }
                    }
                }

                // Captures
                // West capture: file > 0 (from - 9)
                if (f > 0)
                {
                    Square capWest = (Square)((int)from - 9);
                    if (((1UL << (int)capWest) & enemyPieces & allowedMask) != 0)
                    {
                        if (r == 1) // Promotion Capture
                        {
                            list.Add(new Move(from, capWest, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capWest, MoveType.Capture));
                        }
                    }
                }

                // East capture: file < 7 (from - 7)
                if (f < 7)
                {
                    Square capEast = (Square)((int)from - 7);
                    if (((1UL << (int)capEast) & enemyPieces & allowedMask) != 0)
                    {
                        if (r == 1) // Promotion Capture
                        {
                            list.Add(new Move(from, capEast, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capEast, MoveType.Capture));
                        }
                    }
                }
            }

            // En Passant
            if (board.EpSquare != Square.None)
            {
                Square epTo = board.EpSquare;
                int epFile = (int)epTo.File();
                Square epCapSq = (Square)((int)epTo + 8);

                if (epFile > 0)
                {
                    Square from = (Square)((int)epCapSq - 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, numCheckers, checkers, enemyPieces, occupied, ref list);
                    }
                }
                if (epFile < 7)
                {
                    Square from = (Square)((int)epCapSq + 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, numCheckers, checkers, enemyPieces, occupied, ref list);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void GenerateLegalCaptures(in Board board, ref MoveList list)
    {
        Color us = board.SideToMove;
        Color them = us.Opponent();

        ulong ourPieces = board.GetPieces(us);
        ulong enemyPieces = board.GetPieces(them);
        ulong occupied = board.Occupied;

        ulong kingBb = board.Kings & ourPieces;
        if (kingBb == 0) return;
        Square kingSq = Bitboard.Lsb(kingBb);

        // 1. King Captures
        ulong kingTargets = Bitboard.KingAttacks[(int)kingSq] & enemyPieces;
        ulong occWithoutKing = occupied ^ (1UL << (int)kingSq);

        while (kingTargets != 0)
        {
            Square to = Bitboard.PopLsb(ref kingTargets);
            if (!IsSquareAttackedCustom(board, to, them, occWithoutKing))
            {
                list.Add(new Move(kingSq, to, MoveType.Capture));
            }
        }

        // 2. Target Mask for captures is enemy pieces
        ulong targetMask = enemyPieces;

        // 3. Calculate Pinned Pieces and Pin Rays
        ulong pinnedPieces = 0UL;
        Span<ulong> pinRays = stackalloc ulong[64];

        ulong pinners =
            ((MagicBitboards.GetBishopAttacks(kingSq, enemyPieces) & (board.Bishops | board.Queens)) |
             (MagicBitboards.GetRookAttacks(kingSq, enemyPieces) & (board.Rooks | board.Queens))) & enemyPieces;

        while (pinners != 0)
        {
            Square pinnerSq = Bitboard.PopLsb(ref pinners);
            ulong blockers = Bitboard.Between[(int)kingSq, (int)pinnerSq] & occupied;
            if (blockers != 0 && (blockers & (blockers - 1)) == 0 && (blockers & ourPieces) != 0)
            {
                Square pinnedSq = Bitboard.Lsb(blockers);
                pinnedPieces |= blockers;
                pinRays[(int)pinnedSq] = Bitboard.Line[(int)kingSq, (int)pinnerSq];
            }
        }

        // 4. Knight Captures (pinned knights cannot capture)
        ulong knights = board.Knights & ourPieces & ~pinnedPieces;
        while (knights != 0)
        {
            Square from = Bitboard.PopLsb(ref knights);
            ulong moves = Bitboard.KnightAttacks[(int)from] & targetMask;
            while (moves != 0)
            {
                Square to = Bitboard.PopLsb(ref moves);
                list.Add(new Move(from, to, MoveType.Capture));
            }
        }

        // 5. Bishop & Queen Captures
        ulong diagSliders = (board.Bishops | board.Queens) & ourPieces;
        while (diagSliders != 0)
        {
            Square from = Bitboard.PopLsb(ref diagSliders);
            ulong moves = MagicBitboards.GetBishopAttacks(from, occupied) & targetMask;
            if ((pinnedPieces & (1UL << (int)from)) != 0)
            {
                moves &= pinRays[(int)from];
            }
            while (moves != 0)
            {
                Square to = Bitboard.PopLsb(ref moves);
                list.Add(new Move(from, to, MoveType.Capture));
            }
        }

        // 6. Rook & Queen Captures
        ulong orthoSliders = (board.Rooks | board.Queens) & ourPieces;
        while (orthoSliders != 0)
        {
            Square from = Bitboard.PopLsb(ref orthoSliders);
            ulong moves = MagicBitboards.GetRookAttacks(from, occupied) & targetMask;
            if ((pinnedPieces & (1UL << (int)from)) != 0)
            {
                moves &= pinRays[(int)from];
            }
            while (moves != 0)
            {
                Square to = Bitboard.PopLsb(ref moves);
                list.Add(new Move(from, to, MoveType.Capture));
            }
        }

        // 7. Pawn Captures & Promotions
        if (us == Color.White)
        {
            ulong pawns = board.Pawns & ourPieces;
            while (pawns != 0)
            {
                Square from = Bitboard.PopLsb(ref pawns);
                bool isPinned = (pinnedPieces & (1UL << (int)from)) != 0;
                int r = (int)from.Rank();
                int f = (int)from.File();

                // Promotions on single push (rank 7 -> 8)
                if (r == 6)
                {
                    Square to1 = (Square)((int)from + 8);
                    if ((occupied & (1UL << (int)to1)) == 0)
                    {
                        ulong allowedMask = isPinned ? pinRays[(int)from] : ~0UL;
                        if (((1UL << (int)to1) & allowedMask) != 0)
                        {
                            list.Add(new Move(from, to1, MoveType.QueenPromotion));
                            list.Add(new Move(from, to1, MoveType.RookPromotion));
                            list.Add(new Move(from, to1, MoveType.BishopPromotion));
                            list.Add(new Move(from, to1, MoveType.KnightPromotion));
                        }
                    }
                }

                ulong allowedCapMask = isPinned ? (targetMask & pinRays[(int)from]) : targetMask;

                // Captures
                // West capture: file > 0 (from + 7)
                if (f > 0)
                {
                    Square capWest = (Square)((int)from + 7);
                    if (((1UL << (int)capWest) & enemyPieces & allowedCapMask) != 0)
                    {
                        if (r == 6) // Promotion Capture
                        {
                            list.Add(new Move(from, capWest, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capWest, MoveType.Capture));
                        }
                    }
                }

                // East capture: file < 7 (from + 9)
                if (f < 7)
                {
                    Square capEast = (Square)((int)from + 9);
                    if (((1UL << (int)capEast) & enemyPieces & allowedCapMask) != 0)
                    {
                        if (r == 6) // Promotion Capture
                        {
                            list.Add(new Move(from, capEast, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capEast, MoveType.Capture));
                        }
                    }
                }
            }

            // En Passant
            if (board.EpSquare != Square.None)
            {
                Square epTo = board.EpSquare;
                int epFile = (int)epTo.File();
                Square epCapSq = (Square)((int)epTo - 8);

                if (epFile > 0)
                {
                    Square from = (Square)((int)epCapSq - 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, 0, 0UL, enemyPieces, occupied, ref list);
                    }
                }
                if (epFile < 7)
                {
                    Square from = (Square)((int)epCapSq + 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, 0, 0UL, enemyPieces, occupied, ref list);
                    }
                }
            }
        }
        else // Black Pawns
        {
            ulong pawns = board.Pawns & ourPieces;
            while (pawns != 0)
            {
                Square from = Bitboard.PopLsb(ref pawns);
                bool isPinned = (pinnedPieces & (1UL << (int)from)) != 0;
                int r = (int)from.Rank();
                int f = (int)from.File();

                // Promotions on single push (rank 2 -> 1)
                if (r == 1)
                {
                    Square to1 = (Square)((int)from - 8);
                    if ((occupied & (1UL << (int)to1)) == 0)
                    {
                        ulong allowedMask = isPinned ? pinRays[(int)from] : ~0UL;
                        if (((1UL << (int)to1) & allowedMask) != 0)
                        {
                            list.Add(new Move(from, to1, MoveType.QueenPromotion));
                            list.Add(new Move(from, to1, MoveType.RookPromotion));
                            list.Add(new Move(from, to1, MoveType.BishopPromotion));
                            list.Add(new Move(from, to1, MoveType.KnightPromotion));
                        }
                    }
                }

                ulong allowedCapMask = isPinned ? (targetMask & pinRays[(int)from]) : targetMask;

                // Captures
                // West capture: file > 0 (from - 9)
                if (f > 0)
                {
                    Square capWest = (Square)((int)from - 9);
                    if (((1UL << (int)capWest) & enemyPieces & allowedCapMask) != 0)
                    {
                        if (r == 1) // Promotion Capture
                        {
                            list.Add(new Move(from, capWest, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capWest, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capWest, MoveType.Capture));
                        }
                    }
                }

                // East capture: file < 7 (from - 7)
                if (f < 7)
                {
                    Square capEast = (Square)((int)from - 7);
                    if (((1UL << (int)capEast) & enemyPieces & allowedCapMask) != 0)
                    {
                        if (r == 1) // Promotion Capture
                        {
                            list.Add(new Move(from, capEast, MoveType.QueenPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.RookPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.BishopPromotionCapture));
                            list.Add(new Move(from, capEast, MoveType.KnightPromotionCapture));
                        }
                        else
                        {
                            list.Add(new Move(from, capEast, MoveType.Capture));
                        }
                    }
                }
            }

            // En Passant
            if (board.EpSquare != Square.None)
            {
                Square epTo = board.EpSquare;
                int epFile = (int)epTo.File();
                Square epCapSq = (Square)((int)epTo + 8);

                if (epFile > 0)
                {
                    Square from = (Square)((int)epCapSq - 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, 0, 0UL, enemyPieces, occupied, ref list);
                    }
                }
                if (epFile < 7)
                {
                    Square from = (Square)((int)epCapSq + 1);
                    if (((1UL << (int)from) & board.Pawns & ourPieces) != 0)
                    {
                        TryAddEnPassant(in board, from, epTo, epCapSq, kingSq, 0, 0UL, enemyPieces, occupied, ref list);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryAddEnPassant(
        in Board board,
        Square from,
        Square to,
        Square capSq,
        Square kingSq,
        int numCheckers,
        ulong checkers,
        ulong enemyPieces,
        ulong occupied,
        ref MoveList list)
    {
        if (numCheckers > 0)
        {
            Square checkerSq = Bitboard.Lsb(checkers);
            bool blocks = (Bitboard.Between[(int)kingSq, (int)checkerSq] & (1UL << (int)to)) != 0;
            bool captures = capSq == checkerSq;
            if (!blocks && !captures) return;
        }

        ulong occAfter = (occupied ^ (1UL << (int)from) ^ (1UL << (int)capSq)) | (1UL << (int)to);
        ulong enemyNonPawnSliders = enemyPieces & (board.Bishops | board.Rooks | board.Queens);

        ulong rAttackers = (board.Rooks | board.Queens) & enemyNonPawnSliders;
        if ((MagicBitboards.GetRookAttacks(kingSq, occAfter) & rAttackers) != 0) return;

        ulong bAttackers = (board.Bishops | board.Queens) & enemyNonPawnSliders;
        if ((MagicBitboards.GetBishopAttacks(kingSq, occAfter) & bAttackers) != 0) return;

        list.Add(new Move(from, to, MoveType.EnPassant));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSquareAttackedCustom(in Board board, Square sq, Color attackerColor, ulong occ)
    {
        int s = (byte)sq;
        ulong attackerPieces = attackerColor == Color.White ? board.White : board.Black;

        ulong pawnAttacks = attackerColor == Color.White
            ? Bitboard.BlackPawnAttacks[s]
            : Bitboard.WhitePawnAttacks[s];
        if ((pawnAttacks & board.Pawns & attackerPieces) != 0) return true;

        if ((Bitboard.KnightAttacks[s] & board.Knights & attackerPieces) != 0) return true;

        if ((Bitboard.KingAttacks[s] & board.Kings & attackerPieces) != 0) return true;

        ulong bq = (board.Bishops | board.Queens) & attackerPieces;
        if (bq != 0 && (MagicBitboards.GetBishopAttacks(sq, occ) & bq) != 0) return true;

        ulong rq = (board.Rooks | board.Queens) & attackerPieces;
        if (rq != 0 && (MagicBitboards.GetRookAttacks(sq, occ) & rq) != 0) return true;

        return false;
    }
}
