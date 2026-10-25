using System.Numerics;
using System.Text.RegularExpressions;
using Anak.Core;

namespace Anak.Data.Syzygy;

/// <summary>
/// Raised internally when a required table is not available for the probed position.
/// </summary>
internal sealed class SyzygyMissingTableException(string message) : Exception(message);

/// <summary>
/// Managed Syzygy endgame tablebase prober (WDL + DTZ) for standard chess.
/// The supported piece count is not hard-coded: it is derived from the table files found in the configured
/// directory, so the shipped 3-4 piece set works out of the box and 5-6 (or 7) piece sets work when added.
/// Probing a position requires the table for its own material plus every table reachable by captures and
/// promotions; if any of them is missing the probe fails gracefully and the caller falls back to normal search.
/// </summary>
public sealed partial class SyzygyProber : IDisposable
{
    [GeneratedRegex(@"^[KQRBNP]+v[KQRBNP]+$")]
    private static partial Regex TableNameRegex();

    private readonly Dictionary<ulong, SyzygyWdlTable> _wdl = [];
    private readonly Dictionary<ulong, SyzygyDtzTable> _dtz = [];
    private readonly List<IDisposable> _owned = [];

    /// <summary>Largest piece count among the loaded WDL tables (0 when none are loaded).</summary>
    public int MaxPieces { get; private set; }

    /// <summary>Directory the tables were loaded from.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>
    /// Discovers all <c>.rtbw</c> / <c>.rtbz</c> files in <paramref name="path"/>. Files are opened lazily on first probe.
    /// </summary>
    public void SetPath(string path)
    {
        Clear();
        Path = path ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Path) || !Directory.Exists(Path)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(Path, "*.rtb?", SearchOption.TopDirectoryOnly))
            {
                string ext = System.IO.Path.GetExtension(file);
                bool isWdl = ext.Equals(".rtbw", StringComparison.OrdinalIgnoreCase);
                bool isDtz = ext.Equals(".rtbz", StringComparison.OrdinalIgnoreCase);
                if (!isWdl && !isDtz) continue;

                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (!IsTableName(name)) continue;

                if (isWdl)
                {
                    var table = new SyzygyWdlTable(file);
                    Register(_wdl, table);
                    MaxPieces = Math.Max(MaxPieces, name.Length - 1);
                }
                else
                {
                    Register(_dtz, new SyzygyDtzTable(file));
                }
            }
        }
        catch (IOException)
        {
            // Unreadable directory: keep whatever was discovered so far.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Register<T>(Dictionary<ulong, T> map, T table) where T : SyzygyTable
    {
        map[table.Key] = table;
        map[table.MirroredKey] = table;
        _owned.Add(table);
    }

    private static bool IsTableName(string name) =>
        TableNameRegex().IsMatch(name)
        && name != "KvK"
        && name.StartsWith('K')
        && name.Contains("vK", StringComparison.Ordinal)
        && SyzygyKeys.NormalizeName(name, mirror: false) == name;

    private void Clear()
    {
        foreach (IDisposable d in _owned) d.Dispose();
        _owned.Clear();
        _wdl.Clear();
        _dtz.Clear();
        MaxPieces = 0;
        Path = string.Empty;
    }

    public void Dispose() => Clear();

    // ------------------------------------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// WDL from the side to move: 2 win, 1 cursed win, 0 draw, -1 blessed loss, -2 loss.
    /// </summary>
    public bool TryProbeWdl(in Board board, out int wdl)
    {
        wdl = 0;
        if (!CanProbe(in board)) return false;

        Board work = board;
        try
        {
            wdl = ProbeWdl(ref work);
            return true;
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// DTZ50'' value from the side to move (positive when winning, negative when losing, 0 for a draw).
    /// </summary>
    public bool TryProbeDtz(in Board board, out int dtz)
    {
        dtz = 0;
        if (!CanProbe(in board)) return false;

        Board work = board;
        try
        {
            dtz = ProbeDtz(ref work);
            return true;
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Probes the root position and selects the tablebase-best move: for wins, the move that keeps the win and
    /// zeroes/mates fastest; for draws, a drawing move; for losses, the most stubborn defence.
    /// <paramref name="dtz"/> is the DTZ of the root position itself.
    /// </summary>
    public bool TryProbeRoot(in Board board, out Move bestMove, out int dtz)
    {
        bestMove = Move.Null;
        dtz = 0;
        if (!CanProbe(in board)) return false;

        Board work = board;
        try
        {
            dtz = ProbeDtz(ref work);

            MoveList moves = default;
            work.GenerateLegalMoves(ref moves);
            if (moves.Count == 0) return false;

            int bestWdl = int.MinValue;
            int bestRank = 0;
            Move chosen = Move.Null;

            for (int i = 0; i < moves.Count; i++)
            {
                Move m = moves[i];
                bool zeroing = m.IsCapture || IsPawnMove(in work, m);

                work.MakeMove(m, out BoardState state);

                int ourWdl;
                int rank;
                MoveList replies = default;
                work.GenerateLegalMoves(ref replies);
                if (replies.Count == 0)
                {
                    // Checkmate (we win immediately) or stalemate (draw).
                    ourWdl = work.IsInCheck() ? 2 : 0;
                    rank = 0;
                }
                else
                {
                    ourWdl = -ProbeWdl(ref work);

                    // Wins: lower rank = faster progress (zeroing moves count as 1).
                    // Losses: higher rank = longer resistance. Draws are all equivalent.
                    rank = ourWdl == 0 ? 0 : (zeroing ? 1 : 1 + Math.Abs(ProbeDtz(ref work)));
                }

                work.UnmakeMove(m, in state);

                bool better =
                    ourWdl > bestWdl ||
                    (ourWdl == bestWdl && ((ourWdl > 0 && rank < bestRank) || (ourWdl < 0 && rank > bestRank)));

                if (better)
                {
                    bestWdl = ourWdl;
                    bestRank = rank;
                    chosen = m;
                }
            }

            bestMove = chosen;
            return !chosen.IsNull;
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            bestMove = Move.Null;
            return false;
        }
    }

    private static bool IsProbeFailure(Exception ex) =>
        ex is SyzygyMissingTableException or InvalidDataException or IOException or IndexOutOfRangeException;

    private bool CanProbe(in Board board)
    {
        if (MaxPieces == 0) return false;
        if (board.Castling != CastlingRights.None) return false;

        int pieces = BitOperations.PopCount(board.Occupied);
        if (pieces < 2 || pieces > MaxPieces) return false;

        return BitOperations.PopCount(board.Kings & board.White) == 1
            && BitOperations.PopCount(board.Kings & board.Black) == 1;
    }

    // ------------------------------------------------------------------------------------------------
    // WDL
    // ------------------------------------------------------------------------------------------------

    private int ProbeWdlTable(in Board board)
    {
        if (board.Kings == board.Occupied) return 0;

        ulong key = SyzygyKeys.FromBoard(in board);
        if (!_wdl.TryGetValue(key, out SyzygyWdlTable? table))
        {
            throw new SyzygyMissingTableException("Missing WDL table for position");
        }
        return table.Probe(in board);
    }

    /// <summary>
    /// Alpha-beta over captures so that the stored (post-capture) WDL values stay valid.
    /// <paramref name="success"/> is 2 when a capture decided the result, 1 otherwise.
    /// </summary>
    private int ProbeAb(ref Board board, int alpha, int beta, out int success)
    {
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (!m.IsCapture || m.IsEnPassant) continue;

            board.MakeMove(m, out BoardState state);
            int v = -ProbeAb(ref board, -beta, -alpha, out _);
            board.UnmakeMove(m, in state);

            if (v > alpha)
            {
                if (v >= beta)
                {
                    success = 2;
                    return v;
                }
                alpha = v;
            }
        }

        int tableValue = ProbeWdlTable(in board);
        if (alpha >= tableValue)
        {
            success = 1 + (alpha > 0 ? 1 : 0);
            return alpha;
        }

        success = 1;
        return tableValue;
    }

    private int ProbeWdl(ref Board board)
    {
        int v = ProbeAb(ref board, -2, 2, out _);

        if (board.EpSquare == Square.None) return v;

        int v1 = -3;
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (!m.IsEnPassant) continue;

            board.MakeMove(m, out BoardState state);
            int v0 = -ProbeAb(ref board, -2, 2, out _);
            board.UnmakeMove(m, in state);

            if (v0 > v1) v1 = v0;
        }

        if (v1 > -3)
        {
            if (v1 >= v)
            {
                v = v1;
            }
            else if (v == 0 && AllMovesAreEnPassant(in moves))
            {
                // Forced to play the losing en-passant capture.
                v = v1;
            }
        }

        return v;
    }

    private static bool AllMovesAreEnPassant(in MoveList moves)
    {
        for (int i = 0; i < moves.Count; i++)
        {
            if (!moves[i].IsEnPassant) return false;
        }
        return moves.Count > 0;
    }

    // ------------------------------------------------------------------------------------------------
    // DTZ
    // ------------------------------------------------------------------------------------------------

    private static int DtzBeforeZeroing(int wdl) => Math.Sign(wdl) * (Math.Abs(wdl) == 2 ? 1 : 101);

    private static bool IsPawnMove(in Board board, Move m) => (board.Pawns & (1UL << (int)m.From)) != 0;

    private bool ProbeDtzTable(in Board board, int wdl, out int dtz)
    {
        ulong key = SyzygyKeys.FromBoard(in board);
        if (!_dtz.TryGetValue(key, out SyzygyDtzTable? table))
        {
            throw new SyzygyMissingTableException("Missing DTZ table for position");
        }
        return table.Probe(in board, wdl, out dtz);
    }

    private static bool IsCheckmate(ref Board board)
    {
        if (!board.IsInCheck()) return false;
        MoveList replies = default;
        board.GenerateLegalMoves(ref replies);
        return replies.Count == 0;
    }

    private int ProbeDtzNoEp(ref Board board)
    {
        int wdl = ProbeAb(ref board, -2, 2, out int success);

        if (wdl == 0) return 0;

        ulong ours = board.GetPieces(board.SideToMove);
        if (success == 2 || (ours & ~board.Pawns) == 0)
        {
            return DtzBeforeZeroing(wdl);
        }

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        if (wdl > 0)
        {
            // A winning non-capturing pawn move zeroes the counter right away.
            for (int i = 0; i < moves.Count; i++)
            {
                Move m = moves[i];
                if (!IsPawnMove(in board, m) || m.IsCapture) continue;

                board.MakeMove(m, out BoardState state);
                int v = -ProbeWdl(ref board);
                board.UnmakeMove(m, in state);

                if (v == wdl) return v == 2 ? 1 : 101;
            }
        }

        if (ProbeDtzTable(in board, wdl, out int tableDtz))
        {
            return DtzBeforeZeroing(wdl) + (wdl > 0 ? tableDtz : -tableDtz);
        }

        if (wdl > 0)
        {
            int best = 0xffff;

            for (int i = 0; i < moves.Count; i++)
            {
                Move m = moves[i];
                if (IsPawnMove(in board, m) || m.IsCapture) continue;

                board.MakeMove(m, out BoardState state);
                int v = -ProbeDtz(ref board);

                if (v == 1 && IsCheckmate(ref board))
                {
                    best = 1;
                }
                else if (v > 0 && v + 1 < best)
                {
                    best = v + 1;
                }

                board.UnmakeMove(m, in state);
            }

            return best;
        }
        else
        {
            int best = -1;

            for (int i = 0; i < moves.Count; i++)
            {
                Move m = moves[i];
                bool zeroing = m.IsCapture || IsPawnMove(in board, m);

                board.MakeMove(m, out BoardState state);

                int v;
                if (zeroing)
                {
                    if (wdl == -2)
                    {
                        v = -1;
                    }
                    else
                    {
                        v = ProbeAb(ref board, 1, 2, out _);
                        v = v == 2 ? 0 : -101;
                    }
                }
                else
                {
                    v = -ProbeDtz(ref board) - 1;
                }

                board.UnmakeMove(m, in state);

                if (v < best) best = v;
            }

            return best;
        }
    }

    private int ProbeDtz(ref Board board)
    {
        int v = ProbeDtzNoEp(ref board);

        if (board.EpSquare == Square.None) return v;

        int v1 = -3;
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (!m.IsEnPassant) continue;

            board.MakeMove(m, out BoardState state);
            int v0 = -ProbeAb(ref board, -2, 2, out _);
            board.UnmakeMove(m, in state);

            if (v0 > v1) v1 = v0;
        }

        if (v1 > -3)
        {
            v1 = SyzygyConstants.WdlToDtz[v1 + 2];

            if (v < -100)
            {
                if (v1 >= 0) v = v1;
            }
            else if (v < 0)
            {
                if (v1 >= 0 || v1 < -100) v = v1;
            }
            else if (v > 100)
            {
                if (v1 > 0) v = v1;
            }
            else if (v > 0)
            {
                if (v1 == 1) v = v1;
            }
            else if (v1 >= 0)
            {
                v = v1;
            }
            else if (AllMovesAreEnPassant(in moves))
            {
                v = v1;
            }
        }

        return v;
    }
}
