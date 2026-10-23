using System.Globalization;
using Anak.Core;
using Anak.Protocol.Uci;
using Anak.Search;
using File = System.IO.File;

namespace Anak.Protocol.Analyze;

/// <summary>
/// CLI Runner for position and PGN game analysis.
/// Executes search to a specified depth and formats evaluation and PV output.
/// </summary>
public static class AnalyzeRunner
{
    /// <summary>
    /// Executes analysis based on CLI arguments (FEN or PGN path, depth, threads, hash).
    /// </summary>
    public static void Run(
        string? fen = null,
        string? pgnPath = null,
        int depth = 10,
        int threads = 1,
        int hashMb = 64,
        Action<string>? output = null)
    {
        output ??= Console.WriteLine;

        if (!string.IsNullOrWhiteSpace(pgnPath))
        {
            AnalyzePgn(pgnPath, depth, threads, hashMb, output);
            return;
        }

        string fenToAnalyze = string.IsNullOrWhiteSpace(fen) ? Fen.StartPosition : fen;
        AnalyzeFen(fenToAnalyze, depth, threads, hashMb, output);
    }

    /// <summary>
    /// Analyzes a single position specified by FEN string.
    /// </summary>
    public static void AnalyzeFen(
        string fen,
        int depth,
        int threads,
        int hashMb,
        Action<string> output)
    {
        Board board;
        try
        {
            board = Fen.Parse(fen);
            if ((board.Kings & board.White) == 0 || (board.Kings & board.Black) == 0)
            {
                output($"Error: Invalid FEN '{fen}': both white and black kings must be present on the board.");
                return;
            }
        }
        catch (Exception ex)
        {
            output($"Error: Failed to parse FEN '{fen}': {ex.Message}");
            return;
        }

        AnalyzeBoard(in board, depth, threads, hashMb, fen, output);
    }

    /// <summary>
    /// Analyzes all games contained within a PGN file.
    /// </summary>
    public static void AnalyzePgn(
        string pgnPath,
        int depth,
        int threads,
        int hashMb,
        Action<string> output)
    {
        if (!File.Exists(pgnPath))
        {
            output($"Error: PGN file not found: {pgnPath}");
            return;
        }

        List<PgnGame> games;
        try
        {
            string pgnContent = File.ReadAllText(pgnPath);
            games = PgnParser.ParseText(pgnContent);
        }
        catch (Exception ex)
        {
            output($"Error reading PGN file '{pgnPath}': {ex.Message}");
            return;
        }

        if (games.Count == 0)
        {
            output($"Warning: No games parsed from PGN file: {pgnPath}");
            return;
        }

        output($"Loaded {games.Count} game(s) from PGN: {pgnPath}");

        for (int i = 0; i < games.Count; i++)
        {
            PgnGame game = games[i];
            output(string.Empty);
            output($"=== Game {i + 1}/{games.Count}: {game.White} vs {game.Black} ({game.Result}) ===");
            output($"Moves played: {game.Moves.Count} plies");
            Board finalBoard = game.FinalBoard;
            string finalFen = Fen.ToFen(in finalBoard);
            AnalyzeBoard(in finalBoard, depth, threads, hashMb, finalFen, output);
        }
    }

    /// <summary>
    /// Executes search on a board position and outputs formatted progress and summary.
    /// </summary>
    public static void AnalyzeBoard(
        in Board board,
        int depth,
        int threads,
        int hashMb,
        string fenString,
        Action<string> output)
    {
        output("=================================================");
        output(" Anak Chess Engine — Position Analysis");
        output($" FEN:    {fenString}");
        output($" Target Depth: {depth} | Threads: {threads} | Hash: {hashMb} MB");
        output("=================================================");

        IEvaluator evaluator = UciEngineAdapter.CreateEvaluator();
        ISearchEngine engine = new AlphaBetaEngine(evaluator, defaultHashMb: hashMb)
        {
            Threads = threads
        };

        SearchLimits limits = new()
        {
            Depth = depth,
            Nodes = 0
        };

        SearchStatistics lastStats = default;
        bool hasStats = false;

        engine.Search(
            in board,
            in limits,
            stats =>
            {
                lastStats = stats;
                hasStats = true;
                string scoreStr = FormatScore(stats);
                string pvStr = FormatPv(stats);
                output($"Depth {stats.Depth,2}/{stats.SelDepth,-2} | Eval: {scoreStr,14} | Nodes: {stats.Nodes,10:N0} | NPS: {stats.Nps,10:N0} | Time: {stats.TimeMs,5} ms | PV: {pvStr}");
            },
            CancellationToken.None);

        output("-------------------------------------------------");
        output(" Analysis Summary:");
        if (hasStats)
        {
            output($"   Best Move : {(lastStats.BestMove.IsNull ? "None" : lastStats.BestMove.ToUci())}");
            output($"   Evaluation: {FormatScore(lastStats)}");
            output($"   Depth     : {lastStats.Depth} (seldepth {lastStats.SelDepth})");
            output($"   Nodes     : {lastStats.Nodes:N0}");
            output($"   NPS       : {lastStats.Nps:N0}");
            output($"   Time      : {lastStats.TimeMs} ms");
            output($"   PV        : {FormatPv(lastStats)}");
        }
        else
        {
            output("   No search statistics reported.");
        }
        output("=================================================");
    }

    /// <summary>
    /// Formats centipawn or mate score for human-readable output.
    /// </summary>
    public static string FormatScore(SearchStatistics stats)
    {
        if (stats.MateIn != 0)
        {
            return stats.MateIn > 0 ? $"+M{stats.MateIn}" : $"-M{Math.Abs(stats.MateIn)}";
        }
        if (MathF.Abs(stats.RootScore) > 0.98f)
        {
            return stats.RootScore > 0 ? "+M1" : "-M1";
        }
        int cp = stats.ScoreCp;
        if (cp == 0 && stats.RootScore != 0.0f)
        {
            float clamped = Math.Clamp(stats.RootScore, -0.999f, 0.999f);
            cp = (int)(MathF.Atanh(clamped) * 400.0f);
        }
        float pawns = cp / 100.0f;
        return $"{(pawns >= 0 ? "+" : "")}{pawns.ToString("F2", CultureInfo.InvariantCulture)} ({cp:+0;-0;0} cp)";
    }

    /// <summary>
    /// Formats principal variation moves into space-separated UCI strings.
    /// </summary>
    public static string FormatPv(SearchStatistics stats)
    {
        if (stats.Pv != null && stats.Pv.Count > 0)
        {
            return string.Join(' ', stats.Pv.Select(m => m.ToUci()));
        }
        return stats.BestMove.IsNull ? "-" : stats.BestMove.ToUci();
    }
}
