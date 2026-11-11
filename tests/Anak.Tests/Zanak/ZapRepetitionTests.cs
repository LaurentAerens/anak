using Anak.Core;
using Xunit;
using Zanak.Search;
using Zanak.Time;

namespace Anak.Tests.Zanak;

public sealed class ZapRepetitionTests
{
    private static Board SetupUserGameToMove(int toMovePly)
    {
        Board board = Fen.Parse(Fen.StartPosition);

        // 1. e4 c5
        board.MakeMove(new Move(Square.E2, Square.E4), out _);
        if (toMovePly == 1) return board;
        board.MakeMove(new Move(Square.C7, Square.C5), out _);
        if (toMovePly == 2) return board;

        // 2. Nf3 Nc6
        board.MakeMove(new Move(Square.G1, Square.F3), out _);
        if (toMovePly == 3) return board;
        board.MakeMove(new Move(Square.B8, Square.C6), out _);
        if (toMovePly == 4) return board;

        // 3. Bb5 g6
        board.MakeMove(new Move(Square.F1, Square.B5), out _);
        if (toMovePly == 5) return board;
        board.MakeMove(new Move(Square.G7, Square.G6), out _);
        if (toMovePly == 6) return board;

        // 4. O-O Bg7
        board.MakeMove(new Move(Square.E1, Square.G1, MoveType.KingCastle), out _);
        if (toMovePly == 7) return board;
        board.MakeMove(new Move(Square.F8, Square.G7), out _);
        if (toMovePly == 8) return board;

        // 5. c3 Nf6
        board.MakeMove(new Move(Square.C2, Square.C3), out _);
        if (toMovePly == 9) return board;
        board.MakeMove(new Move(Square.G8, Square.F6), out _);
        if (toMovePly == 10) return board;

        // 6. d4 cxd4
        board.MakeMove(new Move(Square.D2, Square.D4), out _);
        if (toMovePly == 11) return board;
        board.MakeMove(new Move(Square.C5, Square.D4), out _);
        if (toMovePly == 12) return board;

        // 7. cxd4 Nxe4
        board.MakeMove(new Move(Square.C3, Square.D4), out _);
        if (toMovePly == 13) return board;
        board.MakeMove(new Move(Square.F6, Square.E4), out _);
        if (toMovePly == 14) return board;

        // 8. Re1 d5
        board.MakeMove(new Move(Square.F1, Square.E1), out _);
        if (toMovePly == 15) return board;
        board.MakeMove(new Move(Square.D7, Square.D5), out _);
        if (toMovePly == 16) return board;

        // 9. Qe2 Bd7
        board.MakeMove(new Move(Square.D1, Square.E2), out _);
        if (toMovePly == 17) return board;
        board.MakeMove(new Move(Square.C8, Square.D7), out _);
        if (toMovePly == 18) return board;

        // 10. Qxe4 dxe4
        board.MakeMove(new Move(Square.E2, Square.E4), out _);
        if (toMovePly == 19) return board;
        board.MakeMove(new Move(Square.D5, Square.E4), out _);
        if (toMovePly == 20) return board;

        // 11. Rxe4 Nb8
        board.MakeMove(new Move(Square.E1, Square.E4), out _);
        if (toMovePly == 21) return board;
        board.MakeMove(new Move(Square.C6, Square.B8), out _);
        if (toMovePly == 22) return board;

        // 12. Rxe7+ Kxe7
        board.MakeMove(new Move(Square.E4, Square.E7), out _);
        if (toMovePly == 23) return board;
        board.MakeMove(new Move(Square.E8, Square.E7), out _);
        if (toMovePly == 24) return board;

        // 13. Bg5+ Bf6
        board.MakeMove(new Move(Square.C1, Square.G5), out _);
        if (toMovePly == 25) return board;
        board.MakeMove(new Move(Square.G7, Square.F6), out _);
        if (toMovePly == 26) return board;

        // 14. Bxf6+ Kxf6
        board.MakeMove(new Move(Square.G5, Square.F6), out _);
        if (toMovePly == 27) return board;
        board.MakeMove(new Move(Square.E7, Square.F6), out _);
        if (toMovePly == 28) return board;

        // 15. Nbd2 Bg4
        board.MakeMove(new Move(Square.B1, Square.D2), out _);
        if (toMovePly == 29) return board;
        board.MakeMove(new Move(Square.D7, Square.G4), out _);
        if (toMovePly == 30) return board;

        // 16. Ne4+ Ke6
        board.MakeMove(new Move(Square.D2, Square.E4), out _);
        if (toMovePly == 31) return board;
        board.MakeMove(new Move(Square.F6, Square.E6), out _);
        if (toMovePly == 32) return board;

        // 17. Nc5+ Kf6
        board.MakeMove(new Move(Square.E4, Square.C5), out _);
        if (toMovePly == 33) return board;
        board.MakeMove(new Move(Square.E6, Square.F6), out _);
        if (toMovePly == 34) return board;

        // 18. Ne4+
        board.MakeMove(new Move(Square.C5, Square.E4), out _);
        if (toMovePly == 35) return board;

        // 18... Ke6
        board.MakeMove(new Move(Square.F6, Square.E6), out _);
        if (toMovePly == 36) return board;

        // 19. Nc5+
        board.MakeMove(new Move(Square.E4, Square.C5), out _);
        if (toMovePly == 37) return board;

        // 19... Kf6
        board.MakeMove(new Move(Square.E6, Square.F6), out _);
        if (toMovePly == 38) return board;

        // 20. Ne4+
        board.MakeMove(new Move(Square.C5, Square.E4), out _);
        if (toMovePly == 39) return board;

        return board;
    }

    [Fact]
    public void Repetition_AtMove18_AvoidsSteppingIntoRepetitionCycle()
    {
        // At ply 35: White just played 18. Ne4+
        // Black is heavily winning (up Queen for minor piece).
        // 18... Ke6 was already played at move 16 (ply 32), and allows 19. Nc5+ Kf6 20. Ne4+ cycle.
        // Zanak must choose a non-repeating winning evasion like Kg7 (f6g7) or Ke7 (f6e7).
        Board board = SetupUserGameToMove(35);

        using var search = new ZapSearch();
        var budget = new SearchBudget(200, 500, 6, 12);
        Move bestMove = search.Search(in board, in budget);

        Assert.NotEqual(new Move(Square.F6, Square.E6), bestMove);
        Assert.True(bestMove == new Move(Square.F6, Square.G7) || bestMove == new Move(Square.F6, Square.E7) || bestMove == new Move(Square.F6, Square.F5), $"Unexpected bestMove: {bestMove}");
    }

    [Fact]
    public void Repetition_AtMove19_AvoidsKf6WhichAllowsImmediateThreefoldDraw()
    {
        // At ply 37: White played 19. Nc5+
        // Black King is on e6.
        // If Black plays 19... Kf6, White can immediately play 20. Ne4+ which triggers threefold repetition!
        // Zanak must reject 19... Kf6 and pick an alternative escape square (e.g. Ke7 or Kd5).
        Board board = SetupUserGameToMove(37);

        using var search = new ZapSearch();
        var budget = new SearchBudget(200, 500, 6, 12);
        Move bestMove = search.Search(in board, in budget);

        Assert.NotEqual(new Move(Square.E6, Square.F6), bestMove);
    }

    [Fact]
    public void Repetition_LosingSide_ChoosesRepetitionToClaimDraw()
    {
        // At ply 38: Black just played 19... Kf6
        // White is heavily losing (down Queen for minor piece).
        // White has legal move 20. Ne4+ (c5e4), which immediately triggers a threefold repetition draw!
        // Because 0 cp (draw) is vastly superior to -800 cp (loss), Zanak as White must choose 20. Ne4+!
        Board board = SetupUserGameToMove(38);

        using var search = new ZapSearch();
        var budget = new SearchBudget(200, 500, 6, 12);
        Move bestMove = search.Search(in board, in budget);

        Assert.Equal(new Move(Square.C5, Square.E4), bestMove);
    }

    [Fact]
    public void Repetition_AtMove20_RootPositionIsThreefoldDraw_ReportsDrawScore()
    {
        // At ply 39: White played 20. Ne4+
        // Position has now occurred 3 times (at ply 31, ply 35, ply 39).
        // Threefold repetition is met!
        Board board = SetupUserGameToMove(39);

        Assert.True(board.IsThreefoldRepetition());

        using var search = new ZapSearch();
        var budget = new SearchBudget(200, 500, 6, 12);
        ZapSearchStats? lastStats = null;
        Move bestMove = search.Search(in board, in budget, stats => lastStats = stats);

        Assert.False(bestMove.IsNull);
        Assert.True(board.IsLegal(in bestMove));
        Assert.NotNull(lastStats);
        Assert.Equal(0, lastStats.Value.ScoreCp);
    }
}
