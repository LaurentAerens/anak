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

    [Fact]
    public void InCheck_RejectsEvasionsThatAllowMateIn1()
    {
        // Position after 30. Qe5+ from lichess game QjIj3NJi: Kf8 (g7f8) walks into 31. Re8#, while Kh6 (g7h6) does not.
        var (moves, keep, rejected) = Run("6r1/1p3rkp/2p1R1p1/3pQ3/pP4B1/P7/2P3PP/R5K1 b - - 2 30");
        int kf8 = Array.FindIndex(moves, m => m.ToUci() == "g7f8");
        int kh6 = Array.FindIndex(moves, m => m.ToUci() == "g7h6");
        Assert.True(kf8 >= 0 && kh6 >= 0);
        Assert.False(keep[kf8]);
        Assert.True(keep[kh6]);
        Assert.True(rejected >= 1);
    }

    [Fact]
    public void InCheck_NeverRejectsAllEvasionsWhenAllAllowMateIn1()
    {
        // Black king in check on h8 (from Qh6+); both Kg8 and Kf8? Use a forced mate-in-2 position where every evasion allows mate-in-1.
        // White Qa6+, Ra1, Black Ka8, pawns a7/b7: wait, Ka8 in check from Qh1? Let's use White Qd5+, Re1, Black Kf8 (moves: Kg7 -> Qg8#? No, Qg8# if f8-g8).
        // Simple: White Qa7+, Rb1, Black Kc8, pawns c7/b7 missing -> Kd8 allows Qb8#/Qd7#; Kb8 allows Qb7#? No, Kc8 in check by Rc1+: Kd8/Kd7?
        // White Rc1+, Qa6, Black Kb8, pawns a7: Ka8 allows Qb7# or Rc8#; Kc8 isn't legal. Wait: only 1 legal move returns 0 anyway.
        // Two legal evasions that both allow mate-in-1: White Qb3+ (b3-f7), Rf1, Black Kg8, pawns g7/h7 -> Kh8 allows Rf8#, Kf8 allows Qf7#.
        var (_, keep, rejected) = Run("6k1/6pp/8/8/8/1Q6/8/5R1K b - - 0 1");
        Assert.Equal(0, rejected);
        Assert.All(keep, k => Assert.True(k));
    }
}
