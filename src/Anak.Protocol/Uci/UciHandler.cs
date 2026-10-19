using System.Text;
using Anak.Core;
using Anak.Protocol.Bench;
using Anak.Search;

namespace Anak.Protocol.Uci;

/// <summary>
/// Asynchronous UCI protocol loop, diagnostic command handler, and I/O coordinator.
/// </summary>
public sealed class UciHandler
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _writeLock = new();
    private readonly UciEngineAdapter _adapter;

    public UciEngineAdapter Adapter => _adapter;

    public UciHandler(TextReader input, TextWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _adapter = new UciEngineAdapter(SendLine);
    }

    /// <summary>
    /// Synchronized, thread-safe line emitter to standard output.
    /// </summary>
    public void SendLine(string line)
    {
        lock (_writeLock)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }

    /// <summary>
    /// Executes the asynchronous UCI loop until a quit command is encountered or the input stream closes.
    /// </summary>
    public static async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        var handler = new UciHandler(input, output);
        await handler.ProcessLoopAsync(cancellationToken);
    }

    /// <summary>
    /// Internal loop processing incoming UCI commands line by line.
    /// </summary>
    public async Task ProcessLoopAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? rawLine = await _input.ReadLineAsync(cancellationToken);
            if (rawLine == null)
            {
                // Input stream terminated (EOF / pipe closed)
                break;
            }

            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            bool shouldQuit = HandleCommand(line);
            if (shouldQuit) break;
        }

        // Clean up on exit
        _adapter.StopSearch();
    }

    /// <summary>
    /// Dispatches a single UCI or diagnostic command. Returns true if the session should quit.
    /// </summary>
    public bool HandleCommand(string line)
    {
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        string command = tokens[0].ToLowerInvariant();

        switch (command)
        {
            case "uci":
                HandleUci();
                break;

            case "isready":
                SendLine("readyok");
                break;

            case "setoption":
                HandleSetOption(tokens);
                break;

            case "ucinewgame":
                _adapter.NewGame();
                break;

            case "position":
                HandlePosition(tokens);
                break;

            case "go":
                HandleGo(tokens);
                break;

            case "stop":
                _adapter.StopSearch();
                break;

            case "ponderhit":
                _adapter.PonderHit();
                break;

            case "quit":
                return true;

            // Diagnostics and testing commands
            case "d" or "display":
                DisplayBoard();
                break;

            case "perft":
                HandlePerft(tokens);
                break;

            case "bench":
                HandleBench(tokens);
                break;

            case "eval":
                HandleEval();
                break;

            default:
                // Gracefully ignore unrecognized commands per UCI protocol
                break;
        }

        return false;
    }

    private void HandleUci()
    {
        SendLine("id name Anak 1.0.0");
        SendLine("id author Anak Contributors");

        if (_adapter.Evaluator is Anak.Data.Nnue.MultiTierNnueEvaluator nnue && nnue.IsActive)
        {
            SendLine($"info string NNUE active: [Open: {nnue.HasOpenModel}, Closed: {nnue.HasClosedModel}, Micro: {nnue.HasMicroModel}] ({nnue.NnueDirectory})");
        }
        else
        {
            SendLine("info string NNUE inactive: using classical heuristic fallback");
        }

        foreach (var decl in _adapter.Options.GetDeclarations())
        {
            SendLine(decl);
        }

        SendLine("uciok");
    }

    private void HandleSetOption(string[] tokens)
    {
        // Format: setoption name <Name> [value <Value>]
        int nameIdx = -1;
        int valueIdx = -1;

        for (int i = 1; i < tokens.Length; i++)
        {
            if (tokens[i].Equals("name", StringComparison.OrdinalIgnoreCase) && nameIdx == -1)
            {
                nameIdx = i + 1;
            }
            else if (tokens[i].Equals("value", StringComparison.OrdinalIgnoreCase) && valueIdx == -1)
            {
                valueIdx = i + 1;
            }
        }

        if (nameIdx == -1 || nameIdx >= tokens.Length) return;

        string name;
        string? value = null;

        if (valueIdx != -1 && valueIdx > nameIdx)
        {
            name = string.Join(' ', tokens[nameIdx..(valueIdx - 1)]);
            value = string.Join(' ', tokens[valueIdx..]);
        }
        else
        {
            name = string.Join(' ', tokens[nameIdx..]);
        }

        _adapter.Options.TrySetValue(name, value);
    }

    private void HandlePosition(string[] tokens)
    {
        // Format: position [startpos | fen <fen_tokens...>] [moves <move_tokens...>]
        if (tokens.Length < 2) return;

        string? fen = null;
        List<string> moves = [];

        int movesIdx = Array.FindIndex(tokens, 1, t => t.Equals("moves", StringComparison.OrdinalIgnoreCase));

        if (tokens[1].Equals("startpos", StringComparison.OrdinalIgnoreCase))
        {
            fen = "startpos";
        }
        else if (tokens[1].Equals("fen", StringComparison.OrdinalIgnoreCase))
        {
            int fenEnd = movesIdx != -1 ? movesIdx : tokens.Length;
            if (fenEnd > 2)
            {
                fen = string.Join(' ', tokens[2..fenEnd]);
            }
        }

        if (movesIdx != -1 && movesIdx + 1 < tokens.Length)
        {
            for (int i = movesIdx + 1; i < tokens.Length; i++)
            {
                moves.Add(tokens[i]);
            }
        }

        _adapter.SetPosition(fen, moves);
    }

    private void HandleGo(string[] tokens)
    {
        SearchLimits limits = new();

        for (int i = 1; i < tokens.Length; i++)
        {
            string token = tokens[i].ToLowerInvariant();

            if (token == "infinite")
            {
                limits.Infinite = true;
            }
            else if (token == "ponder")
            {
                // Ponder mode: runs until stop or ponderhit
                limits.Infinite = true;
            }
            else if (i + 1 < tokens.Length)
            {
                string next = tokens[i + 1];

                switch (token)
                {
                    case "wtime" when int.TryParse(next, out int wt):
                        limits.WhiteTime = wt;
                        i++;
                        break;
                    case "btime" when int.TryParse(next, out int bt):
                        limits.BlackTime = bt;
                        i++;
                        break;
                    case "winc" when int.TryParse(next, out int wi):
                        limits.WhiteIncrement = wi;
                        i++;
                        break;
                    case "binc" when int.TryParse(next, out int bi):
                        limits.BlackIncrement = bi;
                        i++;
                        break;
                    case "movestogo" when int.TryParse(next, out int mtg):
                        limits.MovesToGo = mtg;
                        i++;
                        break;
                    case "depth" when int.TryParse(next, out int d):
                        limits.Depth = d;
                        i++;
                        break;
                    case "nodes" when ulong.TryParse(next, out ulong n):
                        limits.Nodes = n;
                        i++;
                        break;
                    case "movetime" when int.TryParse(next, out int mt):
                        limits.MoveTime = mt;
                        i++;
                        break;
                }
            }
        }

        _adapter.StartSearch(limits);
    }

    private void DisplayBoard()
    {
        Board b = _adapter.CurrentBoard;
        var sb = new StringBuilder();
        sb.AppendLine("+---+---+---+---+---+---+---+---+");

        for (int rank = 7; rank >= 0; rank--)
        {
            sb.Append("|");
            for (int file = 0; file < 8; file++)
            {
                int sq = (rank << 3) | file;
                ulong mask = 1UL << sq;
                char c = '.';

                if ((b.ColorMaskWhite & mask) != 0)
                {
                    if ((b.PieceMaskPawn & mask) != 0) c = 'P';
                    else if ((b.PieceMaskKnight & mask) != 0) c = 'N';
                    else if ((b.PieceMaskBishop & mask) != 0) c = 'B';
                    else if ((b.PieceMaskRook & mask) != 0) c = 'R';
                    else if ((b.PieceMaskQueen & mask) != 0) c = 'Q';
                    else if ((b.PieceMaskKing & mask) != 0) c = 'K';
                }
                else if ((b.ColorMaskBlack & mask) != 0)
                {
                    if ((b.PieceMaskPawn & mask) != 0) c = 'p';
                    else if ((b.PieceMaskKnight & mask) != 0) c = 'n';
                    else if ((b.PieceMaskBishop & mask) != 0) c = 'b';
                    else if ((b.PieceMaskRook & mask) != 0) c = 'r';
                    else if ((b.PieceMaskQueen & mask) != 0) c = 'q';
                    else if ((b.PieceMaskKing & mask) != 0) c = 'k';
                }

                sb.Append($" {c} |");
            }
            sb.AppendLine($" {rank + 1}");
            sb.AppendLine("+---+---+---+---+---+---+---+---+");
        }

        sb.AppendLine("  a   b   c   d   e   f   g   h");
        sb.AppendLine($"Side to move : {b.SideToMove}");
        sb.AppendLine($"Castling     : {b.Castling}");
        sb.AppendLine($"En Passant   : {b.EpSquare.Name()}");
        sb.AppendLine($"Halfmove     : {b.HalfmoveClock}");
        sb.AppendLine($"Fullmove     : {b.FullmoveNumber}");
        sb.AppendLine($"Key          : 0x{b.ZobristKey:X16}");
        sb.AppendLine($"FEN          : {Fen.ToFen(in b)}");

        SendLine(sb.ToString().TrimEnd());
    }

    private void HandlePerft(string[] tokens)
    {
        int depth = 5;
        if (tokens.Length > 1 && int.TryParse(tokens[1], out int d))
        {
            depth = d;
        }

        Board boardCopy = _adapter.CurrentBoard;
        PerftRunner.Divide(ref boardCopy, depth, SendLine);
    }

    private void HandleBench(string[] tokens)
    {
        int depth = 10;
        int threads = 1;
        int hash = 64;

        if (tokens.Length > 1 && int.TryParse(tokens[1], out int d)) depth = d;
        if (tokens.Length > 2 && int.TryParse(tokens[2], out int t)) threads = t;
        if (tokens.Length > 3 && int.TryParse(tokens[3], out int h)) hash = h;

        BenchRunner.Run(depth, threads, hash, SendLine);
    }

    private void HandleEval()
    {
        Board b = _adapter.CurrentBoard;
        MoveList moves = default;
        b.GenerateLegalMoves(ref moves);

        IEvaluator evaluator = _adapter.Evaluator;
        Value val = evaluator.Evaluate(in b, moves.AsReadOnlySpan());

        if (evaluator is Anak.Data.Nnue.MultiTierNnueEvaluator nnue && nnue.IsActive)
        {
            SendLine($"info string Evaluator: Pure Multi-Tier NNUE (Open: {nnue.HasOpenModel}, Closed: {nnue.HasClosedModel}, Micro: {nnue.HasMicroModel})");
        }
        else
        {
            SendLine("info string Evaluator: Classical Heuristic Fallback");
        }

        SendLine($"Evaluation: {val.Raw} cp (from perspective of {b.SideToMove})");
    }
}
