using BenchmarkDotNet.Attributes;
using Anak.Core;
using Anak.Data.Nnue;

namespace Anak.Benchmarks;

[MemoryDiagnoser]
public unsafe class NnueBenchmark
{
    private NnueWeights _weights = null!;
    private short[] _whiteAcc = null!;
    private short[] _blackAcc = null!;

    [GlobalSetup]
    public void Setup()
    {
        string networksDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "networks");
        if (!Directory.Exists(networksDir))
        {
            networksDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "networks"));
        }

        string anakPath = Path.Combine(networksDir, "anak.nnue");
        _weights = NnueWeights.LoadFromFile(anakPath) ?? throw new FileNotFoundException(anakPath);

        int dim = _weights.HiddenDim;
        _whiteAcc = new short[dim];
        _blackAcc = new short[dim];

        var rng = new Random(42);
        for (int i = 0; i < dim; i++)
        {
            _whiteAcc[i] = (short)rng.Next(0, 255);
            _blackAcc[i] = (short)rng.Next(0, 255);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _weights?.Dispose();

    [Benchmark]
    public int BenchmarkEvaluateV2()
    {
        fixed (short* w = _whiteAcc)
        fixed (short* b = _blackAcc)
        {
            return NnueInference.EvaluateV2(w, b, _weights, Color.White, 7);
        }
    }
}
