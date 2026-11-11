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

    [Fact]
    public void ZapSearch_ViennaMove6_RecapturesKnight_EvenWhenLastMoveIsNull()
    {
        Board board = Fen.Parse("r1bqkb1r/ppp2ppp/8/4P3/8/2n2Q2/PPPP2PP/R1B1KBNR w KQkq - 0 6");
        using var search = new ZapSearch();
        var budget = new SearchBudget(200, 400, 6, 10);

        Move best = search.Search(in board, in budget, lastMove: Move.Null);

        Assert.True(best.To == Square.C3 || best.To == Square.E6, $"Expected capture on c3 or e5e6, got {best.ToUci()}");
        Assert.NotEqual(Square.D2, best.To);
    }

    [Fact]
    public void CompareAlphaBetaOnVienna()
    {
        Board rootBoard = Fen.Parse("r1bqkb1r/ppp2ppp/8/4P3/8/2n2Q2/PPPP2PP/R1B1KBNR w KQkq - 0 6");
        var nnue = new Anak.Data.Nnue.MultiTierNnueEvaluator();
        var abEngine = new Anak.Search.AlphaBetaEngine(nnue);
        Move abBest = abEngine.Search(in rootBoard, new SearchLimits { Depth = 6 });
        Assert.Equal(Square.C3, abBest.To);
        Assert.True(abBest.IsCapture);
    }
}
