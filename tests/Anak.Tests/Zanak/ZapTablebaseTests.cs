using Anak.Core;
using Anak.Data.Evaluation;
using Xunit;
using Zanak.Search;
using Zanak.Time;

namespace Anak.Tests.Zanak;

public sealed class ZapTablebaseTests
{
    private sealed class MockTablebaseProber : ITablebaseProber
    {
        public int MaxPieces { get; set; } = 4;
        public int SyzygyProbeDepth { get; set; } = 1;

        public Move ConfiguredDtzMove { get; set; } = Move.Null;
        public int ConfiguredDtz { get; set; } = 0;
        public bool DtzResult { get; set; } = true;

        public int ConfiguredWdl { get; set; } = 0;
        public bool WdlResult { get; set; } = true;

        public int DtzProbeCount { get; private set; }
        public int WdlProbeCount { get; private set; }

        public bool TryProbeWdl(in Board board, out int wdl)
        {
            WdlProbeCount++;
            wdl = ConfiguredWdl;
            return WdlResult;
        }

        public bool TryProbeDtz(in Board board, out Move bestMove, out int dtz)
        {
            DtzProbeCount++;
            bestMove = ConfiguredDtzMove;
            dtz = ConfiguredDtz;
            return DtzResult;
        }
    }

    [Fact]
    public void RootDtzProbe_ReturnsInstantBestMove_WithoutExploringTree()
    {
        // 3-piece endgame: White Kh1, Black Kh8, White pawn on h7
        // FEN: 7k/7P/8/8/8/8/8/7K w - - 0 1
        Board board = Fen.Parse("7k/7P/8/8/8/8/8/7K w - - 0 1");
        Move winningMove = new Move(Square.H1, Square.G2);

        var mockTb = new MockTablebaseProber
        {
            MaxPieces = 4,
            ConfiguredDtzMove = winningMove,
            ConfiguredDtz = 5,
            DtzResult = true
        };

        using var search = new ZapSearch(tablebase: mockTb);
        var budget = new SearchBudget(500, 1000, 8, 12);

        Move best = search.Search(in board, in budget);

        Assert.Equal(winningMove, best);
        Assert.True(mockTb.DtzProbeCount >= 1, "Expected root DTZ probe to be called.");
    }

    [Fact]
    public void InSpineWdl_TruncatesSpineImmediately_AndMapsExactWinScore()
    {
        // 4-piece endgame position: White Kh1, Qf1; Black Kh8, pawn on a7
        Board board = Fen.Parse("7k/p7/8/8/8/8/8/5Q1K w - - 0 1");
        var policy = new ZapPolicy();
        var evaluator = new DefaultEvaluator();

        var mockTb = new MockTablebaseProber
        {
            MaxPieces = 4,
            ConfiguredWdl = -2, // Loss for the side to move post-ply (Black), meaning Win for White (engine)
            WdlResult = true
        };

        ZapTrajectory trajectory = default;
        int score = ZapProbe.Trace(
            ref board,
            targetDepth: 12,
            policy,
            evaluator,
            Color.White,
            ref trajectory,
            tablebase: mockTb);

        // Win score should be mapped to ~29000
        Assert.True(score >= 28000, $"Expected tablebase win score >= 28000, got {score}");
        // Trajectory must truncate on the very first tablebase hit
        Assert.True(trajectory.Count <= 2, $"Expected spine to truncate immediately (<= 2 plies), got {trajectory.Count}");
        Assert.True(mockTb.WdlProbeCount >= 1, "Expected in-spine WDL probe.");
    }

    [Fact]
    public void FalseFortressRefutation_DemotesLeader_WhenOpponentForcesTablebaseDraw()
    {
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());

        // Leader candidate (0) initially evaluated at +300 cp (thinks it's winning material)
        // Contender candidate (1) evaluated at +120 cp
        sharedState.UpdateCandidateBaseline(0, 300);
        sharedState.UpdateCandidateBaseline(1, 120);
        sharedState.SortCandidatesAndPrune(hardAspirationMarginCp: 250);

        Assert.Equal(300, sharedState.GetBestCandidate().Score);

        // Opponent refutes Candidate 0 by liquidating into a Tablebase Draw (WDL = 0, score = 0)
        bool result = sharedState.TryApplyLeaderRefutation(
            leaderIndex: 0,
            refutedScore: 0,
            depth: 8,
            out bool demoted);

        Assert.True(result);
        Assert.True(demoted, "Leader should be demoted because 0 < contender score of 120.");
        Assert.Equal(RootMoveStatus.Refuted, sharedState.GetRootMoveStatus(0));
        // New leader is candidate 1 with +120 cp
        Assert.Equal(120, sharedState.GetBestCandidate().Score);
    }

    [Fact]
    public void TablebaseWinPromotion_ElevatesContender_AboveHeuristicLeader()
    {
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());

        // Candidate 0: Heuristic Leader at +150 cp
        // Candidate 1: Contender at +110 cp
        sharedState.UpdateCandidateBaseline(0, 150);
        sharedState.UpdateCandidateBaseline(1, 110);
        sharedState.SortCandidatesAndPrune(hardAspirationMarginCp: 100);

        // Contender 1 finds liquidation into Tablebase Win (WDL = 2, score = +28990)
        bool result = sharedState.TryApplyContenderPromotion(
            contenderIndex: 1,
            improvedScore: 28990,
            depth: 8,
            out bool promoted);

        Assert.True(result);
        Assert.True(promoted, "Contender with tablebase win should be promoted to leader.");
        Assert.Equal(28990, sharedState.GetBestCandidate().Score);
        Assert.Equal(RootMoveStatus.Active, sharedState.GetRootMoveStatus(1));
    }
}
