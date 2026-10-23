using System.Text;
using Anak.Core;
using Anak.Protocol.Uci;

namespace Anak.Protocol.Analyze;

/// <summary>
/// Represents a parsed PGN chess game with metadata tags, move sequence, and board states.
/// </summary>
public sealed class PgnGame
{
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Move> Moves { get; } = [];
    public Board InitialBoard { get; set; }
    public Board FinalBoard { get; set; }
    public string Result { get; set; } = "*";

    public string White => Tags.GetValueOrDefault("White", "?");
    public string Black => Tags.GetValueOrDefault("Black", "?");
    public string Event => Tags.GetValueOrDefault("Event", "?");
    public string Date => Tags.GetValueOrDefault("Date", "????.??.??");
}

/// <summary>
/// Robust parser for PGN (Portable Game Notation) text files and SAN (Standard Algebraic Notation) moves.
/// </summary>
public static class PgnParser
{
    /// <summary>
    /// Parses all games from a PGN text string.
    /// </summary>
    public static List<PgnGame> ParseText(string pgnText)
    {
        List<PgnGame> games = [];
        using var reader = new StringReader(pgnText);

        Dictionary<string, string> currentTags = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder moveSection = new();
        bool inMoveSection = false;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                if (inMoveSection)
                {
                    ProcessGame(currentTags, moveSection.ToString(), games);
                    currentTags = new(StringComparer.OrdinalIgnoreCase);
                    moveSection.Clear();
                    inMoveSection = false;
                }

                // Extract tag [Key "Value"]
                int firstQuote = trimmed.IndexOf('"');
                int lastQuote = trimmed.LastIndexOf('"');
                if (firstQuote > 1 && lastQuote > firstQuote)
                {
                    string key = trimmed[1..firstQuote].Trim();
                    string val = trimmed[(firstQuote + 1)..lastQuote];
                    currentTags[key] = val;
                }
            }
            else if (trimmed.Length > 0)
            {
                inMoveSection = true;
                moveSection.Append(' ').Append(trimmed);
            }
        }

        if (currentTags.Count > 0 || moveSection.Length > 0)
        {
            ProcessGame(currentTags, moveSection.ToString(), games);
        }

        return games;
    }

    private static void ProcessGame(Dictionary<string, string> tags, string rawMoveText, List<PgnGame> games)
    {
        PgnGame game = new();
        foreach (var kvp in tags)
        {
            game.Tags[kvp.Key] = kvp.Value;
        }

        // Initialize board from FEN tag or standard starting position
        Board board;
        if (tags.TryGetValue("FEN", out string? fen) && !string.IsNullOrWhiteSpace(fen))
        {
            try
            {
                board = Fen.Parse(fen);
            }
            catch
            {
                board = Fen.Parse(Fen.StartPosition);
            }
        }
        else
        {
            board = Fen.Parse(Fen.StartPosition);
        }

        game.InitialBoard = board;

        // Strip comments { ... }, RAVs ( ... ), and line comments ; ...
        string cleanedText = CleanMoveText(rawMoveText);
        string[] tokens = cleanedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawToken in tokens)
        {
            string token = rawToken.Trim();
            if (token.Length == 0) continue;

            // Check for game termination markers
            if (token is "1-0" or "0-1" or "1/2-1/2" or "*")
            {
                game.Result = token;
                continue;
            }

            // Skip numeric annotation glyphs ($1, $2, etc.)
            if (token.StartsWith('$')) continue;

            // Strip move numbers (e.g., "1.", "12...", "1.e4")
            if (token.Contains('.'))
            {
                int dotIndex = token.LastIndexOf('.');
                token = token[(dotIndex + 1)..].Trim();
                if (token.Length == 0) continue;
            }

            // Skip if remaining token is all digits
            if (token.All(char.IsAsciiDigit)) continue;

            Move move = ParseSanMove(ref board, token);
            if (!move.IsNull)
            {
                board.MakeMove(move, out _);
                game.Moves.Add(move);
            }
        }

        game.FinalBoard = board;
        games.Add(game);
    }

    private static string CleanMoveText(string text)
    {
        StringBuilder sb = new(text.Length);
        int braceDepth = 0;
        int parenDepth = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '{')
            {
                braceDepth++;
                continue;
            }
            if (c == '}')
            {
                if (braceDepth > 0) braceDepth--;
                continue;
            }
            if (braceDepth > 0) continue;

            if (c == '(')
            {
                parenDepth++;
                continue;
            }
            if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
                continue;
            }
            if (parenDepth > 0) continue;

            if (c == ';')
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                {
                    i++;
                }
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses a move token in SAN or UCI format relative to the current board state.
    /// </summary>
    public static Move ParseSanMove(ref Board board, string token)
    {
        string s = token.Trim().TrimEnd('+', '#', '!', '?');
        if (string.IsNullOrEmpty(s)) return Move.Null;

        // Castling
        if (s.Equals("O-O", StringComparison.OrdinalIgnoreCase) || s == "0-0")
        {
            MoveList moves = default;
            board.GenerateLegalMoves(ref moves);
            for (int i = 0; i < moves.Count; i++)
            {
                if (moves[i].Type == MoveType.KingCastle)
                    return moves[i];
            }
            return Move.Null;
        }

        if (s.Equals("O-O-O", StringComparison.OrdinalIgnoreCase) || s == "0-0-0")
        {
            MoveList moves = default;
            board.GenerateLegalMoves(ref moves);
            for (int i = 0; i < moves.Count; i++)
            {
                if (moves[i].Type == MoveType.QueenCastle)
                    return moves[i];
            }
            return Move.Null;
        }

        // Check if UCI format (e.g. "e2e4", "e7e8q")
        if (s.Length is 4 or 5 &&
            s[0] >= 'a' && s[0] <= 'h' &&
            s[1] >= '1' && s[1] <= '8' &&
            s[2] >= 'a' && s[2] <= 'h' &&
            s[3] >= '1' && s[3] <= '8')
        {
            Move candidate = UciEngineAdapter.ParseAndMatchMove(ref board, s);
            if (!candidate.IsNull) return candidate;
        }

        // Promotion piece detection (e.g., "=Q", "=R", "Q", "N")
        PieceType promo = PieceType.None;
        int eqIndex = s.IndexOf('=');
        if (eqIndex >= 0 && eqIndex + 1 < s.Length)
        {
            promo = char.ToUpperInvariant(s[eqIndex + 1]) switch
            {
                'Q' => PieceType.Queen,
                'R' => PieceType.Rook,
                'B' => PieceType.Bishop,
                'N' => PieceType.Knight,
                _ => PieceType.None
            };
            s = s[..eqIndex];
        }
        else if (s.Length >= 3 && char.IsUpper(s[^1]) && "QRBN".Contains(s[^1]))
        {
            promo = s[^1] switch
            {
                'Q' => PieceType.Queen,
                'R' => PieceType.Rook,
                'B' => PieceType.Bishop,
                'N' => PieceType.Knight,
                _ => PieceType.None
            };
            s = s[..^1];
        }

        // Moving piece type
        PieceType pieceType = PieceType.Pawn;
        if (s.Length > 0 && "NBRQK".Contains(s[0]))
        {
            pieceType = s[0] switch
            {
                'N' => PieceType.Knight,
                'B' => PieceType.Bishop,
                'R' => PieceType.Rook,
                'Q' => PieceType.Queen,
                'K' => PieceType.King,
                _ => PieceType.Pawn
            };
            s = s[1..];
        }

        // Remove capture glyph 'x'
        s = s.Replace("x", string.Empty);

        if (s.Length < 2) return Move.Null;

        // Destination square is the last 2 characters
        string destStr = s[^2..];
        if (destStr[0] < 'a' || destStr[0] > 'h' || destStr[1] < '1' || destStr[1] > '8')
            return Move.Null;

        Square toSq = SquareExtensions.Parse(destStr);
        string disambig = s[..^2];

        char? disambigFile = null;
        char? disambigRank = null;

        if (disambig.Length == 1)
        {
            if (disambig[0] >= 'a' && disambig[0] <= 'h')
                disambigFile = disambig[0];
            else if (disambig[0] >= '1' && disambig[0] <= '8')
                disambigRank = disambig[0];
        }
        else if (disambig.Length == 2)
        {
            if (disambig[0] >= 'a' && disambig[0] <= 'h')
                disambigFile = disambig[0];
            if (disambig[1] >= '1' && disambig[1] <= '8')
                disambigRank = disambig[1];
        }

        // Match against legal moves
        MoveList legalMoves = default;
        board.GenerateLegalMoves(ref legalMoves);

        for (int i = 0; i < legalMoves.Count; i++)
        {
            Move m = legalMoves[i];
            if (m.To != toSq) continue;
            if (board.GetPieceAt(m.From).Type() != pieceType) continue;
            if (promo != PieceType.None && m.PromotionPieceType != promo) continue;
            if (disambigFile.HasValue && (char)('a' + (int)m.From.File()) != disambigFile.Value) continue;
            if (disambigRank.HasValue && (char)('1' + (int)m.From.Rank()) != disambigRank.Value) continue;

            return m;
        }

        return Move.Null;
    }
}
