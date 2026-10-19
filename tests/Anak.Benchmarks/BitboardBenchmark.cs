using BenchmarkDotNet.Attributes;
using Anak.Core;

namespace Anak.Benchmarks;

[MemoryDiagnoser]
public class BitboardBenchmark
{
    private ulong[] _occupancies = null!;
    private Square[] _squares = null!;

    [GlobalSetup]
    public void Setup()
    {
        _occupancies = new ulong[64];
        _squares = new Square[64];
        var rng = new Random(42);

        for (int i = 0; i < 64; i++)
        {
            _squares[i] = (Square)i;
            byte[] bytes = new byte[8];
            rng.NextBytes(bytes);
            _occupancies[i] = BitConverter.ToUInt64(bytes, 0);
        }
    }

    [Benchmark]
    public ulong BenchmarkBishopAttacks()
    {
        ulong sum = 0;
        for (int i = 0; i < 64; i++)
        {
            sum ^= MagicBitboards.GetBishopAttacks(_squares[i], _occupancies[i]);
        }
        return sum;
    }

    [Benchmark]
    public ulong BenchmarkRookAttacks()
    {
        ulong sum = 0;
        for (int i = 0; i < 64; i++)
        {
            sum ^= MagicBitboards.GetRookAttacks(_squares[i], _occupancies[i]);
        }
        return sum;
    }
}
