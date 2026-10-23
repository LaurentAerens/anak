using System.Diagnostics;

namespace Anak.Protocol.Datagen;

/// <summary>
/// Multi-threaded Datagen orchestrator and CLI driver.
/// Manages parallel workers, live progress telemetry, and graceful file flushing.
/// </summary>
public static class DatagenRunner
{
    public static void Run(DatagenOptions options, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        log ??= Console.WriteLine;

        log("===========================================================================");
        log("            ANAK CHESS ENGINE — TOPOLOGY STRATIFIED DATAGEN");
        log("===========================================================================");
        log($"Target Games:       {options.Games:N0}");
        log($"Concurrency:        {options.Threads} threads");
        log($"Search Limits:      Depth {options.Depth}, Max Nodes {options.Nodes:N0}");
        log($"Random Opening:     {options.RandomPlies} plies");
        log($"Output Directory:   {Path.GetFullPath(options.OutputDir)}");
        if (!string.IsNullOrEmpty(options.SyzygyPath))
        {
            log($"Syzygy Tablebases:  {options.SyzygyPath}");
        }
        log("===========================================================================");

        using var stratifiedWriter = new StratifiedBinpackWriter(options.OutputDir, options.MicroSampleRate, options.ExportEpd);

        int totalGamesTarget = options.Games;
        int completedGames = 0;
        int whiteWins = 0;
        int draws = 0;
        int blackWins = 0;

        var stopwatch = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler? cancelHandler = null;

        // Graceful Ctrl+C handling
        cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            log("\n[Datagen] Cancellation requested! Flushing in-flight records and finalizing binpacks...");
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            Parallel.For(0, options.Threads, new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Threads,
                CancellationToken = cts.Token
            },
            workerId =>
            {
                var worker = new DatagenWorker(workerId, options, stratifiedWriter);

                while (!cts.Token.IsCancellationRequested)
                {
                    int current = Interlocked.Increment(ref completedGames);
                    if (current > totalGamesTarget)
                    {
                        break;
                    }

                    float? wdl = worker.PlayOneGame(cts.Token);
                    if (wdl.HasValue)
                    {
                        if (wdl.Value > 0.75f) Interlocked.Increment(ref whiteWins);
                        else if (wdl.Value < 0.25f) Interlocked.Increment(ref blackWins);
                        else Interlocked.Increment(ref draws);
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            stratifiedWriter.Flush();
            stopwatch.Stop();
        }

        int totalDone = Math.Min(completedGames, totalGamesTarget);
        double elapsedSeconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double gamesPerSec = totalDone / elapsedSeconds;
        double posPerSec = stratifiedWriter.TotalPositions / elapsedSeconds;

        log("\n===========================================================================");
        log("                     DATAGEN GENERATION FINISHED");
        log("===========================================================================");
        log($"Games Completed:    {totalDone:N0} / {totalGamesTarget:N0} ({gamesPerSec:F1} games/s)");
        log($"Positions Created:  {stratifiedWriter.TotalPositions:N0} ({posPerSec:F1} pos/s)");
        log($"Game Outcomes:      {whiteWins} W / {draws} D / {blackWins} L");
        log($"Elapsed Time:       {stopwatch.Elapsed:hh\\:mm\\:ss\\.ff}");
        log("Dataset Stratification Breakdown:");
        log($"  - Open Pack:      {stratifiedWriter.OpenCount:N0} records");
        log($"  - Closed Pack:    {stratifiedWriter.ClosedCount:N0} records");
        log($"  - Endgame Pack:   {stratifiedWriter.EndgameCount:N0} records");
        log($"  - Queenless Pack: {stratifiedWriter.QueenlessCount:N0} records");
        log($"  - Volatility:     {stratifiedWriter.VolatilityCount:N0} records (Tactical Swings >= 150cp)");
        log($"  - Micro Pack:     {stratifiedWriter.MicroCount:N0} records (Validation Sample)");
        log("===========================================================================\n");
    }
}
