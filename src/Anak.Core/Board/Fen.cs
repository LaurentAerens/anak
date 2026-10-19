using System.Text;

namespace Anak.Core;

public static class Fen
{
    public const string StartPosition = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public static Board Parse(ReadOnlySpan<char> fen)
    {
        fen = fen.Trim();
        Board board = default;
        board.FullmoveNumber = 1;

        int part = 0;
        int rank = 7;
        int file = 0;

        int i = 0;
        while (i < fen.Length)
        {
            // Skip spaces to advance parts
            if (fen[i] == ' ')
            {
                while (i < fen.Length && fen[i] == ' ') i++;
                part++;
                continue;
            }

            switch (part)
            {
                case 0: // 1. Piece placement
                    {
                        char c = fen[i];
                        if (c == '/')
                        {
                            rank--;
                            file = 0;
                        }
                        else if (char.IsDigit(c))
                        {
                            file += c - '0';
                        }
                        else
                        {
                            Piece piece = PieceExtensions.FromChar(c);
                            if (piece != Piece.None && file < 8 && rank >= 0)
                            {
                                Square sq = SquareExtensions.Create(file, rank);
                                ulong bit = 1UL << (byte)sq;
                                Color color = piece.Color();
                                PieceType type = piece.Type();

                                if (color == Color.White) board.White |= bit;
                                else board.Black |= bit;

                                switch (type)
                                {
                                    case PieceType.Pawn: board.Pawns |= bit; break;
                                    case PieceType.Knight: board.Knights |= bit; break;
                                    case PieceType.Bishop: board.Bishops |= bit; break;
                                    case PieceType.Rook: board.Rooks |= bit; break;
                                    case PieceType.Queen: board.Queens |= bit; break;
                                    case PieceType.King: board.Kings |= bit; break;
                                }
                                file++;
                            }
                        }
                        i++;
                        break;
                    }

                case 1: // 2. Side to move
                    {
                        board.SideToMove = fen[i] == 'b' ? Color.Black : Color.White;
                        i++;
                        break;
                    }

                case 2: // 3. Castling rights
                    {
                        while (i < fen.Length && fen[i] != ' ')
                        {
                            char c = fen[i];
                            switch (c)
                            {
                                case 'K': board.Castling |= CastlingRights.WhiteKingside; break;
                                case 'Q': board.Castling |= CastlingRights.WhiteQueenside; break;
                                case 'k': board.Castling |= CastlingRights.BlackKingside; break;
                                case 'q': board.Castling |= CastlingRights.BlackQueenside; break;
                            }
                            i++;
                        }
                        break;
                    }

                case 3: // 4. En passant square
                    {
                        int start = i;
                        while (i < fen.Length && fen[i] != ' ') i++;
                        ReadOnlySpan<char> epSpan = fen[start..i];
                        board.EpSquare = epSpan is "-" ? Square.None : SquareExtensions.Parse(epSpan);
                        break;
                    }

                case 4: // 5. Halfmove clock
                    {
                        int start = i;
                        while (i < fen.Length && fen[i] != ' ') i++;
                        if (ushort.TryParse(fen[start..i], out ushort halfmove))
                        {
                            board.HalfmoveClock = halfmove;
                        }
                        break;
                    }

                case 5: // 6. Fullmove number
                    {
                        int start = i;
                        while (i < fen.Length && fen[i] != ' ') i++;
                        if (ushort.TryParse(fen[start..i], out ushort fullmove))
                        {
                            board.FullmoveNumber = fullmove;
                        }
                        break;
                    }

                default:
                    i++;
                    break;
            }
        }

        board.ZobristKey = Zobrist.ComputeKey(in board);
        board.PushHistory(board.ZobristKey);

        return board;
    }

    public static string ToFen(in Board board)
    {
        var sb = new StringBuilder();

        // 1. Piece placement
        for (int rank = 7; rank >= 0; rank--)
        {
            int emptyCount = 0;
            for (int file = 0; file < 8; file++)
            {
                Square sq = SquareExtensions.Create(file, rank);
                Piece piece = board.GetPieceAt(sq);
                if (piece == Piece.None)
                {
                    emptyCount++;
                }
                else
                {
                    if (emptyCount > 0)
                    {
                        sb.Append(emptyCount);
                        emptyCount = 0;
                    }
                    sb.Append(piece.ToChar());
                }
            }
            if (emptyCount > 0)
            {
                sb.Append(emptyCount);
            }
            if (rank > 0)
            {
                sb.Append('/');
            }
        }

        // 2. Side to move
        sb.Append(board.SideToMove == Color.White ? " w " : " b ");

        // 3. Castling
        if (board.Castling == CastlingRights.None)
        {
            sb.Append('-');
        }
        else
        {
            if (board.Castling.HasFlag(CastlingRights.WhiteKingside)) sb.Append('K');
            if (board.Castling.HasFlag(CastlingRights.WhiteQueenside)) sb.Append('Q');
            if (board.Castling.HasFlag(CastlingRights.BlackKingside)) sb.Append('k');
            if (board.Castling.HasFlag(CastlingRights.BlackQueenside)) sb.Append('q');
        }

        // 4. En passant
        sb.Append(' ');
        sb.Append(board.EpSquare.Name());

        // 5. Halfmove clock
        sb.Append(' ');
        sb.Append(board.HalfmoveClock);

        // 6. Fullmove number
        sb.Append(' ');
        sb.Append(board.FullmoveNumber);

        return sb.ToString();
    }

    public static string Ascii(in Board board)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  +---+---+---+---+---+---+---+---+");
        for (int rank = 7; rank >= 0; rank--)
        {
            sb.Append((rank + 1) + " |");
            for (int file = 0; file < 8; file++)
            {
                Square sq = SquareExtensions.Create(file, rank);
                Piece piece = board.GetPieceAt(sq);
                char c = piece == Piece.None ? ' ' : piece.ToChar();
                sb.Append($" {c} |");
            }
            sb.AppendLine();
            sb.AppendLine("  +---+---+---+---+---+---+---+---+");
        }
        sb.AppendLine("    a   b   c   d   e   f   g   h");
        sb.AppendLine();
        sb.AppendLine($"Side to move: {board.SideToMove}");
        sb.AppendLine($"Castling: {board.Castling}");
        sb.AppendLine($"En passant: {board.EpSquare.Name()}");
        sb.AppendLine($"Halfmove clock: {board.HalfmoveClock}");
        sb.AppendLine($"Fullmove number: {board.FullmoveNumber}");
        sb.AppendLine($"Zobrist key: 0x{board.ZobristKey:X16}");
        return sb.ToString();
    }
}
