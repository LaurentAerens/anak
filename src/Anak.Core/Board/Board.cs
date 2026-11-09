using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anak.Core;

[InlineArray(1024)]
public struct RepetitionTable
{
    private ulong _element0;
}

[InlineArray(64)]
public struct Mailbox
{
    private byte _element0;
}

[StructLayout(LayoutKind.Sequential)]
public struct Board : IEquatable<Board>
{
    // Bitboard piece sets
    public ulong White;
    public ulong Black;

    public ulong Pawns;
    public ulong Knights;
    public ulong Bishops;
    public ulong Rooks;
    public ulong Queens;
    public ulong Kings;

    // State fields
    public Color SideToMove;
    public CastlingRights Castling;
    public Square EpSquare;
    public ushort HalfmoveClock;
    public ushort FullmoveNumber;
    public ulong ZobristKey;
    public ulong PawnKey;

    private static ReadOnlySpan<byte> CastlingMask =>
    [
        0x0D, 0x0F, 0x0F, 0x0F, 0x0C, 0x0F, 0x0F, 0x0E, // Rank 1: A1(~WQ), E1(~W), H1(~WK)
        0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
        0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
        0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
        0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
        0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
        0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
        0x07, 0x0F, 0x0F, 0x0F, 0x03, 0x0F, 0x0F, 0x0B  // Rank 8: A8(~BQ), E8(~B), H8(~BK)
    ];

    // Piece mailbox (kept in hot cachelines alongside bitboards)
    private Mailbox _mailbox;
    private int _gamePly;

    // Repetition history (cold 8KB buffer placed at end of struct)
    private RepetitionTable _history;

    public int GamePly => _gamePly;

    public ulong GetHistory(int index) => _history[index];

    public readonly ulong Occupied
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => White | Black;
    }

    public readonly ulong Empty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ~(White | Black);
    }

    // Compatibility properties
    public readonly ulong ColorMaskWhite => White;
    public readonly ulong ColorMaskBlack => Black;
    public readonly ulong PieceMaskPawn => Pawns;
    public readonly ulong PieceMaskKnight => Knights;
    public readonly ulong PieceMaskBishop => Bishops;
    public readonly ulong PieceMaskRook => Rooks;
    public readonly ulong PieceMaskQueen => Queens;
    public readonly ulong PieceMaskKing => Kings;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetPieces(Color color) => color == Color.White ? White : Black;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetPieces(PieceType type) => type switch
    {
        PieceType.Pawn => Pawns,
        PieceType.Knight => Knights,
        PieceType.Bishop => Bishops,
        PieceType.Rook => Rooks,
        PieceType.Queen => Queens,
        PieceType.King => Kings,
        _ => 0UL
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetPieces(Piece piece) => GetPieces(piece.Color()) & GetPieces(piece.Type());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Piece GetPieceAt(Square square)
    {
        return (uint)square < 64
            ? (Piece)Unsafe.Add(ref Unsafe.As<Mailbox, byte>(ref Unsafe.AsRef(in _mailbox)), (int)(byte)square)
            : Piece.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetPiece(Square sq, Piece piece)
    {
        if ((uint)sq < 64)
            Unsafe.Add(ref Unsafe.As<Mailbox, byte>(ref _mailbox), (int)(byte)sq) = (byte)piece;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsSquareAttacked(Square sq, Color attackerColor)
    {
        int s = (byte)sq;
        ulong attackerPieces = attackerColor == Color.White ? White : Black;

        // Pawns
        ulong pawnAttacks = attackerColor == Color.White
            ? Bitboard.BlackPawnAttacks[s]
            : Bitboard.WhitePawnAttacks[s];
        if ((pawnAttacks & Pawns & attackerPieces) != 0) return true;

        // Knights
        if ((Bitboard.KnightAttacks[s] & Knights & attackerPieces) != 0) return true;

        // Kings
        if ((Bitboard.KingAttacks[s] & Kings & attackerPieces) != 0) return true;

        // Bishops & Queens
        ulong occ = Occupied;
        ulong enemyBishopsQueens = (Bishops | Queens) & attackerPieces;
        if (enemyBishopsQueens != 0 && (MagicBitboards.GetBishopAttacks(sq, occ) & enemyBishopsQueens) != 0) return true;

        // Rooks & Queens
        ulong enemyRooksQueens = (Rooks | Queens) & attackerPieces;
        if (enemyRooksQueens != 0 && (MagicBitboards.GetRookAttacks(sq, occ) & enemyRooksQueens) != 0) return true;

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsInCheck(Color color)
    {
        ulong kingBb = Kings & (color == Color.White ? White : Black);
        if (kingBb == 0) return false;
        Square kingSq = Bitboard.Lsb(kingBb);
        return IsSquareAttacked(kingSq, color.Opponent());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsInCheck() => IsInCheck(SideToMove);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsCheck() => IsInCheck();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool GivesCheck(Move move)
    {
        Color us = SideToMove;
        Color them = us.Opponent();
        ulong enemyKingBb = Kings & (them == Color.White ? White : Black);
        if (enemyKingBb == 0) return false;

        Square kSq = Bitboard.Lsb(enemyKingBb);
        int k = (byte)kSq;

        Square from = move.From;
        Square to = move.To;
        ulong fromBit = 1UL << (byte)from;
        ulong toBit = 1UL << (byte)to;

        PieceType movedType = move.IsPromotion ? move.PromotionPieceType : GetPieceAt(from).Type();
        ulong occ = (Occupied & ~fromBit) | toBit;

        if (move.Type == MoveType.EnPassant)
        {
            Square capSq = SquareExtensions.Create(to.File(), from.Rank());
            occ &= ~(1UL << (byte)capSq);
        }
        else if (move.IsCastling)
        {
            Square rFrom = move.Type == MoveType.KingCastle
                ? (us == Color.White ? Square.H1 : Square.H8)
                : (us == Color.White ? Square.A1 : Square.A8);
            Square rTo = move.Type == MoveType.KingCastle
                ? (us == Color.White ? Square.F1 : Square.F8)
                : (us == Color.White ? Square.D1 : Square.D8);
            occ = (occ & ~(1UL << (byte)rFrom)) | (1UL << (byte)rTo);
            toBit = 1UL << (byte)rTo;
            movedType = PieceType.Rook;
        }

        // 1. Direct check from the moved (or promoted) piece on 'to'
        switch (movedType)
        {
            case PieceType.Pawn:
                ulong pawnAttacks = us == Color.White
                    ? Bitboard.BlackPawnAttacks[k]
                    : Bitboard.WhitePawnAttacks[k];
                if ((pawnAttacks & toBit) != 0) return true;
                break;
            case PieceType.Knight:
                if ((Bitboard.KnightAttacks[k] & toBit) != 0) return true;
                break;
            case PieceType.Bishop:
                if ((MagicBitboards.GetBishopAttacks(kSq, occ) & toBit) != 0) return true;
                break;
            case PieceType.Rook:
                if ((MagicBitboards.GetRookAttacks(kSq, occ) & toBit) != 0) return true;
                break;
            case PieceType.Queen:
                if (((MagicBitboards.GetBishopAttacks(kSq, occ) | MagicBitboards.GetRookAttacks(kSq, occ)) & toBit) != 0) return true;
                break;
        }

        // 2. Discovered check from our remaining sliders
        ulong usPieces = (us == Color.White ? White : Black) & ~fromBit;
        ulong diagSliders = usPieces & (Bishops | Queens);
        if (diagSliders != 0 && (MagicBitboards.GetBishopAttacks(kSq, occ) & diagSliders) != 0)
        {
            return true;
        }

        ulong orthSliders = usPieces & (Rooks | Queens);
        if (orthSliders != 0 && (MagicBitboards.GetRookAttacks(kSq, occ) & orthSliders) != 0)
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddPiece(Square sq, Color color, PieceType type)
    {
        ulong bit = 1UL << (byte)sq;
        if (color == Color.White) White |= bit; else Black |= bit;
        switch (type)
        {
            case PieceType.Pawn: Pawns |= bit; break;
            case PieceType.Knight: Knights |= bit; break;
            case PieceType.Bishop: Bishops |= bit; break;
            case PieceType.Rook: Rooks |= bit; break;
            case PieceType.Queen: Queens |= bit; break;
            case PieceType.King: Kings |= bit; break;
        }
        _mailbox[(byte)sq] = (byte)PieceExtensions.Create(color, type);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemovePiece(Square sq, Color color, PieceType type)
    {
        ulong bit = 1UL << (byte)sq;
        if (color == Color.White) White &= ~bit; else Black &= ~bit;
        switch (type)
        {
            case PieceType.Pawn: Pawns &= ~bit; break;
            case PieceType.Knight: Knights &= ~bit; break;
            case PieceType.Bishop: Bishops &= ~bit; break;
            case PieceType.Rook: Rooks &= ~bit; break;
            case PieceType.Queen: Queens &= ~bit; break;
            case PieceType.King: Kings &= ~bit; break;
        }
        _mailbox[(byte)sq] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MovePiece(Square from, Square to, Color color, PieceType type)
    {
        ulong mask = (1UL << (byte)from) | (1UL << (byte)to);
        if (color == Color.White) White ^= mask; else Black ^= mask;
        switch (type)
        {
            case PieceType.Pawn: Pawns ^= mask; break;
            case PieceType.Knight: Knights ^= mask; break;
            case PieceType.Bishop: Bishops ^= mask; break;
            case PieceType.Rook: Rooks ^= mask; break;
            case PieceType.Queen: Queens ^= mask; break;
            case PieceType.King: Kings ^= mask; break;
        }
        _mailbox[(byte)to] = _mailbox[(byte)from];
        _mailbox[(byte)from] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool MakeMove(Move move, out BoardState state)
    {
        return MakeMove(move, out state, GetPieceAt(move.From));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool MakeMove(Move move, out BoardState state, Piece movedPiece)
    {
        Square from = move.From;
        Square to = move.To;
        MoveType type = move.Type;
        Color us = SideToMove;
        Color them = us.Opponent();

        PieceType movedType = movedPiece.Type();

        // Determine captured piece
        Piece capturedPiece = Piece.None;
        Square capSq = to;
        if (type == MoveType.EnPassant)
        {
            capSq = SquareExtensions.Create(to.File(), from.Rank());
            capturedPiece = them == Color.White ? Piece.WhitePawn : Piece.BlackPawn;
        }
        else if ((Occupied & (1UL << (byte)to)) != 0)
        {
            capturedPiece = GetPieceAt(to);
        }

        state = new BoardState(Castling, EpSquare, HalfmoveClock, capturedPiece, ZobristKey, PawnKey);

        // 1. Remove captured piece if any
        if (capturedPiece != Piece.None)
        {
            PieceType capType = capturedPiece.Type();
            RemovePiece(capSq, them, capType);
            ulong capZobrist = Zobrist.PieceSquare[(int)capturedPiece, (int)capSq];
            ZobristKey ^= capZobrist;
            if (capType == PieceType.Pawn)
            {
                PawnKey ^= capZobrist;
            }
        }

        // 2. Move or promote the piece
        if (move.IsPromotion)
        {
            RemovePiece(from, us, PieceType.Pawn);
            ulong fromZobrist = Zobrist.PieceSquare[(int)movedPiece, (int)from];
            ZobristKey ^= fromZobrist;
            PawnKey ^= fromZobrist;

            PieceType promoType = move.PromotionPieceType;
            AddPiece(to, us, promoType);
            Piece promoPiece = PieceExtensions.Create(us, promoType);
            ZobristKey ^= Zobrist.PieceSquare[(int)promoPiece, (int)to];
        }
        else
        {
            MovePiece(from, to, us, movedType);
            ulong moveZobrist = Zobrist.PieceSquare[(int)movedPiece, (int)from] ^ Zobrist.PieceSquare[(int)movedPiece, (int)to];
            ZobristKey ^= moveZobrist;
            if (movedType == PieceType.Pawn)
            {
                PawnKey ^= moveZobrist;
            }
        }

        // 3. Castling rook move
        if (move.IsCastling)
        {
            Square rFrom, rTo;
            Piece rookPiece = us == Color.White ? Piece.WhiteRook : Piece.BlackRook;
            if (type == MoveType.KingCastle)
            {
                rFrom = us == Color.White ? Square.H1 : Square.H8;
                rTo = us == Color.White ? Square.F1 : Square.F8;
            }
            else
            {
                rFrom = us == Color.White ? Square.A1 : Square.A8;
                rTo = us == Color.White ? Square.D1 : Square.D8;
            }
            MovePiece(rFrom, rTo, us, PieceType.Rook);
            ZobristKey ^= Zobrist.PieceSquare[(int)rookPiece, (int)rFrom];
            ZobristKey ^= Zobrist.PieceSquare[(int)rookPiece, (int)rTo];
        }

        // 4. Update castling rights branchlessly via 64-square mask table
        ZobristKey ^= Zobrist.Castling[(int)Castling];
        Castling &= (CastlingRights)(CastlingMask[(int)from] & CastlingMask[(int)to]);
        ZobristKey ^= Zobrist.Castling[(int)Castling];

        // 5. Update en passant square
        if (EpSquare != Square.None)
        {
            ZobristKey ^= Zobrist.EpFile[(int)EpSquare & 7];
            EpSquare = Square.None;
        }

        ulong epKey = 0UL;
        bool epCanBeCaptured = false;
        if (type == MoveType.DoublePawnPush)
        {
            Square epSq = (Square)((int)from ^ 24);
            EpSquare = epSq;
            epKey = Zobrist.EpFile[(int)from & 7];
            ZobristKey ^= epKey;

            ulong epAttackers = (us == Color.White
                ? Bitboard.WhitePawnAttacks[(int)epSq]
                : Bitboard.BlackPawnAttacks[(int)epSq]) & Pawns & (them == Color.White ? White : Black);
            epCanBeCaptured = epAttackers != 0;
        }

        // 6. Update clocks
        if (movedType == PieceType.Pawn || capturedPiece != Piece.None)
        {
            HalfmoveClock = 0;
        }
        else
        {
            HalfmoveClock++;
        }

        if (us == Color.Black)
        {
            FullmoveNumber++;
        }

        // 7. Update side to move
        SideToMove = them;
        ZobristKey ^= Zobrist.BlackToMove;

        // 8. Update history (strip EpFile from repetition history if no enemy pawn can capture en passant)
        if (_gamePly < 1024)
        {
            _history[_gamePly++] = (type == MoveType.DoublePawnPush && !epCanBeCaptured)
                ? (ZobristKey ^ epKey)
                : ZobristKey;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void UnmakeMove(Move move, in BoardState state)
    {
        UnmakeMove(move, in state, move.IsPromotion ? Piece.None : GetPieceAt(move.To));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void UnmakeMove(Move move, in BoardState state, Piece movedPiece)
    {
        if (_gamePly > 0)
        {
            _gamePly--;
        }

        Square from = move.From;
        Square to = move.To;
        MoveType type = move.Type;

        SideToMove = SideToMove.Opponent();
        Color us = SideToMove;
        Color them = us.Opponent();

        if (us == Color.Black)
        {
            FullmoveNumber--;
        }

        // 1. Undo piece move or promotion
        if (move.IsPromotion)
        {
            PieceType promoType = move.PromotionPieceType;
            RemovePiece(to, us, promoType);
            AddPiece(from, us, PieceType.Pawn);
        }
        else
        {
            MovePiece(to, from, us, movedPiece.Type());
        }

        // 2. Undo capture
        if (state.CapturedPiece != Piece.None)
        {
            Square capSq = type == MoveType.EnPassant
                ? SquareExtensions.Create(to.File(), from.Rank())
                : to;
            AddPiece(capSq, them, state.CapturedPiece.Type());
        }

        // 3. Undo castling rook move
        if (move.IsCastling)
        {
            Square rFrom, rTo;
            if (type == MoveType.KingCastle)
            {
                rFrom = us == Color.White ? Square.H1 : Square.H8;
                rTo = us == Color.White ? Square.F1 : Square.F8;
            }
            else
            {
                rFrom = us == Color.White ? Square.A1 : Square.A8;
                rTo = us == Color.White ? Square.D1 : Square.D8;
            }
            MovePiece(rTo, rFrom, us, PieceType.Rook);
        }

        // 4. Restore state
        Castling = state.Castling;
        EpSquare = state.EpSquare;
        HalfmoveClock = state.HalfmoveClock;
        ZobristKey = state.ZobristKey;
        PawnKey = state.PawnKey;
    }

    public bool MakeNullMove(out BoardState state)
    {
        state = new BoardState(Castling, EpSquare, HalfmoveClock, Piece.None, ZobristKey, PawnKey);

        if (EpSquare != Square.None)
        {
            ZobristKey ^= Zobrist.EpFile[(int)EpSquare & 7];
            EpSquare = Square.None;
        }

        SideToMove = SideToMove.Opponent();
        ZobristKey ^= Zobrist.BlackToMove;
        HalfmoveClock++;

        if (_gamePly < 1024)
        {
            _history[_gamePly++] = ZobristKey;
        }

        return true;
    }

    public void UnmakeNullMove(in BoardState state)
    {
        if (_gamePly > 0)
        {
            _gamePly--;
        }

        SideToMove = SideToMove.Opponent();
        Castling = state.Castling;
        EpSquare = state.EpSquare;
        HalfmoveClock = state.HalfmoveClock;
        ZobristKey = state.ZobristKey;
        PawnKey = state.PawnKey;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsThreefoldRepetition()
    {
        int count = 0;
        int start = Math.Max(0, (_gamePly - 1) - HalfmoveClock);
        for (int i = _gamePly - 3; i >= start; i -= 2)
        {
            if (_history[i] == ZobristKey)
            {
                count++;
                if (count >= 2) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks for a repetition draw in search.
    /// Within the active search tree (i >= rootIndex), a 2-fold repetition (1 prior occurrence)
    /// is an immediate draw because the move cycle can be repeated indefinitely.
    /// For occurrences predating the search root (i < rootIndex), 2 prior occurrences
    /// (3-fold repetition total) are required to avoid false draw declarations on game history.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsRepetition(int searchPly)
    {
        if (HalfmoveClock < 4) return false;
        int count = 0;
        int start = Math.Max(0, (_gamePly - 1) - HalfmoveClock);
        int rootIndex = (_gamePly - 1) - searchPly;

        for (int i = _gamePly - 3; i >= start; i -= 2)
        {
            if (_history[i] == ZobristKey)
            {
                if (i >= rootIndex)
                {
                    return true;
                }

                count++;
                if (count >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsFiftyMoveRule() => HalfmoveClock >= 100;

    public bool HasInsufficientMaterial()
    {
        if ((Pawns | Rooks | Queens) != 0) return false;

        int whiteKnights = Bitboard.PopCount(Knights & White);
        int whiteBishops = Bitboard.PopCount(Bishops & White);
        int blackKnights = Bitboard.PopCount(Knights & Black);
        int blackBishops = Bitboard.PopCount(Bishops & Black);

        int whiteMinors = whiteKnights + whiteBishops;
        int blackMinors = blackKnights + blackBishops;

        if (whiteMinors == 0 && blackMinors == 0) return true;
        if ((whiteMinors == 1 && blackMinors == 0) || (whiteMinors == 0 && blackMinors == 1)) return true;

        if (whiteMinors == 1 && whiteBishops == 1 && blackMinors == 1 && blackBishops == 1)
        {
            const ulong lightSquares = 0x55AA55AA55AA55AAUL;
            bool whiteOnLight = ((Bishops & White) & lightSquares) != 0;
            bool blackOnLight = ((Bishops & Black) & lightSquares) != 0;
            return whiteOnLight == blackOnLight;
        }

        return false;
    }

    public bool IsGameOver(out GameResult result)
    {
        if (IsThreefoldRepetition())
        {
            result = GameResult.ThreefoldRepetition;
            return true;
        }

        if (IsFiftyMoveRule())
        {
            result = GameResult.FiftyMoveRule;
            return true;
        }

        if (HasInsufficientMaterial())
        {
            result = GameResult.InsufficientMaterial;
            return true;
        }

        MoveList moves = default;
        GenerateLegalMoves(ref moves);

        if (moves.Count == 0)
        {
            if (IsInCheck())
            {
                result = GameResult.Checkmate;
                return true;
            }
            else
            {
                result = GameResult.Stalemate;
                return true;
            }
        }

        result = GameResult.Ongoing;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GenerateLegalMoves(ref MoveList moveList)
    {
        MoveGenerator.GenerateLegalMoves(in this, ref moveList);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MoveList GenerateLegalMoves()
    {
        MoveList list = default;
        GenerateLegalMoves(ref list);
        return list;
    }

    /// <summary>
    /// Fast single-candidate move legality validation for lazy move generation.
    /// Validates piece color, destination square, piece-specific movement attacks, and king safety.
    /// Castling falls back to legal move check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public readonly bool IsLegal(in Move move)
    {
        if (move.IsNull) return false;

        Square from = move.From;
        Square to = move.To;
        if (from == to) return false;

        Color us = SideToMove;
        Color them = us.Opponent();

        // Fast O(1) castling legality check without generating moves
        if (move.IsCastling)
        {
            if (IsInCheck()) return false;
            ulong occ = Occupied;

            if (us == Color.White)
            {
                if (move.Type == MoveType.KingCastle)
                {
                    const ulong emptyMask = (1UL << (int)Square.F1) | (1UL << (int)Square.G1);
                    return from == Square.E1 && to == Square.G1 &&
                           (Castling & CastlingRights.WhiteKingside) != 0 &&
                           (occ & emptyMask) == 0 &&
                           !IsSquareAttacked(Square.F1, Color.Black) &&
                           !IsSquareAttacked(Square.G1, Color.Black);
                }
                if (move.Type == MoveType.QueenCastle)
                {
                    const ulong emptyMask = (1UL << (int)Square.B1) | (1UL << (int)Square.C1) | (1UL << (int)Square.D1);
                    return from == Square.E1 && to == Square.C1 &&
                           (Castling & CastlingRights.WhiteQueenside) != 0 &&
                           (occ & emptyMask) == 0 &&
                           !IsSquareAttacked(Square.D1, Color.Black) &&
                           !IsSquareAttacked(Square.C1, Color.Black);
                }
            }
            else
            {
                if (move.Type == MoveType.KingCastle)
                {
                    const ulong emptyMask = (1UL << (int)Square.F8) | (1UL << (int)Square.G8);
                    return from == Square.E8 && to == Square.G8 &&
                           (Castling & CastlingRights.BlackKingside) != 0 &&
                           (occ & emptyMask) == 0 &&
                           !IsSquareAttacked(Square.F8, Color.White) &&
                           !IsSquareAttacked(Square.G8, Color.White);
                }
                if (move.Type == MoveType.QueenCastle)
                {
                    const ulong emptyMask = (1UL << (int)Square.B8) | (1UL << (int)Square.C8) | (1UL << (int)Square.D8);
                    return from == Square.E8 && to == Square.C8 &&
                           (Castling & CastlingRights.BlackQueenside) != 0 &&
                           (occ & emptyMask) == 0 &&
                           !IsSquareAttacked(Square.D8, Color.White) &&
                           !IsSquareAttacked(Square.C8, Color.White);
                }
            }
            return false;
        }

        Piece piece = GetPieceAt(from);
        if (piece == Piece.None || piece.Color() != us) return false;

        ulong toBit = 1UL << (int)to;
        ulong ourPieces = GetPieces(us);
        if ((ourPieces & toBit) != 0) return false;

        PieceType pt = piece.Type();
        ulong enemyPieces = GetPieces(them);
        ulong occupied = Occupied;
        bool isCaptureDest = (enemyPieces & toBit) != 0;

        switch (pt)
        {
            case PieceType.Pawn:
                {
                    int rDiff = (int)to.Rank() - (int)from.Rank();
                    int fDiff = (int)to.File() - (int)from.File();

                    if (us == Color.White)
                    {
                        if (fDiff == 0)
                        {
                            // Push
                            if (isCaptureDest || move.IsCapture) return false;

                            if (rDiff == 1)
                            {
                                if ((occupied & toBit) != 0) return false;
                                if (to.Rank() == Rank.Rank8)
                                {
                                    if (!move.IsPromotion || move.PromotionPieceType == PieceType.None) return false;
                                }
                                else
                                {
                                    if (move.IsPromotion || move.Type != MoveType.Quiet) return false;
                                }
                            }
                            else if (rDiff == 2)
                            {
                                if (from.Rank() != Rank.Rank2) return false;
                                if (move.Type != MoveType.DoublePawnPush) return false;
                                Square intermediate = (Square)((int)from + 8);
                                if ((occupied & ((1UL << (int)intermediate) | toBit)) != 0) return false;
                            }
                            else
                            {
                                return false;
                            }
                        }
                        else if (fDiff == 1 || fDiff == -1)
                        {
                            // Capture or En Passant
                            if (rDiff != 1) return false;

                            if (move.Type == MoveType.EnPassant)
                            {
                                if (EpSquare == Square.None || to != EpSquare) return false;
                                Square capSq = (Square)((int)to - 8);
                                if ((Pawns & enemyPieces & (1UL << (int)capSq)) == 0) return false;
                            }
                            else
                            {
                                if (!isCaptureDest) return false;
                                if (to.Rank() == Rank.Rank8)
                                {
                                    if (!move.IsPromotion || move.PromotionPieceType == PieceType.None) return false;
                                }
                                else
                                {
                                    if (move.Type != MoveType.Capture) return false;
                                }
                            }
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else // Black
                    {
                        if (fDiff == 0)
                        {
                            // Push
                            if (isCaptureDest || move.IsCapture) return false;

                            if (rDiff == -1)
                            {
                                if ((occupied & toBit) != 0) return false;
                                if (to.Rank() == Rank.Rank1)
                                {
                                    if (!move.IsPromotion || move.PromotionPieceType == PieceType.None) return false;
                                }
                                else
                                {
                                    if (move.IsPromotion || move.Type != MoveType.Quiet) return false;
                                }
                            }
                            else if (rDiff == -2)
                            {
                                if (from.Rank() != Rank.Rank7) return false;
                                if (move.Type != MoveType.DoublePawnPush) return false;
                                Square intermediate = (Square)((int)from - 8);
                                if ((occupied & ((1UL << (int)intermediate) | toBit)) != 0) return false;
                            }
                            else
                            {
                                return false;
                            }
                        }
                        else if (fDiff == 1 || fDiff == -1)
                        {
                            // Capture or En Passant
                            if (rDiff != -1) return false;

                            if (move.Type == MoveType.EnPassant)
                            {
                                if (EpSquare == Square.None || to != EpSquare) return false;
                                Square capSq = (Square)((int)to + 8);
                                if ((Pawns & enemyPieces & (1UL << (int)capSq)) == 0) return false;
                            }
                            else
                            {
                                if (!isCaptureDest) return false;
                                if (to.Rank() == Rank.Rank1)
                                {
                                    if (!move.IsPromotion || move.PromotionPieceType == PieceType.None) return false;
                                }
                                else
                                {
                                    if (move.Type != MoveType.Capture) return false;
                                }
                            }
                        }
                        else
                        {
                            return false;
                        }
                    }
                    break;
                }

            case PieceType.Knight:
                {
                    if (move.IsPromotion || move.IsEnPassant) return false;
                    if ((Bitboard.KnightAttacks[(int)from] & toBit) == 0) return false;
                    if (isCaptureDest != move.IsCapture) return false;
                    break;
                }

            case PieceType.Bishop:
                {
                    if (move.IsPromotion || move.IsEnPassant) return false;
                    if ((MagicBitboards.GetBishopAttacks(from, occupied) & toBit) == 0) return false;
                    if (isCaptureDest != move.IsCapture) return false;
                    break;
                }

            case PieceType.Rook:
                {
                    if (move.IsPromotion || move.IsEnPassant) return false;
                    if ((MagicBitboards.GetRookAttacks(from, occupied) & toBit) == 0) return false;
                    if (isCaptureDest != move.IsCapture) return false;
                    break;
                }

            case PieceType.Queen:
                {
                    if (move.IsPromotion || move.IsEnPassant) return false;
                    ulong qAttacks = MagicBitboards.GetBishopAttacks(from, occupied) | MagicBitboards.GetRookAttacks(from, occupied);
                    if ((qAttacks & toBit) == 0) return false;
                    if (isCaptureDest != move.IsCapture) return false;
                    break;
                }

            case PieceType.King:
                {
                    if (move.IsPromotion || move.IsEnPassant) return false;
                    if ((Bitboard.KingAttacks[(int)from] & toBit) == 0) return false;
                    if (isCaptureDest != move.IsCapture) return false;
                    break;
                }

            default:
                return false;
        }

        // King safety: verify our king is not left in check on post-move bitboards without mutating Board
        Square kingSq;
        if (pt == PieceType.King)
        {
            kingSq = to;
        }
        else
        {
            ulong kingBb = Kings & ourPieces;
            if (kingBb == 0) return true;
            kingSq = Bitboard.Lsb(kingBb);
        }

        ulong fromBit = 1UL << (int)from;
        ulong occAfter;
        ulong enemyAfter;

        if (move.Type == MoveType.EnPassant)
        {
            int capSq = us == Color.White ? (int)to - 8 : (int)to + 8;
            ulong capBit = 1UL << capSq;
            occAfter = (occupied ^ fromBit ^ capBit) | toBit;
            enemyAfter = enemyPieces ^ capBit;
        }
        else
        {
            occAfter = (occupied ^ fromBit) | toBit;
            enemyAfter = enemyPieces & ~toBit;
        }

        int k = (byte)kingSq;
        ulong pawnAttacks = us == Color.White ? Bitboard.WhitePawnAttacks[k] : Bitboard.BlackPawnAttacks[k];
        if ((pawnAttacks & Pawns & enemyAfter) != 0) return false;
        if ((Bitboard.KnightAttacks[k] & Knights & enemyAfter) != 0) return false;
        if ((Bitboard.KingAttacks[k] & Kings & enemyAfter) != 0) return false;
        ulong bq = (Bishops | Queens) & enemyAfter;
        if (bq != 0 && (MagicBitboards.GetBishopAttacks(kingSq, occAfter) & bq) != 0) return false;
        ulong rq = (Rooks | Queens) & enemyAfter;
        if (rq != 0 && (MagicBitboards.GetRookAttacks(kingSq, occAfter) & rq) != 0) return false;

        return true;
    }

    public void PushHistory(ulong key)
    {
        if (_gamePly < 1024)
        {
            _history[_gamePly++] = key;
        }
    }

    public void ClearHistory()
    {
        _gamePly = 0;
    }

    public bool Equals(Board other)
    {
        return White == other.White &&
               Black == other.Black &&
               Pawns == other.Pawns &&
               Knights == other.Knights &&
               Bishops == other.Bishops &&
               Rooks == other.Rooks &&
               Queens == other.Queens &&
               Kings == other.Kings &&
               SideToMove == other.SideToMove &&
               Castling == other.Castling &&
               EpSquare == other.EpSquare &&
               HalfmoveClock == other.HalfmoveClock &&
               FullmoveNumber == other.FullmoveNumber &&
               ZobristKey == other.ZobristKey;
    }

    public override bool Equals(object? obj) => obj is Board other && Equals(other);

    public override int GetHashCode() => (int)ZobristKey;

    public override string ToString() => Fen.ToFen(in this);

    public static bool operator ==(Board left, Board right) => left.Equals(right);
    public static bool operator !=(Board left, Board right) => !left.Equals(right);
}
