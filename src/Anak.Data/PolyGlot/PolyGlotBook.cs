using System.Buffers.Binary;
using Anak.Core;
using File = System.IO.File;

namespace Anak.Data.PolyGlot;

/// <summary>
/// PolyGlot opening book reader supporting fast binary search probing on .bin book files.
/// </summary>
public sealed class PolyGlotBook
{
    private PolyGlotEntry[] _entries = [];
    private readonly object _lock = new();

    public string BookFile { get; set; } = "book.bin";
    public bool OwnBook { get; set; } = true;
    public BookMode Mode { get; set; } = BookMode.WeightedRandom;
    public int EntryCount => _entries.Length;
    public bool IsLoaded => _entries.Length > 0;

    public PolyGlotBook() { }

    public PolyGlotBook(string? bookFile, bool ownBook = true)
    {
        BookFile = bookFile ?? "book.bin";
        OwnBook = ownBook;
        if (!string.IsNullOrWhiteSpace(bookFile))
        {
            Load(bookFile);
        }
    }

    /// <summary>
    /// Intelligently resolves the path to an opening book file across BaseDirectory and repository hierarchy.
    /// </summary>
    public static string? ResolveBookPath(string? bookFile)
    {
        if (string.IsNullOrWhiteSpace(bookFile))
        {
            bookFile = "book.bin";
        }

        if (Path.IsPathRooted(bookFile))
        {
            return File.Exists(bookFile) ? Path.GetFullPath(bookFile) : null;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string directCandidate = Path.Combine(baseDir, bookFile);
        if (File.Exists(directCandidate))
            return Path.GetFullPath(directCandidate);

        string assetsCandidate = Path.Combine(baseDir, "assets", bookFile);
        if (File.Exists(assetsCandidate))
            return Path.GetFullPath(assetsCandidate);

        // Search parent directories up to 7 levels
        string? parent = Directory.GetParent(baseDir)?.FullName;
        for (int i = 0; i < 7 && parent != null; i++)
        {
            string check = Path.Combine(parent, bookFile);
            if (File.Exists(check))
                return Path.GetFullPath(check);

            check = Path.Combine(parent, "assets", bookFile);
            if (File.Exists(check))
                return Path.GetFullPath(check);

            parent = Directory.GetParent(parent)?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Loads a PolyGlot binary (.bin) book file into memory.
    /// Returns true on success, false if file does not exist or format is invalid.
    /// </summary>
    public bool Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        string? resolved = ResolveBookPath(filePath);

        if (resolved == null || !File.Exists(resolved))
        {
            lock (_lock)
            {
                _entries = [];
                BookFile = filePath;
            }
            return false;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(resolved);
            if (bytes.Length < 16 || bytes.Length % 16 != 0)
            {
                lock (_lock)
                {
                    _entries = [];
                    BookFile = filePath;
                }
                return false;
            }

            int count = bytes.Length / 16;
            PolyGlotEntry[] entries = new PolyGlotEntry[count];

            for (int i = 0; i < count; i++)
            {
                int offset = i * 16;
                ulong key = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset, 8));
                ushort move = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 8, 2));
                ushort weight = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 10, 2));
                uint learn = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 12, 4));

                entries[i] = new PolyGlotEntry(key, move, weight, learn);
            }

            lock (_lock)
            {
                _entries = entries;
                BookFile = resolved;
            }
            return true;
        }
        catch
        {
            lock (_lock)
            {
                _entries = [];
                BookFile = filePath;
            }
            return false;
        }
    }

    /// <summary>
    /// Loads the built-in master opening book into memory.
    /// Persists book.bin to the application base directory if not already present.
    /// </summary>
    public bool LoadDefaultBook()
    {
        lock (_lock)
        {
            _entries = DefaultOpeningBook.GetDefaultEntries();
            BookFile = "book.bin";
        }

        try
        {
            string targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "book.bin");
            DefaultOpeningBook.EnsureBookFileExists(targetPath);
        }
        catch
        {
            // Non-critical if directory is read-only
        }

        return true;
    }

    /// <summary>
    /// Attempts to load the specified book file from disk; if missing or invalid, falls back to the built-in master opening book.
    /// </summary>
    public bool LoadOrDefault(string? bookFile = "book.bin")
    {
        if (!string.IsNullOrWhiteSpace(bookFile) && Load(bookFile))
        {
            return true;
        }

        return LoadDefaultBook();
    }

    /// <summary>
    /// Probes the book for a move matching the current board state using the configured BookMode.
    /// </summary>
    public bool TryGetMove(in Board board, out Move move)
    {
        return TryGetMove(in board, out move, Mode);
    }

    /// <summary>
    /// Probes the book for a move matching the current board state.
    /// If pickBest is true, selects the move with the highest weight;
    /// otherwise performs weighted random selection.
    /// </summary>
    public bool TryGetMove(in Board board, out Move move, bool pickBest)
    {
        return TryGetMove(in board, out move, pickBest ? BookMode.BestMove : BookMode.WeightedRandom);
    }

    /// <summary>
    /// Probes the book for a move matching the current board state with an explicit BookMode strategy.
    /// Returns true if a valid legal move was found.
    /// </summary>
    public bool TryGetMove(in Board board, out Move move, BookMode mode)
    {
        move = Move.Null;
        if (!OwnBook) return false;

        PolyGlotEntry[] entries;
        lock (_lock)
        {
            entries = _entries;
        }

        if (entries.Length == 0) return false;

        ulong key = PolyGlotZobrist.ComputeKey(in board);

        // Binary search for the first entry matching key
        int left = 0;
        int right = entries.Length - 1;
        int firstIndex = -1;

        while (left <= right)
        {
            int mid = left + ((right - left) >> 1);
            if (entries[mid].Key >= key)
            {
                if (entries[mid].Key == key)
                    firstIndex = mid;
                right = mid - 1;
            }
            else
            {
                left = mid + 1;
            }
        }

        if (firstIndex < 0) return false;

        // Generate legal moves to validate candidate moves
        MoveList legalMoves = default;
        board.GenerateLegalMoves(ref legalMoves);
        if (legalMoves.Count == 0) return false;

        Span<Move> candidateMoves = stackalloc Move[64];
        Span<ushort> candidateWeights = stackalloc ushort[64];
        int candidateCount = 0;
        int totalWeight = 0;

        for (int i = firstIndex; i < entries.Length && entries[i].Key == key; i++)
        {
            ref readonly var entry = ref entries[i];
            if (entry.Weight == 0) continue;

            if (TryMatchMove(entry, legalMoves, out Move matchedMove))
            {
                if (candidateCount < candidateMoves.Length)
                {
                    candidateMoves[candidateCount] = matchedMove;
                    candidateWeights[candidateCount] = entry.Weight;
                    totalWeight += entry.Weight;
                    candidateCount++;
                }
            }
        }

        if (candidateCount == 0) return false;

        if (mode == BookMode.BestMove || totalWeight == 0)
        {
            int bestIdx = 0;
            ushort bestWeight = candidateWeights[0];
            for (int i = 1; i < candidateCount; i++)
            {
                if (candidateWeights[i] > bestWeight)
                {
                    bestWeight = candidateWeights[i];
                    bestIdx = i;
                }
            }
            move = candidateMoves[bestIdx];
            return true;
        }

        if (mode == BookMode.UniformRandom)
        {
            move = candidateMoves[Random.Shared.Next(candidateCount)];
            return true;
        }

        // WeightedRandom: sample proportional to weight
        int roll = Random.Shared.Next(totalWeight);
        int currentWeight = 0;
        for (int i = 0; i < candidateCount; i++)
        {
            currentWeight += candidateWeights[i];
            if (roll < currentWeight)
            {
                move = candidateMoves[i];
                return true;
            }
        }

        move = candidateMoves[0];
        return true;
    }

    private static bool TryMatchMove(in PolyGlotEntry entry, MoveList legalMoves, out Move matched)
    {
        Square from = entry.FromSquare;
        Square to = entry.ToSquare;
        int promo = entry.PromotionPiece;

        PieceType promoType = promo switch
        {
            1 => PieceType.Knight,
            2 => PieceType.Bishop,
            3 => PieceType.Rook,
            4 => PieceType.Queen,
            _ => PieceType.None
        };

        // PolyGlot castling notation:
        // White: e1h1 (O-O) -> e1g1, e1a1 (O-O-O) -> e1c1
        // Black: e8h8 (O-O) -> e8g8, e8a8 (O-O-O) -> e8c8
        bool isCastlingPoly = (from == Square.E1 && (to == Square.H1 || to == Square.A1)) ||
                             (from == Square.E8 && (to == Square.H8 || to == Square.A8));

        for (int i = 0; i < legalMoves.Count; i++)
        {
            Move legal = legalMoves[i];
            if (legal.From != from) continue;

            if (isCastlingPoly && legal.IsCastling)
            {
                if (from == Square.E1 && to == Square.H1 && legal.To == Square.G1) { matched = legal; return true; }
                if (from == Square.E1 && to == Square.A1 && legal.To == Square.C1) { matched = legal; return true; }
                if (from == Square.E8 && to == Square.H8 && legal.To == Square.G8) { matched = legal; return true; }
                if (from == Square.E8 && to == Square.A8 && legal.To == Square.C8) { matched = legal; return true; }
            }

            if (legal.To == to)
            {
                if (promoType != PieceType.None)
                {
                    if (legal.PromotionPieceType == promoType)
                    {
                        matched = legal;
                        return true;
                    }
                }
                else
                {
                    matched = legal;
                    return true;
                }
            }
        }

        matched = Move.Null;
        return false;
    }
}
