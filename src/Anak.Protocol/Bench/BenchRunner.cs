using System.Diagnostics;
using Anak.Core;
using Anak.Protocol.Uci;
using Anak.Search;

namespace Anak.Protocol.Bench;

/// <summary>
/// Benchmark runner executing search over standard benchmark positions,
/// measuring search throughput, Nodes Per Second (NPS), and zero-GC memory allocation discipline.
/// </summary>
public static class BenchRunner
{
    public static readonly string[] BenchPositions =
    [
        // 1. Initial position
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        // 2. Kiwipete
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        // 3. Position 3
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        // 4. Position 4
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
        // 5. Position 5
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        // 6. Endgame
        "8/8/4k3/8/8/4K3/4P3/8 w - - 0 1"
    ];

    /// <summary>
    /// Executes the standard benchmark suite across all canonical positions.
    /// </summary>
    /// <param name="depth">Target search depth per position (default: 10).</param>
    /// <param name="threads">Number of search threads (default: 1).</param>
    /// <param name="hashMb">Transposition Table size in megabytes (default: 64).</param>
    /// <param name="output">Optional output delegate for reporting results.</param>
    public static void Run(int depth = 10, int threads = 1, int hashMb = 64, Action<string>? output = null)
    {
        output ??= Console.WriteLine;

        output("=================================================");
        output($" Anak Chess Engine — Benchmark Harness");
        output($" Target Depth: {depth} | Threads: {threads} | Hash: {hashMb} MB");
        output("=================================================");

        // Warm up and instantiate engine
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

        ulong totalNodes = 0;
        var totalStopwatch = new Stopwatch();

        // Perform GC collection before benchmark to isolate search memory allocations
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int gen0Start = GC.CollectionCount(0);
        int gen1Start = GC.CollectionCount(1);
        int gen2Start = GC.CollectionCount(2);

        totalStopwatch.Start();

        for (int i = 0; i < BenchPositions.Length; i++)
        {
            string fen = BenchPositions[i];
            Board board = Fen.Parse(fen);

            ulong posNodes = 0;
            var posStopwatch = Stopwatch.StartNew();

            engine.Search(
                in board,
                in limits,
                info =>
                {
                    if (info.Nodes > posNodes)
                    {
                        posNodes = info.Nodes;
                    }
                },
                CancellationToken.None);

            posStopwatch.Stop();
            long posMs = Math.Max(1, posStopwatch.ElapsedMilliseconds);
            ulong posNps = (posNodes * 1000UL) / (ulong)posMs;

            totalNodes += posNodes;
            output($"Position {i + 1}/{BenchPositions.Length}: {posNodes:N0} nodes | {posMs} ms | {posNps:N0} nps");
        }

        totalStopwatch.Stop();

        int gen0Delta = GC.CollectionCount(0) - gen0Start;
        int gen1Delta = GC.CollectionCount(1) - gen1Start;
        int gen2Delta = GC.CollectionCount(2) - gen2Start;

        long totalMs = Math.Max(1, totalStopwatch.ElapsedMilliseconds);
        ulong totalNps = (totalNodes * 1000UL) / (ulong)totalMs;

        output("=================================================");
        output($"Total Nodes: {totalNodes:N0}");
        output($"Total Time:  {totalMs} ms");
        output($"NPS:         {totalNps:N0}");
        output($"GC Gen0:     {gen0Delta}");
        output($"GC Gen1:     {gen1Delta}");
        output($"GC Gen2:     {gen2Delta}");
        output("=================================================");
        // Standard benchmark summary string expected by tournament harnesses
        output($"{totalNodes} nodes {totalNps} nps");
    }
}
