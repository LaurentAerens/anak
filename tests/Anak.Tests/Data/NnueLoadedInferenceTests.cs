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
}
