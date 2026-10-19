using Anak.Core;
using Anak.Protocol.Bench;
using Anak.Protocol.Uci;

namespace Anak.Protocol;

/// <summary>
/// CLI Application entry point for Anak Chess Engine.
/// Routes between benchmark, perft diagnostics, and the asynchronous UCI protocol loop.
/// </summary>
public static class Program
{
    public const string Version = "0.0.1";
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
        }

        // Default execution mode: asynchronous UCI protocol loop
        await UciHandler.RunAsync(Console.In, Console.Out);
    }
}
