using Anak.Core;
using Xunit;

namespace Anak.Tests.Core;

public class SEETests
{
    [Fact]
    public void SEE_FreePawn_ReturnsPositive()
    {
        // Black pawn on e5 is completely undefended; White knight on f3 takes it
        Board board = Fen.Parse("rnbqkb1r/pppp1ppp/5n2/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 0 1");
        Move capture = new(Square.F3, Square.E5, MoveType.Capture);

        int see = StaticExchangeEvaluator.Evaluate(in board, capture);
        Assert.True(see >= 100, $"Expected SEE >= 100, but got {see}");
    }

    [Fact]
    public void SEE_QueenTakesDefendedPawn_ReturnsNegative()
    {
        // Black pawn on d5 defended by knight c6 and pawn e6. White queen on d1 takes d5.
        Board board = Fen.Parse("r1bqkbnr/ppp2ppp/2n1p3/3p4/4P3/3P4/PPP2PPP/RNBQKBNR w KQkq - 0 1");
        Move queenTakesPawn = new(Square.D1, Square.D5, MoveType.Capture);

        int see = StaticExchangeEvaluator.Evaluate(in board, queenTakesPawn);
        // Queen (900) captured for pawn (100) -> loss of ~800
        Assert.True(see <= -700, $"Expected losing SEE <= -700 for QxP on defended pawn, got {see}");
    }

    [Fact]
    public void SEE_PawnTakesDefendedQueen_ReturnsPositive()
    {
        // Black queen on e4 defended by Rook e8; White pawn on d3 takes Queen e4
        Board board = Fen.Parse("4r1k1/ppp2ppp/8/8/4q3/3P4/PPP2PPP/R1B2RK1 w - - 0 1");
        Move pawnTakesQueen = new(Square.D3, Square.E4, MoveType.Capture);

        int see = StaticExchangeEvaluator.Evaluate(in board, pawnTakesQueen);
        // Pawn takes Queen (900) and gets recaptured (-100) -> net +800
        Assert.True(see >= 700, $"Expected winning SEE >= 700 for PxQ, got {see}");
    }

    [Fact]
    public void SEE_EqualMinorExchange_ReturnsNearZero()
    {
        // White Bishop on c4 takes Black Bishop on e6 defended by Pawn f7
        Board board = Fen.Parse("rnbqk2r/ppp2ppp/4bn2/3p4/1bB1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 0 1");
        Move bishopTakesBishop = new(Square.C4, Square.E6, MoveType.Capture);

        int see = StaticExchangeEvaluator.Evaluate(in board, bishopTakesBishop);
        Assert.InRange(see, -50, 50);
    }

    [Fact]
    public void SEE_QuietBishopMoveToKnightAttackedPawnDefendedSquare_ReturnsMinorTradeNotHanging()
    {
        // Italian Game: 1. e4 e5 2. Nf3 Nc6 3. Bc4
        // Square c4 is attacked by nothing or if Black has a Knight on a5/e5 and White defends with d3/b3.
        // Specifically: White plays Bc4 where Black Knight on a5 attacks c4 and White Pawn on d3 defends c4.
        Board board = Fen.Parse("r1bqkbnr/pppp1ppp/8/n3p3/4P3/3P1N2/PPP2PPP/RNBQKB1R w KQkq - 1 4");
        Move bc4 = new(Square.F1, Square.C4, MoveType.Quiet);

        int see = StaticExchangeEvaluator.Evaluate(in board, bc4);
        // Bishop (330) captured by Knight (320) and recaptured by Pawn -> net -10 cp (minor exchange), NOT -330 cp
        Assert.Equal(-10, see);
        Assert.True(StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, bc4, -50));
    }
}
