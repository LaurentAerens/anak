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
        Assert.True(PawnTopology.IsEndgame(in board));
        Assert.Equal(NnueExpertType.Endgame, PawnTopology.ClassifyExpert(in board));
    }

    [Fact]
    public void ClassifyExpert_RoutesAllSixExpertsCorrectly()
    {
        // 1. Closed Middlegame (starting position: 32 pieces, Queens present, balanced, kings on E1/E8)
        Board closedBoard = Fen.Parse(Fen.StartPosition);
        Assert.Equal(NnueExpertType.Closed, PawnTopology.ClassifyExpert(in closedBoard));

        // 2. Open Middlegame (Open Sicilian: >14 pieces, Queens present, balanced, same-flank/central kings, openness >= 0.50)
        Board openBoard = Fen.Parse("r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4");
        Assert.Equal(NnueExpertType.Open, PawnTopology.ClassifyExpert(in openBoard));

        // 3. Endgame (<= 14 pieces)
        Board endgameBoard = Fen.Parse("4k3/pp3ppp/8/8/8/8/PP3PPP/4K3 w - - 0 30");
        Assert.True(PawnTopology.IsEndgame(in endgameBoard));
        Assert.Equal(NnueExpertType.Endgame, PawnTopology.ClassifyExpert(in endgameBoard));

        // 4. Queenless Middlegame (> 14 pieces, 0 Queens)
        Board queenlessBoard = Fen.Parse("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1");
        Assert.False(PawnTopology.IsEndgame(in queenlessBoard));
        Assert.True(PawnTopology.IsQueenless(in queenlessBoard));
        Assert.Equal(NnueExpertType.Queenless, PawnTopology.ClassifyExpert(in queenlessBoard));

        // 5. Material Imbalance (> 14 pieces, unequal Queens or exchange imbalance)
        Board imbalanceBoard = Fen.Parse("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Assert.True(PawnTopology.HasMaterialImbalance(in imbalanceBoard));
        Assert.Equal(NnueExpertType.Imbalance, PawnTopology.ClassifyExpert(in imbalanceBoard));

        // Exchange imbalance (Rook vs Minor with equal Queens and > 14 pieces)
        Board exchangeBoard = Fen.Parse("1nbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQK1NR w KQk - 0 1");
        Assert.True(PawnTopology.HasMaterialImbalance(in exchangeBoard));
        Assert.Equal(NnueExpertType.Imbalance, PawnTopology.ClassifyExpert(in exchangeBoard));

        // 6. Opposite-Side Castling Attack (> 14 pieces, Queens present, balanced material, White King on C1, Black King on G8)
        Board attackBoard = Fen.Parse("r1bq1rk1/ppp1ppbp/2np1np1/8/3PP3/2N1BP2/PPPQ2PP/2KR1BNR w - - 3 8");
        Assert.True(PawnTopology.IsOppositeSideCastling(in attackBoard));
        Assert.Equal(NnueExpertType.Attack, PawnTopology.ClassifyExpert(in attackBoard));
    }
}
