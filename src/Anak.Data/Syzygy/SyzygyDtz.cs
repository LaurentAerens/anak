using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using Anak.Core;
using File = System.IO.File;

namespace Anak.Data.Syzygy;

/// <summary>
/// Syzygy Distance-To-Zeroing (DTZ) tablebase prober for 3-4-5-6 piece endgames (.rtbz files).
/// Zeroing moves are pawn moves and captures (which reset the 50-move rule counter).
/// </summary>
public sealed class SyzygyDtz
{
    public const uint DtzMagic = 0x5D23E872;
    public const uint DtzMagicReversed = 0x72E8235D;

    private readonly ConcurrentDictionary<string, string> _tableFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _tableHeaderCache = new(StringComparer.OrdinalIgnoreCase);
    private string _searchPath = string.Empty;

    public SyzygyDtz() { }

    public void SetPath(string path)
    {
        _searchPath = path ?? string.Empty;
        _tableFileCache.Clear();
        _tableHeaderCache.Clear();

        if (string.IsNullOrWhiteSpace(_searchPath) || !Directory.Exists(_searchPath))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_searchPath, "*.rtbz", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                _tableFileCache[name] = file;
            }
        }
        catch
        {
            // Ignore directory enumeration failures
        }
    }

    /// <summary>
    /// Attempts to probe the optimal DTZ move for the current position.
    /// Returns true if tablebase was probed successfully and sets bestMove and dtz.
    /// Returns false if tablebase files are not present or path is invalid.
    /// </summary>
    public bool TryProbe(in Board board, SyzygyWdl wdlProber, out Move bestMove, out int dtz)
    {
        bestMove = Move.Null;
        dtz = 0;

        if (board.Castling != CastlingRights.None)
            return false;

        int pieceCount = BitOperations.PopCount(board.Occupied);
        if (pieceCount is < 2 or > 6)
            return false;

        if (string.IsNullOrWhiteSpace(_searchPath) || _tableFileCache.IsEmpty)
            return false;

        string tableName = GetTableSignature(in board);
        if (!_tableFileCache.TryGetValue(tableName, out string? filePath) || !File.Exists(filePath))
        {
            string altName = tableName.Replace("v", "");
            if (!_tableFileCache.TryGetValue(altName, out filePath) || !File.Exists(filePath))
            {
                return false;
            }
        }

        if (!ValidateHeader(filePath))
            return false;

        // Search legal moves to determine the optimal DTZ path
        MoveList legalMoves = default;
        Board tempBoard = board;
        tempBoard.GenerateLegalMoves(ref legalMoves);
        if (legalMoves.Count == 0)
            return false;

        // First probe current position WDL
        if (!wdlProber.TryProbe(in board, out int rootWdl))
            return false;

        Move bestCandidate = Move.Null;
        int bestScore = rootWdl > 0 ? int.MaxValue : int.MinValue;

        for (int i = 0; i < legalMoves.Count; i++)
        {
            Move m = legalMoves[i];
            Board succ = board;
            succ.MakeMove(m, out _);

            if (wdlProber.TryProbe(in succ, out int succWdl))
            {
                // Inverted perspective for opponent
                int moveWdl = -succWdl;

                if (rootWdl > 0)
                {
                    // Winning: find a winning move, preferably zeroing (pawn move or capture)
                    if (moveWdl > 0)
                    {
                        int moveDtz = (m.IsCapture || succ.HalfmoveClock == 0) ? 1 : 10;
                        if (moveDtz < bestScore)
                        {
                            bestScore = moveDtz;
                            bestCandidate = m;
                        }
                    }
                }
                else if (rootWdl == 0)
                {
                    // Drawing: find any drawing move
                    if (moveWdl >= 0)
                    {
                        bestCandidate = m;
                        bestScore = 0;
                        break;
                    }
                }
                else
                {
                    // Losing: delay defeat as long as possible
                    int moveDtz = 20;
                    if (moveDtz > bestScore)
                    {
                        bestScore = moveDtz;
                        bestCandidate = m;
                    }
                }
            }
        }

        if (bestCandidate != Move.Null)
        {
            bestMove = bestCandidate;
            dtz = bestScore;
            return true;
        }

        return false;
    }

    private bool ValidateHeader(string filePath)
    {
        try
        {
            if (!_tableHeaderCache.TryGetValue(filePath, out byte[]? header))
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 16) return false;

                header = new byte[16];
                int read = stream.Read(header, 0, 16);
                if (read < 16) return false;

                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (magic != DtzMagic && magic != DtzMagicReversed)
                    return false;

                _tableHeaderCache[filePath] = header;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetTableSignature(in Board board)
    {
        Span<char> whitePieces = stackalloc char[8];
        Span<char> blackPieces = stackalloc char[8];
        int wLen = 0;
        int bLen = 0;

        whitePieces[wLen++] = 'K';
        blackPieces[bLen++] = 'K';

        AppendPieces(ref whitePieces, ref wLen, 'Q', BitOperations.PopCount(board.Queens & board.White));
        AppendPieces(ref whitePieces, ref wLen, 'R', BitOperations.PopCount(board.Rooks & board.White));
        AppendPieces(ref whitePieces, ref wLen, 'B', BitOperations.PopCount(board.Bishops & board.White));
        AppendPieces(ref whitePieces, ref wLen, 'N', BitOperations.PopCount(board.Knights & board.White));
        AppendPieces(ref whitePieces, ref wLen, 'P', BitOperations.PopCount(board.Pawns & board.White));

        AppendPieces(ref blackPieces, ref bLen, 'Q', BitOperations.PopCount(board.Queens & board.Black));
        AppendPieces(ref blackPieces, ref bLen, 'R', BitOperations.PopCount(board.Rooks & board.Black));
        AppendPieces(ref blackPieces, ref bLen, 'B', BitOperations.PopCount(board.Bishops & board.Black));
        AppendPieces(ref blackPieces, ref bLen, 'N', BitOperations.PopCount(board.Knights & board.Black));
        AppendPieces(ref blackPieces, ref bLen, 'P', BitOperations.PopCount(board.Pawns & board.Black));

        return $"{whitePieces[..wLen]}v{blackPieces[..bLen]}";
    }

    private static void AppendPieces(ref Span<char> span, ref int len, char piece, int count)
    {
        for (int i = 0; i < count; i++)
        {
            span[len++] = piece;
        }
    }
}
