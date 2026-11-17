using Anak.Core;
using Xunit;
using Zanak.Search;

namespace Anak.Tests.Zanak;

public sealed class ZapRootFilterTests
{
    private static (Move[] moves, bool[] keep, int rejected) Run(string fen, int margin = 300)
    {
        Board board = Fen.Parse(fen);
        MoveList list = default;
        MoveGenerator.GenerateLegalMoves(in board, ref list);
        Move[] moves = list.AsSpan().ToArray();
        bool[] keep = new bool[moves.Length];
        int rejected = ZapRootFilter.Filter(in board, moves, keep, margin);
        return (moves, keep, rejected);
    }

    [Fact]
    public void StartPosition_RejectsNothing()
    {
        var (_, keep, rejected) = Run(Fen.StartPosition);
        Assert.Equal(0, rejected);
        Assert.All(keep, k => Assert.True(k));
    }

    [Fact]
    public void QueenStepsIntoPawnAttack_IsRejected_ButSafeMovesKept()
    {
        // White queen d1 can go to d5 where the black e6 pawn takes it for free.
        var (moves, keep, rejected) = Run("4k3/8/4p3/8/8/8/8/3QK3 w - - 0 1");
        int d5 = Array.FindIndex(moves, m => m.ToUci() == "d1d5");
        int d2 = Array.FindIndex(moves, m => m.ToUci() == "d1d2");
        Assert.True(d5 >= 0 && d2 >= 0);
        Assert.False(keep[d5]);
        Assert.True(keep[d2]);
        Assert.True(rejected >= 1);
    }

    [Fact]
    public void DisabledMargin_RejectsNothing()
    {
        var (_, keep, rejected) = Run("4k3/8/4p3/8/8/8/8/3QK3 w - - 0 1", margin: 0);
        Assert.Equal(0, rejected);
        Assert.All(keep, k => Assert.True(k));
    }

    [Fact]
    public void BishopTakesDefendedPawn_HangingMinor_IsRejected()
    {
        // FEN from Game 2: r2qkbnr/1b1n1ppp/p2pp3/1p6/P2NP1P1/2N1B3/1PP2P1P/R2QKB1R b KQkq - 0 9
        // Black plays Bxe4 (b7e4) taking e4 pawn defended by Nc3 and Nf3.
        var (moves, keep, rejected) = Run("r2qkbnr/1b1n1ppp/p2pp3/1p6/P2NP1P1/2N1B3/1PP2P1P/R2QKB1R b KQkq - 0 9");
        int bxe4 = Array.FindIndex(moves, m => m.ToUci() == "b7e4");
        Assert.True(bxe4 >= 0);
        Assert.False(keep[bxe4]);
    }

    [Fact]
    public void NeverRejectsEveryMove()
    {
        var (_, keep, _) = Run("rnbqkbnr/pppp1ppp/8/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2");
        Assert.Contains(true, keep);
    }

    [Fact]
    public void ExistingMateIn1Threat_RejectsMovesThatStillAllowMateIn1()
    {
        // Black Qh3 threatens Qxg2# on a null move; Ra1a2 allows Qxg2#, while g2h3 (taking the queen) stops it.
        var (moves, keep, _) = Run("6k1/5ppp/8/8/8/7q/6P1/R5K1 w - - 0 1");
        int ra2 = Array.FindIndex(moves, m => m.ToUci() == "a1a2");
        int g2h3 = Array.FindIndex(moves, m => m.ToUci() == "g2h3");
        Assert.True(ra2 >= 0 && g2h3 >= 0);
        Assert.False(keep[ra2]);
        Assert.True(keep[g2h3]);
    }
}
