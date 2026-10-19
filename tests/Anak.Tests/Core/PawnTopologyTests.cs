using Anak.Core;
using Xunit;

namespace Anak.Tests.Core;

public sealed class PawnTopologyTests
{
    [Fact]
    public void StartingPosition_IsClassifiedAsClosed()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        float openness = PawnTopology.CalculateOpenness(in board);

        // Standard startpos has 16 pawns, 0 open files, 0 semi-open files
        Assert.InRange(openness, 0.20f, 0.45f);
        Assert.False(PawnTopology.IsOpen(in board));
    }

    [Fact]
    public void FrenchDefenseAdvance_IsClassifiedAsClosed()
    {
        // 1.e4 e6 2.d4 d5 3.e5 c5 4.c3 - classic locked pawn chain
        const string frenchFen = "rnbqkbnr/pp3ppp/4p3/2ppP3/3P4/2P5/PP3PPP/RNBQKBNR b KQkq - 0 4";
        Board board = Fen.Parse(frenchFen);
        float openness = PawnTopology.CalculateOpenness(in board);

        Assert.True(openness < 0.50f, $"Expected French Defense to be closed (< 0.50), got {openness}");
        Assert.False(PawnTopology.IsOpen(in board));
    }

    [Fact]
    public void OpenSicilian_IsClassifiedAsOpen()
    {
        // 1.e4 c5 2.Nf3 d6 3.d4 cxd4 4.Nxd4 - c and d files open/semi-open
        const string sicilianFen = "r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4";
        Board board = Fen.Parse(sicilianFen);
        float openness = PawnTopology.CalculateOpenness(in board);

        Assert.True(openness >= 0.50f, $"Expected Open Sicilian to be open (>= 0.50), got {openness}");
        Assert.True(PawnTopology.IsOpen(in board));
    }

    [Fact]
    public void LiquidatedPawnEndgame_IsClassifiedAsOpen()
    {
        // Only 2 pawns remaining, lots of open files
        const string endgameFen = "8/5k2/4p3/8/8/4P3/5K2/8 w - - 0 40";
        Board board = Fen.Parse(endgameFen);
        float openness = PawnTopology.CalculateOpenness(in board);

        Assert.True(openness >= 0.60f, $"Expected endgame with 2 pawns to be open (>= 0.60), got {openness}");
        Assert.True(PawnTopology.IsOpen(in board));
    }
}
