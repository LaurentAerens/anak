using Anak.Core;
using Anak.Search;
using Xunit;
using Zanak.Search;
using Zanak.Time;

namespace Anak.Tests.Zanak;

public sealed class ZapSearchTests
{
    [Fact]
    public void Search_SingleLegalMove_ReturnsImmediately()
    {
        Board board = Fen.Parse("7K/7P/7q/5k2/8/8/8/8 w - - 0 1");
        MoveList list = default;
        MoveGenerator.GenerateLegalMoves(in board, ref list);
        Assert.Equal(1, list.Count);

        using var search = new ZapSearch();
        var budget = new SearchBudget(1000, 2000, 8, 14);

        Move best = search.Search(in board, in budget);
        Assert.Equal(list[0], best);
    }

    [Fact]
    public void Search_StartPosition_ReturnsValidLegalMove()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        using var search = new ZapSearch();
        var budget = new SearchBudget(100, 200, 6, 10);

        Move best = search.Search(in board, in budget);

        Assert.False(best.IsNull);
        Assert.True(board.IsLegal(in best));
    }

    [Fact]
    public void ZapSearchEngine_ISearchEngine_ExecutesSuccessfully()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        using var engine = new ZapSearchEngine();

        var limits = new SearchLimits
        {
            MoveTime = 100
        };

        SearchStatistics? lastStats = null;
        Move best = engine.Search(
            in board,
            in limits,
            stats => lastStats = stats);

        Assert.False(best.IsNull);
        Assert.True(board.IsLegal(in best));
        Assert.NotNull(lastStats);
        Assert.True(lastStats.Value.Nodes > 0);
    }

    [Fact]
    public void GatedQualification_PrunesDeadStems_AndActivatesLeaderAndContenders()
    {
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());

        sharedState.UpdateCandidateBaseline(0, 200);
        sharedState.UpdateCandidateBaseline(1, 150);
        sharedState.UpdateCandidateBaseline(2, 50);

        sharedState.SortCandidatesAndPrune(hardAspirationMarginCp: 100);

        var leader = sharedState.GetCandidate(0);
        Assert.Equal(200, leader.Score);
        Assert.False(leader.IsDeadStem);
        Assert.Equal(RootMoveStatus.Active, sharedState.GetRootMoveStatus(0));

        var contender = sharedState.GetCandidate(1);
        Assert.Equal(150, contender.Score);
        Assert.False(contender.IsDeadStem);
        Assert.Equal(RootMoveStatus.Active, sharedState.GetRootMoveStatus(1));

        var deadStem = sharedState.GetCandidate(2);
        Assert.Equal(50, deadStem.Score);
        Assert.True(deadStem.IsDeadStem);
        Assert.Equal(RootMoveStatus.Dead, sharedState.GetRootMoveStatus(2));
    }

    [Fact]
    public void LeaderRefutation_DemotesLeader_AndMarksRefuted()
    {
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());

        sharedState.UpdateCandidateBaseline(0, 100);
        sharedState.UpdateCandidateBaseline(1, 80);
        sharedState.SortCandidatesAndPrune(100);

        Assert.Equal(0, sharedState.GetBestCandidate().Score == 100 ? 0 : -1);

        bool result = sharedState.TryApplyLeaderRefutation(leaderIndex: 0, refutedScore: 50, depth: 6, out bool demoted);

        Assert.True(result);
        Assert.True(demoted);
        Assert.Equal(RootMoveStatus.Refuted, sharedState.GetRootMoveStatus(0));
        Assert.Equal(80, sharedState.GetBestCandidate().Score);
    }

    [Fact]
    public void ContenderPromotion_ElevatesContenderPastLeader()
    {
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());

        sharedState.UpdateCandidateBaseline(0, 100);
        sharedState.UpdateCandidateBaseline(1, 80);
        sharedState.SortCandidatesAndPrune(100);

        bool result = sharedState.TryApplyContenderPromotion(contenderIndex: 1, improvedScore: 150, depth: 6, out bool promoted);

        Assert.True(result);
        Assert.True(promoted);
        Assert.Equal(150, sharedState.GetBestCandidate().Score);
        Assert.Equal(RootMoveStatus.Active, sharedState.GetRootMoveStatus(1));
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")] // Start position
    [InlineData("r1bqkb1r/pppp1ppp/2n5/4p3/2B1n3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 5")] // Tactical Italian
    [InlineData("8/2k5/8/8/8/8/4K3/8 w - - 0 1")] // King and pawn/bare king
    [InlineData("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/3P1N2/PPP2PPP/RNBQK2R w KQkq - 1 5")] // Complex middlegame
    [InlineData("8/8/8/8/4k3/8/4P3/4K3 w - - 0 1")] // Simple endgame
    public void Search_DiversePositions_AlwaysReturnsLegalMove(string fen)
    {
        Board board = Fen.Parse(fen);
        using var search = new ZapSearch();
        var budget = new SearchBudget(50, 100, 4, 8);

        Move best = search.Search(in board, in budget);

        Assert.False(best.IsNull);
        Assert.True(board.IsLegal(in best), $"Expected legal move, got {best.ToUci()} in FEN {fen}");
    }

    [Fact]
    public void Search_RespectsCancellationToken()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        using var search = new ZapSearch();
        var budget = new SearchBudget(5000, 10000, 20, 30);
        using var cts = new System.Threading.CancellationTokenSource();

        // Cancel immediately
        cts.Cancel();

        Move best = search.Search(in board, in budget, cancellationToken: cts.Token);

        // Even with immediate cancellation, it should safely return without hanging or crashing
        if (!best.IsNull)
        {
            Assert.True(board.IsLegal(in best));
        }
    }
}
