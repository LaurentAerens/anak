using Anak.Core;
using Anak.Data.Nnue;
using Xunit;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Anak.Tests.Data;

public sealed class NnueLoadedInferenceTests
{
    private static string GetNetworksDirectory()
    {
        // Try locating ../../../../../networks or ./networks
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            string candidate = Path.Combine(current, "networks");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "open.nnue")))
            {
                return candidate;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent == null) break;
            current = parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "networks");
    }

    [Fact]
    public void LoadedMultiTierEvaluator_EvaluatesWithZeroAllocations()
    {
        string netDir = GetNetworksDirectory();
        if (!Directory.Exists(netDir) || !File.Exists(Path.Combine(netDir, "open.nnue")))
        {
            return; // Skip if networks not generated
        }

        using var evaluator = new MultiTierNnueEvaluator(netDir);
        Assert.True(evaluator.IsActive);
        Assert.True(evaluator.HasOpenModel);
        Assert.True(evaluator.HasClosedModel);
        Assert.True(evaluator.HasMicroModel);

        Board board = Fen.Parse(Fen.StartPosition);

        // Warm up JIT and Tiered Compilation
        evaluator.SetRootTopology(in board);
        for (int i = 0; i < 100; i++)
        {
            _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
            _ = evaluator.EvaluateMicro(in board);
        }

        // Test Zero Allocations during NNUE evaluation
        long allocatedEval = -1;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                _ = evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            allocatedEval = bytesAfter - bytesBefore;
            if (allocatedEval == 0) break;
        }

        Assert.Equal(0, allocatedEval);

        // Test Zero Allocations during Micro-NNUE watchdog evaluation
        long allocatedMicro = -1;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                _ = evaluator.EvaluateMicro(in board);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            allocatedMicro = bytesAfter - bytesBefore;
            if (allocatedMicro == 0) break;
        }

        Assert.Equal(0, allocatedMicro);
    }

    [Fact]
    public void LoadedMultiTierEvaluator_MicroSensibleScores()
    {
        string netDir = GetNetworksDirectory();
        if (!Directory.Exists(netDir) || !File.Exists(Path.Combine(netDir, "micro.nnue")))
        {
            return;
        }

        using var evaluator = new MultiTierNnueEvaluator(netDir);
        Assert.True(evaluator.HasMicroModel);

        Board bStart = Fen.Parse(Fen.StartPosition);
        Board bPlusQ = Fen.Parse("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Board bMinusQ = Fen.Parse("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1");

        int evalStart = evaluator.EvaluateMicro(in bStart).Raw;
        int evalPlusQ = evaluator.EvaluateMicro(in bPlusQ).Raw;
        int evalMinusQ = evaluator.EvaluateMicro(in bMinusQ).Raw;

        Assert.InRange(evalStart, -50, 50);
        Assert.True(evalPlusQ > 500, $"Expected evalPlusQ > 500, got {evalPlusQ}");
        Assert.True(evalMinusQ < -500, $"Expected evalMinusQ < -500, got {evalMinusQ}");
    }

    [Fact]
    public void LoadedMultiTierEvaluator_MicroParityBetweenIncrementalAndFull()
    {
        string netDir = GetNetworksDirectory();
        if (!Directory.Exists(netDir) || !File.Exists(Path.Combine(netDir, "micro.nnue")))
        {
            return;
        }

        using var evaluator = new MultiTierNnueEvaluator(netDir);
        Board board = Fen.Parse(Fen.StartPosition);

        evaluator.InitSearch(in board);

        Move[] moves = new[]
        {
            new Move(Square.E2, Square.E4),
            new Move(Square.E7, Square.E5),
            new Move(Square.G1, Square.F3),
            new Move(Square.B8, Square.C6),
            new Move(Square.F1, Square.B5),
            new Move(Square.A7, Square.A6),
        };

        for (int i = 0; i < moves.Length; i++)
        {
            Move m = moves[i];
            Piece moved = board.GetPieceAt(m.From);
            board.MakeMove(m, out BoardState state);
            evaluator.PushMove(m, moved, state.CapturedPiece, in board);

            int incMicro = evaluator.EvaluateMicro(in board).Raw;

            // Full calculation from scratch
            using var freshEval = new MultiTierNnueEvaluator(netDir);
            int fullMicro = freshEval.EvaluateMicro(in board).Raw;

            Assert.Equal(fullMicro, incMicro);
        }
    }

    [Fact]
    public void BenchmarkNnueInferenceSpeed()
    {
        string netDir = GetNetworksDirectory();
        using var eval = new MultiTierNnueEvaluator(netDir);
        Board board = Fen.Parse(Fen.StartPosition);

        eval.InitSearch(in board);

        // Warmup
        for (int i = 0; i < 10000; i++)
        {
            eval.Evaluate(in board, ReadOnlySpan<Move>.Empty);
            eval.EvaluateMicro(in board);
        }

        const int iterations = 100000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            eval.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }
        sw.Stop();
        double nsPerStandard = (sw.Elapsed.TotalSeconds * 1e9) / iterations;

        sw.Restart();
        for (int i = 0; i < iterations; i++)
        {
            eval.EvaluateMicro(in board);
        }
        sw.Stop();
        double nsPerMicro = (sw.Elapsed.TotalSeconds * 1e9) / iterations;

        Console.WriteLine($"[BENCH] EvaluateStandard: {nsPerStandard:F1} ns ({1e9 / nsPerStandard:F0} eval/sec)");
        Console.WriteLine($"[BENCH] EvaluateMicro:    {nsPerMicro:F1} ns ({1e9 / nsPerMicro:F0} eval/sec)");
    }
}
