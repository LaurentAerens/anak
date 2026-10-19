using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anak.Core;

[InlineArray(1024)]
public struct RepetitionTable
{
    private ulong _element0;
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

    // Repetition history
    private RepetitionTable _history;
    private int _gamePly;

    public int GamePly => _gamePly;

    public ulong GetHistory(int index) => _history[index];

    public ulong Occupied
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => White | Black;
    }

    public ulong Empty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ~(White | Black);
    }

    // Compatibility properties
    public ulong ColorMaskWhite => White;
    public ulong ColorMaskBlack => Black;
    public ulong PieceMaskPawn => Pawns;
    public ulong PieceMaskKnight => Knights;
    public ulong PieceMaskBishop => Bishops;
    public ulong PieceMaskRook => Rooks;
    public ulong PieceMaskQueen => Queens;
    public ulong PieceMaskKing => Kings;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetPieces(Color color) => color == Color.White ? White : Black;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetPieces(PieceType type) => type switch
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
    public ulong GetPieces(Piece piece) => GetPieces(piece.Color()) & GetPieces(piece.Type());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Piece GetPieceAt(Square square)
    {
        ulong bit = 1UL << (byte)square;
        if ((Occupied & bit) == 0) return Piece.None;

        Color color = (White & bit) != 0 ? Color.White : Color.Black;
        PieceType type =
            (Pawns & bit) != 0 ? PieceType.Pawn :
            (Knights & bit) != 0 ? PieceType.Knight :
            (Bishops & bit) != 0 ? PieceType.Bishop :
            (Rooks & bit) != 0 ? PieceType.Rook :
            (Queens & bit) != 0 ? PieceType.Queen :
            PieceType.King;

        return PieceExtensions.Create(color, type);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSquareAttacked(Square sq, Color attackerColor)
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
        ulong bAttacks = MagicBitboards.GetBishopAttacks(sq, occ);
        if ((bAttacks & (Bishops | Queens) & attackerPieces) != 0) return true;

        // Rooks & Queens
        ulong rAttacks = MagicBitboards.GetRookAttacks(sq, occ);
        if ((rAttacks & (Rooks | Queens) & attackerPieces) != 0) return true;

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInCheck(Color color)
    {
        ulong kingBb = Kings & (color == Color.White ? White : Black);
        if (kingBb == 0) return false;
        Square kingSq = Bitboard.Lsb(kingBb);
        return IsSquareAttacked(kingSq, color.Opponent());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInCheck() => IsInCheck(SideToMove);

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
    }

    public bool MakeMove(Move move, out BoardState state)
    {
        Square from = move.From;
        Square to = move.To;
        MoveType type = move.Type;
        Color us = SideToMove;
        Color them = us.Opponent();

        Piece movedPiece = GetPieceAt(from);
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

        state = new BoardState(Castling, EpSquare, HalfmoveClock, capturedPiece, ZobristKey);

        // 1. Remove captured piece if any
        if (capturedPiece != Piece.None)
        {
            RemovePiece(capSq, them, capturedPiece.Type());
            ZobristKey ^= Zobrist.PieceSquare[(int)capturedPiece, (int)capSq];
        }

        // 2. Move or promote the piece
        if (move.IsPromotion)
        {
            RemovePiece(from, us, PieceType.Pawn);
            ZobristKey ^= Zobrist.PieceSquare[(int)movedPiece, (int)from];

            PieceType promoType = move.PromotionPieceType;
            AddPiece(to, us, promoType);
            Piece promoPiece = PieceExtensions.Create(us, promoType);
            ZobristKey ^= Zobrist.PieceSquare[(int)promoPiece, (int)to];
        }
        else
        {
            MovePiece(from, to, us, movedType);
            ZobristKey ^= Zobrist.PieceSquare[(int)movedPiece, (int)from];
            ZobristKey ^= Zobrist.PieceSquare[(int)movedPiece, (int)to];
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

        // 4. Update castling rights
        ZobristKey ^= Zobrist.Castling[(int)Castling];
        if (movedType == PieceType.King)
        {
            Castling &= us == Color.White ? ~CastlingRights.White : ~CastlingRights.Black;
        }
        else if (movedType == PieceType.Rook)
        {
            if (from == Square.A1) Castling &= ~CastlingRights.WhiteQueenside;
            else if (from == Square.H1) Castling &= ~CastlingRights.WhiteKingside;
            else if (from == Square.A8) Castling &= ~CastlingRights.BlackQueenside;
            else if (from == Square.H8) Castling &= ~CastlingRights.BlackKingside;
        }

        // If a rook was captured in its original corner, lose castling rights
        if (to == Square.A1) Castling &= ~CastlingRights.WhiteQueenside;
        else if (to == Square.H1) Castling &= ~CastlingRights.WhiteKingside;
        else if (to == Square.A8) Castling &= ~CastlingRights.BlackQueenside;
        else if (to == Square.H8) Castling &= ~CastlingRights.BlackKingside;

        ZobristKey ^= Zobrist.Castling[(int)Castling];

        // 5. Update en passant square
        if (EpSquare != Square.None)
        {
            ZobristKey ^= Zobrist.EpFile[(int)EpSquare.File()];
            EpSquare = Square.None;
        }

        if (type == MoveType.DoublePawnPush)
        {
            EpSquare = (Square)(((int)from + (int)to) / 2);
            ZobristKey ^= Zobrist.EpFile[(int)EpSquare.File()];
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

        // 8. Update history
        if (_gamePly < 1024)
        {
            _history[_gamePly++] = ZobristKey;
        }

        return true;
    }

    public void UnmakeMove(Move move, in BoardState state)
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
            Piece movedPiece = GetPieceAt(to);
            MovePiece(from, to, us, movedPiece.Type());
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
            MovePiece(rFrom, rTo, us, PieceType.Rook);
        }

        // 4. Restore state
        Castling = state.Castling;
        EpSquare = state.EpSquare;
        HalfmoveClock = state.HalfmoveClock;
        ZobristKey = state.ZobristKey;
    }

    public bool MakeNullMove(out BoardState state)
    {
        state = new BoardState(Castling, EpSquare, HalfmoveClock, Piece.None, ZobristKey);

        if (EpSquare != Square.None)
        {
            ZobristKey ^= Zobrist.EpFile[(int)EpSquare.File()];
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
    }

    public bool IsThreefoldRepetition()
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
