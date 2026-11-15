using BenchmarkDotNet.Attributes;
using Anak.Core;
using Anak.Data.Nnue;

namespace Anak.Benchmarks;

[MemoryDiagnoser]
public unsafe class NnueBenchmark
{
    private NnueWeights _standardWeights = null!;
    private short[] _whiteAccStd = null!;
    private short[] _blackAccStd = null!;

    [GlobalSetup]
    public void Setup()
    {
        string networksDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "networks");
        if (!Directory.Exists(networksDir))
        {
            networksDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "networks"));
        }

        string anakPath = Path.Combine(networksDir, "anak.nnue");
        string openPath = Path.Combine(networksDir, "open.nnue");
        string loadPath = File.Exists(anakPath) ? anakPath : openPath;

        _standardWeights = NnueWeights.LoadFromFile(loadPath) ?? throw new FileNotFoundException(loadPath);

        int stdDim = _standardWeights.HiddenDim;
        _whiteAccStd = new short[stdDim];
        _blackAccStd = new short[stdDim];

        var rng = new Random(42);
        for (int i = 0; i < stdDim; i++)
        {
            _whiteAccStd[i] = (short)rng.Next(0, 150);
            _blackAccStd[i] = (short)rng.Next(0, 150);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _standardWeights?.Dispose();
    }

    [Benchmark]
    public int BenchmarkEvaluateStandard()
    {
        fixed (short* w = _whiteAccStd)
        fixed (short* b = _blackAccStd)
        {
            return NnueInference.EvaluateStandard(w, b, _standardWeights);
        }
    }

    [Benchmark]
    public int BenchmarkEvaluateMoEBlended()
    {
        fixed (short* w = _whiteAccStd)
        fixed (short* b = _blackAccStd)
        {
            return _standardWeights.IsMoE
                ? NnueInference.EvaluateMoEBlended(w, b, _standardWeights, Color.White, 1, 0, 128)
                : NnueInference.EvaluateStandard(w, b, _standardWeights);
        }
    }
}
