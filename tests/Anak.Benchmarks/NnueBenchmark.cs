using BenchmarkDotNet.Attributes;
using Anak.Core;
using Anak.Data.Nnue;

namespace Anak.Benchmarks;

[MemoryDiagnoser]
public unsafe class NnueBenchmark
{
    private NnueWeights _standardWeights = null!;
    private NnueWeights _microWeights = null!;
    private short[] _whiteAccStd = null!;
    private short[] _blackAccStd = null!;
    private short[] _whiteAccMicro = null!;
    private short[] _blackAccMicro = null!;

    [GlobalSetup]
    public void Setup()
    {
        string networksDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "networks");
        if (!Directory.Exists(networksDir))
        {
            networksDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "networks"));
        }

        string openPath = Path.Combine(networksDir, "open.nnue");
        string microPath = Path.Combine(networksDir, "micro.nnue");

        _standardWeights = NnueWeights.LoadFromFile(openPath) ?? throw new FileNotFoundException(openPath);
        _microWeights = NnueWeights.LoadFromFile(microPath) ?? throw new FileNotFoundException(microPath);

        int stdDim = _standardWeights.HiddenDim;
        _whiteAccStd = new short[stdDim];
        _blackAccStd = new short[stdDim];
        _whiteAccMicro = new short[16];
        _blackAccMicro = new short[16];

        var rng = new Random(42);
        for (int i = 0; i < stdDim; i++)
        {
            _whiteAccStd[i] = (short)rng.Next(0, 150);
            _blackAccStd[i] = (short)rng.Next(0, 150);
        }
        for (int i = 0; i < 16; i++)
        {
            _whiteAccMicro[i] = (short)rng.Next(0, 150);
            _blackAccMicro[i] = (short)rng.Next(0, 150);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _standardWeights?.Dispose();
        _microWeights?.Dispose();
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
    public int BenchmarkEvaluateMicro()
    {
        fixed (short* w = _whiteAccMicro)
        fixed (short* b = _blackAccMicro)
        {
            return NnueInference.EvaluateMicro(w, b, _microWeights);
        }
    }
}
