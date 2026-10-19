using System.Buffers.Binary;
using Anak.Core;
using File = System.IO.File;

namespace Anak.Data.PolyGlot;

/// <summary>
/// Built-in master opening book provider containing canonical grandmaster lines
/// across all major openings (1.e4, 1.d4, 1.c4, 1.Nf3).
/// Used as an immediate high-variety fallback whenever an external book.bin is not supplied.
/// </summary>
public static class DefaultOpeningBook
{
    private static readonly (string Moves, int Weight)[] _lines =
    [
        // --- 1. e4 KING'S PAWN OPENINGS ---
        ("e2e4 e7e5 g1f3 b8c6 f1b5 a7a6 b5a4 g8f6 e1g1 f8e7 f1e1 b7b5 a4b3 d7d6 c2c3 e1g1", 500), // Ruy Lopez Closed
        ("e2e4 e7e5 g1f3 b8c6 f1b5 g8f6 e1g1 f6e4 d2d4 e4d6 b5c6 d7c6 d4e5", 350),             // Ruy Lopez Berlin
        ("e2e4 e7e5 g1f3 b8c6 f1c4 f8c5 c2c3 g8f6 d2d3 e8g8 e1g1 d7d6", 450),                   // Italian Game Giuoco Piano
        ("e2e4 e7e5 g1f3 b8c6 f1c4 g8f6 d2d3 f8e7 e1g1 e8g8", 350),                               // Italian Two Knights
        ("e2e4 e7e5 g1f3 b8c6 d2d4 e5d4 f3d4 f8c5 d4b3 c5b6 b1c3", 250),                         // Scotch Game
        ("e2e4 e7e5 g1f3 g8f6 f3e5 d7d6 e5f3 f6e4 d2d4 d6d5 f1d3", 250),                         // Petrov Defense
        ("e2e4 e7e5 b1c3 g8f6 f2f4 d7d5 f4e5 f6e4 g1f3", 150),                                     // Vienna Gambit
        ("e2e4 e7e5 f2f4 e5f4 g1f3 g7g5 h2h4 g5g4", 100),                                           // King's Gambit

        // --- SICILIAN DEFENSE (1. e4 c5) ---
        ("e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6 b1c3 a7a6 c1e3 e7e5 d4b3 c8e6", 600),          // Sicilian Najdorf
        ("e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6 b1c3 g7g6 c1e3 f8g7 f2f3 e8g8 d1d2 b8c6", 400), // Sicilian Dragon
        ("e2e4 c7c5 g1f3 b8c6 d2d4 c5d4 f3d4 g8f6 b1c3 e7e5 d4b5 d7d6 c1g5", 350),                // Sicilian Sveshnikov
        ("e2e4 c7c5 g1f3 b8c6 f1b5 g7g6 e1g1 f8g7 f1e1 e7e5", 250),                               // Sicilian Rossolimo
        ("e2e4 c7c5 g1f3 e7e6 d2d4 c5d4 f3d4 a7a6 b1c3 d8c7 f1d3", 300),                         // Sicilian Kan
        ("e2e4 c7c5 c2c3 d7d5 e4d5 d8d5 d2d4 g8f6 g1f3 c8g4", 200),                               // Sicilian Alapin
        ("e2e4 c7c5 b1c3 b8c6 g2g3 g7g6 f1g2 f8g7 d2d3 d7d6", 150),                               // Closed Sicilian

        // --- FRENCH DEFENSE (1. e4 e6) ---
        ("e2e4 e7e6 d2d4 d7d5 b1c3 g8f6 c1g5 f8e7 e4e5 f6d7 h2h4", 350),                         // French Classical / Alekhine-Chatard
        ("e2e4 e7e6 d2d4 d7d5 b1c3 f8b4 e4e5 c7c5 a2a3 b4c3 b2c3", 300),                         // French Winawer
        ("e2e4 e7e6 d2d4 d7d5 b1d2 c7c5 g1f3 c5d4 f3d4 b8c6", 250),                               // French Tarrasch
        ("e2e4 e7e6 d2d4 d7d5 e4e5 c7c5 c2c3 b8c6 g1f3 d8b6", 200),                               // French Advance

        // --- CARO-KANN DEFENSE (1. e4 c6) ---
        ("e2e4 c7c6 d2d4 d7d5 b1c3 d5e4 c3e4 c8f5 e4g3 f5g6 h2h4 h7h6 g1f3 b8d7", 400),          // Caro-Kann Classical
        ("e2e4 c7c6 d2d4 d7d5 e4e5 c8f5 g1f3 e7e6 f1e2 c6c5 c1e3", 300),                         // Caro-Kann Advance
        ("e2e4 c7c6 d2d4 d7d5 e4d5 c6d5 f1d3 b8c6 c2c3 d8c7", 150),                               // Caro-Kann Exchange

        // --- OTHER 1. e4 DEFENSES ---
        ("e2e4 d7d6 d2d4 g8f6 b1c3 g7g6 f2f4 f8g7 g1f3 e8g8", 200),                               // Pirc Austrian Attack
        ("e2e4 d7d5 e4d5 d8d5 b1c3 d5a5 d2d4 g8f6 g1f3 c8f5", 150),                               // Scandinavian Defense

        // --- 1. d4 QUEEN'S PAWN OPENINGS ---
        ("d2d4 g8f6 c2c4 e7e6 b1c3 f8b4 e2e3 e8g8 f1d3 d7d5 g1f3 c7c5", 500),                      // Nimzo-Indian
        ("d2d4 g8f6 c2c4 e7e6 g1f3 b7b6 g2g3 c8b7 f1g2 f8e7 e1g1", 350),                           // Queen's Indian
        ("d2d4 g8f6 c2c4 g7g6 b1c3 f8g7 e2e4 d7d6 g1f3 e8g8 f1e2 e7e5", 500),                      // King's Indian Mar del Plata
        ("d2d4 g8f6 c2c4 g7g6 b1c3 d7d5 c4d5 f6d5 e2e4 d5c3 b2c3 f8g7 g1f3 c7c5", 400),           // Grünfeld Exchange
        ("d2d4 g8f6 c2c4 c7c5 d4d5 b7b5 c4b5 a7a6 b5a6 c8a6", 200),                               // Benko Gambit
        ("d2d4 g8f6 g1f3 d7d5 c1f4 c7c5 e2e3 b8c6 c2c3 d8b6", 350),                               // London System

        // --- QUEEN'S GAMBIT (1. d4 d5) ---
        ("d2d4 d7d5 c2c4 e7e6 b1c3 g8f6 c1g5 f8e7 e2e3 e8g8 g1f3 h7h6", 450),                      // QGD Orthodox
        ("d2d4 d7d5 c2c4 e7e6 b1c3 g8f6 c4d5 e6d5 c1g5 c7c6 e2e3", 350),                           // QGD Exchange
        ("d2d4 d7d5 c2c4 c7c6 g1f3 g8f6 b1c3 e7e6 e2e3 b8d7 f1d3", 400),                           // Semi-Slav
        ("d2d4 d7d5 c2c4 c7c6 g1f3 g8f6 b1c3 d5c4 a2a4 c8f5", 300),                               // Classical Slav
        ("d2d4 d7d5 c2c4 d5c4 g1f3 g8f6 e2e3 e7e6 f1c4 c7c5", 200),                               // Queen's Gambit Accepted
        ("d2d4 f7f5 g2g3 g8f6 f1g2 e7e6 g1f3 d7d5 e1g1 f8d6", 150),                               // Dutch Leningrad/Stonewall

        // --- 1. c4 ENGLISH OPENING ---
        ("c2c4 e7e5 b1c3 g8f6 g1f3 b8c6 g2g3 f8b4 f1g2 e8g8", 400),                               // English Four Knights
        ("c2c4 c7c5 b1c3 b8c6 g2g3 g7g6 f1g2 f8g7 g1f3 e7e6", 300),                               // Symmetrical English
        ("c2c4 g8f6 b1c3 e7e6 e2e4 d7d5 e4e5 d5d4", 250),                                         // English Mikenas

        // --- 1. Nf3 RÃ‰TI & FLANK OPENINGS ---
        ("g1f3 d7d5 g2g3 g8f6 f1g2 c7c6 e1g1 c8f5 d2d3 e7e6", 350),                               // Réti Opening
        ("g1f3 g8f6 g2g3 g7g6 f1g2 f8g7 e1g1 e8g8 d2d3 d7d6", 300),                               // King's Indian Attack
    ];

    private static PolyGlotEntry[]? _cachedEntries;
    private static readonly object _lock = new();

    /// <summary>
    /// Generates and returns a fully sorted array of PolyGlotEntry structs derived from the canonical master lines.
    /// Execution takes less than 1 ms and is cached in memory.
    /// </summary>
    public static PolyGlotEntry[] GetDefaultEntries()
    {
        lock (_lock)
        {
            if (_cachedEntries != null)
                return _cachedEntries;

            Dictionary<(ulong Key, ushort Move), ushort> entryWeights = new();

            foreach (var (moveLine, baseWeight) in _lines)
            {
                Board board = Fen.Parse(Fen.StartPosition);
                string[] uciMoves = moveLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                foreach (string uci in uciMoves)
                {
                    ulong polyKey = PolyGlotZobrist.ComputeKey(in board);

                    MoveList legalMoves = default;
                    board.GenerateLegalMoves(ref legalMoves);

                    Move matched = Move.Null;
                    for (int i = 0; i < legalMoves.Count; i++)
                    {
                        if (legalMoves[i].ToUci().Equals(uci, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = legalMoves[i];
                            break;
                        }
                    }

                    if (matched.IsNull)
                        break;

                    ushort encodedPolyMove = EncodePolyGlotMove(matched);
                    var entryKey = (polyKey, encodedPolyMove);

                    if (entryWeights.TryGetValue(entryKey, out ushort existing))
                    {
                        entryWeights[entryKey] = (ushort)Math.Min(ushort.MaxValue, existing + baseWeight);
                    }
                    else
                    {
                        entryWeights[entryKey] = (ushort)baseWeight;
                    }

                    board.MakeMove(matched, out _);
                }
            }

            var list = new List<PolyGlotEntry>(entryWeights.Count);
            foreach (var kvp in entryWeights)
            {
                list.Add(new PolyGlotEntry(kvp.Key.Key, kvp.Key.Move, kvp.Value, 0));
            }

            // PolyGlot format requires entries to be sorted strictly by 64-bit key ascending
            list.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Move.CompareTo(b.Move));
            _cachedEntries = list.ToArray();
            return _cachedEntries;
        }
    }

    /// <summary>
    /// Writes out the default opening book to a .bin file on disk if not already present.
    /// </summary>
    public static bool EnsureBookFileExists(string filePath)
    {
        if (File.Exists(filePath))
            return true;

        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            PolyGlotEntry[] entries = GetDefaultEntries();
            byte[] buffer = new byte[entries.Length * 16];

            for (int i = 0; i < entries.Length; i++)
            {
                int offset = i * 16;
                BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), entries[i].Key);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 8, 2), entries[i].Move);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 10, 2), entries[i].Weight);
                BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 12, 4), entries[i].Learn);
            }

            File.WriteAllBytes(filePath, buffer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ushort EncodePolyGlotMove(Move move)
    {
        int fromFile = (byte)move.From & 7;
        int fromRank = (byte)move.From >> 3;
        int toFile = (byte)move.To & 7;
        int toRank = (byte)move.To >> 3;

        // PolyGlot castling destination encoding:
        // White: e1g1 (O-O) -> e1h1, e1c1 (O-O-O) -> e1a1
        // Black: e8g8 (O-O) -> e8h8, e8c8 (O-O-O) -> e8a8
        if (move.IsCastling)
        {
            if (move.From == Square.E1 && move.To == Square.G1) { toFile = 7; toRank = 0; }
            else if (move.From == Square.E1 && move.To == Square.C1) { toFile = 0; toRank = 0; }
            else if (move.From == Square.E8 && move.To == Square.G8) { toFile = 7; toRank = 7; }
            else if (move.From == Square.E8 && move.To == Square.C8) { toFile = 0; toRank = 7; }
        }

        int promoCode = move.PromotionPieceType switch
        {
            PieceType.Knight => 1,
            PieceType.Bishop => 2,
            PieceType.Rook => 3,
            PieceType.Queen => 4,
            _ => 0
        };

        return (ushort)(toFile | (toRank << 3) | (fromFile << 6) | (fromRank << 9) | (promoCode << 12));
    }
}
