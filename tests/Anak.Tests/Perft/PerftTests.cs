using Anak.Core;
using Xunit;

namespace Anak.Tests.Perft;

public class PerftTests
{
    private const string StartPos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    private const string Position3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    private const string Position4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    private const string Position5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";

    [Theory]
    [InlineData(1, 20UL)]
    [InlineData(2, 400UL)]
    [InlineData(3, 8902UL)]
    [InlineData(4, 197281UL)]
    [InlineData(5, 4865609UL)]
    public void Position1_StartPos_ExactCounts(int depth, ulong expected)
    {
        var board = Fen.Parse(StartPos);
        ulong actual = PerftRunner.Perft(ref board, depth);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 48UL)]
    [InlineData(2, 2039UL)]
    [InlineData(3, 97862UL)]
    [InlineData(4, 4085603UL)]
    public void Position2_Kiwipete_ExactCounts(int depth, ulong expected)
    {
        var board = Fen.Parse(Kiwipete);
        ulong actual = PerftRunner.Perft(ref board, depth);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 14UL)]
    [InlineData(2, 191UL)]
    [InlineData(3, 2812UL)]
    [InlineData(4, 43238UL)]
    [InlineData(5, 674624UL)]
    public void Position3_ExactCounts(int depth, ulong expected)
    {
        var board = Fen.Parse(Position3);
        ulong actual = PerftRunner.Perft(ref board, depth);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 6UL)]
    [InlineData(2, 264UL)]
    [InlineData(3, 9467UL)]
    [InlineData(4, 422333UL)]
    [InlineData(5, 15833292UL)]
    public void Position4_ExactCounts(int depth, ulong expected)
    {
        var board = Fen.Parse(Position4);
        ulong actual = PerftRunner.Perft(ref board, depth);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 44UL)]
    [InlineData(2, 1486UL)]
    [InlineData(3, 62379UL)]
    [InlineData(4, 2103487UL)]
    public void Position5_ExactCounts(int depth, ulong expected)
    {
        var board = Fen.Parse(Position5);
        ulong actual = PerftRunner.Perft(ref board, depth);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PerftDivide_Matches_Perft()
    {
        var board = Fen.Parse(StartPos);
        ulong callbackSum = 0;
        ulong divideResult = PerftRunner.PerftDivide(ref board, 3, (move, count) =>
        {
            Assert.False(move.IsNull);
            Assert.True(count > 0);
            callbackSum += count;
        });

        ulong perftResult = PerftRunner.Perft(ref board, 3);
        Assert.Equal(8902UL, perftResult);
        Assert.Equal(perftResult, divideResult);
        Assert.Equal(perftResult, callbackSum);
    }

    [Fact]
    public void Benchmark_Calculates_Valid_MNPS()
    {
        var board = Fen.Parse(StartPos);
        var result = PerftRunner.Benchmark(ref board, 3);
        Assert.Equal(8902UL, result.Nodes);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        Assert.True(result.Mnps >= 0.0);
    }
}
