using BenchmarkDotNet.Attributes;
using Anak.Core;

namespace Anak.Benchmarks;

[MemoryDiagnoser]
public class MoveGenBenchmark
{
    private Board _startPos;
    private Board _kiwipete;

    public const string KiwipeteFen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    [GlobalSetup]
    public void Setup()
    {
        _startPos = Fen.Parse(Fen.StartPosition);
        _kiwipete = Fen.Parse(KiwipeteFen);
    }

    [Benchmark]
    public int GenerateStartPosLegalMoves()
    {
        MoveList list = default;
        MoveGenerator.GenerateLegalMoves(in _startPos, ref list);
        return list.Count;
    }

    [Benchmark]
    public int GenerateKiwipeteLegalMoves()
    {
        MoveList list = default;
        MoveGenerator.GenerateLegalMoves(in _kiwipete, ref list);
        return list.Count;
    }

    [Benchmark]
    public ulong PerftStartPosDepth4()
    {
        Board board = _startPos;
        return Anak.Protocol.Bench.PerftRunner.Perft(ref board, 4);
    }

    [Benchmark]
    public ulong PerftKiwipeteDepth3()
    {
        Board board = _kiwipete;
        return Anak.Protocol.Bench.PerftRunner.Perft(ref board, 3);
    }
}
