using Anak.Core;
using Anak.Protocol.Analyze;
using Anak.Protocol.Bench;
using Anak.Protocol.Datagen;
using Anak.Protocol.Uci;

namespace Anak.Protocol;

/// <summary>
/// CLI Application entry point for Anak Chess Engine.
/// Routes between benchmark, perft diagnostics, and the asynchronous UCI protocol loop.
/// </summary>
public static class Program
{
    public const string Version = "1.0.0";
    public const string EngineName = $"Anak {Version}";
    public const string Author = "Anak Contributors";

    public static async Task Main(string[] args)
    {
        if (args.Length > 0)
        {
            string command = args[0].ToLowerInvariant();

            if (command is "-v" or "--version" or "version")
            {
                Console.WriteLine($"{EngineName} by {Author}");
                return;
            }

            if (command is "bench")
            {
                int depth = 10;
                int threads = 1;
                int hash = 64;

                if (args.Length > 1 && int.TryParse(args[1], out int d)) depth = d;
                if (args.Length > 2 && int.TryParse(args[2], out int t)) threads = t;
                if (args.Length > 3 && int.TryParse(args[3], out int h)) hash = h;

                BenchRunner.Run(depth, threads, hash, Console.WriteLine);
                return;
            }

            if (command is "--perft" or "perft")
            {
                int depth = 5;
                string fen = Fen.StartPosition;

                if (args.Length > 1 && int.TryParse(args[1], out int d)) depth = d;
                if (args.Length > 2)
                {
                    fen = string.Join(' ', args.Skip(2));
                }

                Board board = Fen.Parse(fen);
                Console.WriteLine($"Running Perft divide to depth {depth} on FEN: {fen}");
                PerftRunner.Divide(ref board, depth, Console.WriteLine);
                return;
            }

            if (command is "analyze")
            {
                string? fen = null;
                string? pgn = null;
                int depth = 10;
                int threads = 1;
                int hash = 64;

                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg is "--fen" or "-f")
                    {
                        List<string> fenParts = [];
                        while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            i++;
                            fenParts.Add(args[i]);
                        }
                        fen = string.Join(' ', fenParts);
                    }
                    else if (arg is "--pgn" or "-p")
                    {
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            i++;
                            pgn = args[i];
                        }
                    }
                    else if (arg is "--depth" or "-d")
                    {
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int d))
                        {
                            depth = d;
                            i++;
                        }
                    }
                    else if (arg is "--threads" or "-t")
                    {
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int t))
                        {
                            threads = t;
                            i++;
                        }
                    }
                    else if (arg is "--hash" or "-h")
                    {
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int h))
                        {
                            hash = h;
                            i++;
                        }
                    }
                    else if (!arg.StartsWith('-') && fen == null && pgn == null)
                    {
                        if (arg.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase))
                        {
                            pgn = arg;
                        }
                        else
                        {
                            List<string> fenParts = [arg];
                            while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                            {
                                i++;
                                fenParts.Add(args[i]);
                            }
                            fen = string.Join(' ', fenParts);
                        }
                    }
                }

                AnalyzeRunner.Run(fen, pgn, depth, threads, hash, Console.WriteLine);
                return;
            }

            if (command is "datagen")
            {
                var options = new DatagenOptions();

                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i].ToLowerInvariant();
                    if (arg is "--games" or "-g" && i + 1 < args.Length && int.TryParse(args[i + 1], out int g))
                    {
                        options.Games = g;
                        i++;
                    }
                    else if (arg is "--threads" or "-t" && i + 1 < args.Length && int.TryParse(args[i + 1], out int t))
                    {
                        options.Threads = t;
                        i++;
                    }
                    else if (arg is "--depth" or "-d" && i + 1 < args.Length && int.TryParse(args[i + 1], out int d))
                    {
                        options.Depth = d;
                        i++;
                    }
                    else if (arg is "--nodes" or "-n" && i + 1 < args.Length && int.TryParse(args[i + 1], out int n))
                    {
                        options.Nodes = n;
                        i++;
                    }
                    else if (arg is "--random-plies" or "-r" && i + 1 < args.Length && int.TryParse(args[i + 1], out int r))
                    {
                        options.RandomPlies = r;
                        i++;
                    }
                    else if (arg is "--output" or "-o" && i + 1 < args.Length)
                    {
                        options.OutputDir = args[i + 1];
                        i++;
                    }
                    else if (arg is "--syzygy" && i + 1 < args.Length)
                    {
                        options.SyzygyPath = args[i + 1];
                        i++;
                    }
                    else if (arg is "--epd")
                    {
                        options.ExportEpd = true;
                    }
                }

                DatagenRunner.Run(options, Console.WriteLine);
                return;
            }
        }

        // Default execution mode: asynchronous UCI protocol loop
        await UciHandler.RunAsync(Console.In, Console.Out);
    }
}
