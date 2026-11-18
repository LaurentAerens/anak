using System.Numerics;
using System.Text;
using Anak.Core;
using Anak.Search;
using Xunit;
using Zanak.Search;
using Zanak.Time;
using Zanak.Uci;

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
        sharedState.UpdateCandidateScreen(0, 200);
        sharedState.UpdateCandidateScreen(1, 150);
        sharedState.UpdateCandidateScreen(2, 50);

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

    [Fact]
    public void Qualification_DoesNotPruneAnUnscreenedAlternativeOnOptimismAlone()
    {
        var state = new ZapSharedState();
        state.Reset([new Move(Square.E2, Square.E4), new Move(Square.D2, Square.D4)]);
        state.UpdateCandidateBaseline(0, 1500);
        state.UpdateCandidateBaseline(1, 0);
        state.SortCandidatesAndPrune(100);
        Assert.False(state.GetCandidate(1).IsDeadStem);
    }

    [Fact]
    public void RefutationCeiling_SurvivesPromotionAndBaselineRefresh()
    {
        var state = new ZapSharedState();
        state.Reset([new Move(Square.E2, Square.E4)]);
        state.UpdateCandidateBaseline(0, 100);
        state.TryApplyContenderPromotion(0, 500, 4, out _);
        state.ApplyAgentRefutation(0, -200, 5);
        Assert.False(state.TryApplyContenderPromotion(0, 1000, 6, out _));
        state.UpdateCandidateBaseline(0, 2000);
        Assert.Equal(-200, state.GetCandidate(0).Score);
        Assert.True(state.GetCandidate(0).AgentFloor <= state.GetCandidate(0).AgentCeiling);
    }

    [Fact]
    public void Extraction_KeepsEveryLowDeltaPositionAcrossBothRoles()
    {
        using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 });
        Board root = Fen.Parse(Fen.StartPosition);
        Move rootMove = LegalMove(in root, "e2e4");
        Board board = root;
        Assert.True(board.MakeMove(rootMove, out _));
        var steps = Steps(board, "e7e5", "g1f3", "b8c6");

        search.ExtractCrosspoints(in root, rootMove, 0, steps, Color.White, 1, ZapAgentRole.Refutation);
        search.ExtractCrosspoints(in root, rootMove, 0, steps, Color.White, 1, ZapAgentRole.Promotion);

        var plies = new List<int>();
        while (search.WorkQueue.TryDequeue(out var cp))
        {
            plies.Add(cp.Ply);
            Assert.True(cp.Board.IsLegal(cp.OriginalMove));
        }
        Assert.Equal(new[] { 1, 2, 3 }, plies.Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerReplacement_IncludesJunctionMove_EvenWithCachedResult(bool cached)
    {
        var parameters = new ZapParams { DefaultThreads = 1, RootFilterMarginCp = 0, QuiescenceMaxPlies = 0 };
        var queue = new ZapWorkQueue(parameters);
        var state = new ZapSharedState();
        Board root = Fen.Parse(Fen.StartPosition);
        Move rootMove = LegalMove(in root, "e2e4");
        Board junction = root;
        Assert.True(junction.MakeMove(rootMove, out _));
        var original = Steps(junction, "e7e5", "g1f3");
        state.Reset([rootMove]);
        uint generation = state.NextGeneration();
        state.UpdateCandidateBaseline(0, 0);
        state.UpdateCandidateTrajectory(0, original);
        using var tt = new ZapTranspositionTable(1);
        Move refutation = LegalMove(in junction, "c7c5");
        if (cached) tt.Store(junction.ZobristKey, refutation, 99, -500, flags: 2);

        var evaluator = new C5Evaluator();
        using var pool = new ZapWorkerPool(1, queue, state, parameters, evaluator, tt);
        pool.SetRootBoard(in root);
        var budget = new SearchBudget(1000, 2000, 2, 2);
        pool.StartExploration(Color.White, in budget, runOnCaller: true);
        var cp = new ZapCrosspoint(junction, 0, 1, 0, 0, CrosspointKind.AdversarialRefutation,
            1, original[0].Move, generation: generation);
        queue.Enqueue(in cp);
        while (queue.TryDequeue(out var pending, state, generation))
            pool.ProcessCrosspoint(in pending, new ZapPolicy(parameters), evaluator);
        pool.StopExploration();

        ZapRootCandidate candidate = state.GetCandidate(0);
        Assert.Equal(-500, candidate.Score);
        Assert.NotEmpty(candidate.Trajectory);
        Assert.Equal(refutation, candidate.Trajectory[0].Move);
        Assert.Equal(refutation, candidate.BestFollowup);
        foreach (var step in candidate.Trajectory)
        {
            Assert.Equal(junction.ZobristKey, step.ZobristKey);
            Assert.True(junction.IsLegal(step.Move));
            Assert.True(junction.MakeMove(step.Move, out _));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(32)]
    public void Worker_ResumedBatchesCoverEverySiblingOnce_AndConverge(int batchSize)
    {
        var parameters = new ZapParams
        {
            DefaultThreads = 1,
            RootFilterMarginCp = 0,
            QuiescenceMaxPlies = 0,
            MaxSiblingExploration = batchSize
        };
        Board root = Fen.Parse(Fen.StartPosition);
        Move rootMove = LegalMove(in root, "e2e4");
        Board junction = root;
        Assert.True(junction.MakeMove(rootMove, out _));
        var original = Steps(junction, "e7e5");
        var state = new ZapSharedState();
        state.Reset([rootMove]);
        uint generation = state.NextGeneration();
        state.UpdateCandidateTrajectory(0, original);
        var queue = new ZapWorkQueue(parameters);
        var evaluator = new C5Evaluator();
        using var pool = new ZapWorkerPool(1, queue, state, parameters, evaluator);
        pool.SetRootBoard(in root);
        var budget = new SearchBudget(1000, 2000, 2, 2);
        pool.StartExploration(Color.White, in budget, runOnCaller: true);
        var cp = new ZapCrosspoint(junction, 0, 1, 0, 0, CrosspointKind.AdversarialRefutation,
            1, original[0].Move, generation: generation);
        queue.Enqueue(in cp);
        var batches = new List<ZapBatchTrace>();
        pool.BatchCompleted += batches.Add;
        var policy = new ZapPolicy(parameters);
        while (queue.TryDequeue(out var pending, state, generation))
            pool.ProcessCrosspoint(in pending, policy, evaluator);
        pool.StopExploration();

        MoveList legal = default;
        junction.GenerateLegalMoves(ref legal);
        Assert.Equal(legal.Count - 1, pool.SiblingsTraced);
        Assert.Equal(legal.Count, state.TotalNodes);
        Assert.Equal(1, pool.CompletedCrosspoints);
        Assert.Equal(-500, state.GetCandidate(0).Score);
        Assert.Equal("c7c5", state.GetCandidate(0).BestFollowup.ToUci());
        Assert.True(batches[^1].Completed);
        Assert.All(batches.SkipLast(1), batch => Assert.False(batch.Completed));
        Assert.True(batches[0].ExploredSiblings <= batchSize);
        Assert.Equal((legal.Count - 2) / batchSize + 1, batches.Count);
    }

    [Fact]
    public void Tree_IncrementalSiblingsPreserveEarlierRefutationAndDescendants()
    {
        Board root = Fen.Parse(Fen.StartPosition);
        Assert.True(root.MakeMove(LegalMove(in root, "e2e4"), out _));
        ZapTrajectoryStep[] original = Steps(root, "e7e5", "g1f3", "b8c6");
        var tree = new ZapSpineTree();
        tree.Reset(1);
        tree.SetRootSpine(0, original, 100);
        var cp = new ZapCrosspoint(root, 0, 1, 100, 0, CrosspointKind.AdversarialRefutation,
            3, original[0].Move);
        var first = tree.Expand(in cp, false, [new ZapSpineTree.Child(original, 100)], out _);
        Board child = root;
        Assert.True(child.MakeMove(original[0].Move, out _));
        var descendant = new ZapCrosspoint(child, 0, 2, 100, 0, CrosspointKind.Opportunity,
            2, original[1].Move);
        tree.Expand(in descendant, true, [new ZapSpineTree.Child(Steps(child, "g1f3", "b8c6"), -400)], out _);
        Assert.Equal(-400, tree.RootValue(0));
        tree.Expand(in cp, false, [new ZapSpineTree.Child(Steps(root, "c7c5", "g1f3", "b8c6"), 200)], out _);
        Assert.Equal(-400, tree.RootValue(0));
        Assert.True(first[0].Edge.Live);
        Assert.Equal(original.Select(s => s.Move), tree.PrincipalLine(0).Select(s => s.Move));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_InterleavingChallengesPublishedRoots_AndStillSeedsEveryRoot(bool interleave)
    {
        Board board = Fen.Parse(Fen.StartPosition);
        var parameters = new ZapParams
        {
            DefaultThreads = 1,
            RootFilterMarginCp = 0,
            MaxSiblingExploration = 1,
            InterleaveRootSeeding = interleave
        };
        using var search = new ZapSearch(parameters, evaluator: new ZeroEvaluator());
        bool challengedDuringSeeding = false;
        MoveList legal = default;
        board.GenerateLegalMoves(ref legal);
        int rootCount = legal.Count;
        search.SharedState.AgentTraced += _ =>
        {
            if (search.SeededRoots < rootCount) challengedDuringSeeding = true;
        };
        using var cts = new CancellationTokenSource();
        var budget = new SearchBudget(10000, 20000, 3, 3);
        Move best = search.Search(in board, in budget, _ => cts.Cancel(), cts.Token);
        Assert.True(board.IsLegal(best));
        Assert.Equal(rootCount, search.SeededRoots);
        Assert.Equal(interleave, challengedDuringSeeding);
    }

    [Fact]
    public void Search_PickupDiagnosticsRecordReasonsAndProgress_AndCloseAfterSearch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zanak-pickups-{Guid.NewGuid():N}.jsonl");
        try
        {
            Board board = Fen.Parse(Fen.StartPosition);
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 1, MaxSiblingExploration = 1 },
                evaluator: new ZeroEvaluator())
            { PickupTraceFile = path };
            search.SharedState.AgentTraced += _ => search.SharedState.RequestStop();
            search.Search(in board, new SearchBudget(1000, 2000, 3, 3));
            string[] lines = System.IO.File.ReadAllLines(path);
            Assert.Contains(lines, line => line.Contains("\"type\":\"pickup\"") && line.Contains("\"EstimatedCost\""));
            Assert.Contains(lines, line => line.Contains("\"type\":\"batch\"") && line.Contains("\"Completed\":false"));
            Assert.Contains("\"type\":\"summary\"", lines[^1]);
            using (System.IO.File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            search.PickupTraceFile = string.Empty;
            search.Search(in board, new SearchBudget(1000, 2000, 3, 3));
            Assert.Equal(lines, System.IO.File.ReadAllLines(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Search_ExpiredBudgetReturnsLegalFallbackWithoutEvaluating()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 }, evaluator: new ThrowingEvaluator());
        Move best = search.Search(in board, new SearchBudget(0, 0, 8, 8));
        Assert.True(board.IsLegal(best));
        Assert.Equal(0, search.SeededRoots);
        Assert.Equal(-1, search.FirstBatchMs);
    }

    [Fact]
    public void Uci_SchedulingOptionsAreAdvertisedAndApplied()
    {
        using var output = new StringWriter();
        using var uci = new UciHandler(new StringReader(""), output);
        uci.HandleCommand("setoption name InterleaveRootSeeding value true");
        uci.HandleCommand("setoption name PickupCostWeight value 0");
        uci.HandleCommand("setoption name MaxSiblingExploration value 0");
        uci.HandleCommand("uci");
        Assert.Contains("option name InterleaveRootSeeding type check default true", output.ToString());
        Assert.Contains("option name PickupCostWeight type spin default 0 ", output.ToString());
        Assert.Contains("option name MaxSiblingExploration type spin default 1 ", output.ToString());
        Assert.Contains("option name PickupTraceFile type string", output.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Search_ConcurrentBatchesPublishLegalRootLines(int threads)
    {
        Board root = Fen.Parse(Fen.StartPosition);
        using var search = new ZapSearch(new ZapParams
        {
            DefaultThreads = threads,
            MaxSiblingExploration = 1
        }, evaluator: new C5Evaluator());
        int reports = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            search.Search(in root, new SearchBudget(100, 200, 4, 8), stats =>
            {
                Board replay = root;
                foreach (Move move in stats.Pv)
                {
                    Assert.True(replay.IsLegal(move), $"Illegal PV move {move.ToUci()}");
                    Assert.True(replay.MakeMove(move, out _));
                }
                reports++;
            });
        }
        Assert.True(reports >= 3);
    }

    [Fact]
    public void Worker_ComparedWithTerminalBaseline_DoesNotInventARefutationFromStaticScore()
    {
        var parameters = new ZapParams { DefaultThreads = 1, RootFilterMarginCp = 0, QuiescenceMaxPlies = 0 };
        var queue = new ZapWorkQueue(parameters);
        var state = new ZapSharedState();
        Board root = Fen.Parse(Fen.StartPosition);
        Move rootMove = LegalMove(in root, "e2e4");
        Board junction = root;
        Assert.True(junction.MakeMove(rootMove, out _));
        var original = Steps(junction, "e7e5");
        state.Reset([rootMove]);
        uint generation = state.NextGeneration();
        state.UpdateCandidateBaseline(0, 0);
        state.UpdateCandidateTrajectory(0, original);
        var evaluator = new ZeroEvaluator();
        using var pool = new ZapWorkerPool(1, queue, state, parameters, evaluator);
        pool.SetRootBoard(in root);
        var budget = new SearchBudget(1000, 2000, 2, 2);
        pool.StartExploration(Color.White, in budget);
        var cp = new ZapCrosspoint(junction, 0, 1, 1000, 0, CrosspointKind.AdversarialRefutation,
            1, original[0].Move, generation: generation);
        pool.ProcessCrosspoint(in cp, new ZapPolicy(parameters), evaluator);
        pool.StopExploration();
        Assert.Equal(int.MaxValue, state.GetCandidate(0).AgentCeiling);
    }

    [Fact]
    public void Uci_InvalidNnuePath_IsReportedAndPreventsStrengthSearch()
    {
        using var output = new StringWriter();
        using var uci = new UciHandler(new StringReader(""), output);
        uci.HandleCommand("uci");
        uci.HandleCommand("setoption name NnuePath value Z:\\missing-zanak-networks");
        uci.HandleCommand("go movetime 10");
        Assert.Contains("option name NnuePath", output.ToString());
        Assert.Contains("NNUE configuration error:", output.ToString());
        Assert.Contains("Search refused:", output.ToString());
        Assert.DoesNotContain("bestmove", output.ToString());
    }

    [Fact]
    public void NnuePath_CorruptModel_IsRejectedInsteadOfSilentlyUsingEmbeddedWeights()
    {
        var directory = Directory.CreateTempSubdirectory("zanak-nnue-");
        string path = Path.Combine(directory.FullName, "open.nnue");
        try
        {
            System.IO.File.WriteAllBytes(path, [1, 2, 3, 4]);
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 });
            Assert.Throws<InvalidDataException>(() => search.SetNnuePath(directory.FullName));
            Assert.True(search.NnueEvaluator!.IsActive);
        }
        finally
        {
            System.IO.File.Delete(path);
            Directory.Delete(directory.FullName);
        }
    }

    [Fact]
    public void Probe_ExplicitHorizon_DoesNotGrowByRepeatingExchangeResolution()
    {
        Board root = Fen.Parse("3r3k/8/8/3p4/4P3/8/8/K7 w - - 0 1");
        var parameters = new ZapParams { QuiescenceMaxPlies = 2 };
        var evaluator = new ZeroEvaluator();
        Board bounded = root;
        ZapTrajectory boundedTrajectory = default;
        ZapProbe.Trace(ref bounded, 1, new ZapPolicy(parameters), evaluator, Color.White,
            ref boundedTrajectory, parameters: parameters, maxPlies: 1);
        Board extended = root;
        ZapTrajectory extendedTrajectory = default;
        ZapProbe.Trace(ref extended, 1, new ZapPolicy(parameters), evaluator, Color.White,
            ref extendedTrajectory, parameters: parameters);

        Assert.Equal(1, boundedTrajectory.Count);
        Assert.True(extendedTrajectory.Count > boundedTrajectory.Count);
        Assert.Equal(boundedTrajectory[0].Move, extendedTrajectory[0].Move);
    }

    [Fact]
    public void Splice_StaleGeneration_CannotChangeTheNextSearch()
    {
        Board root = Fen.Parse(Fen.StartPosition);
        Move rootMove = LegalMove(in root, "e2e4");
        Assert.True(root.MakeMove(rootMove, out _));
        var original = Steps(root, "e7e5");
        var replacement = Steps(root, "c7c5");
        var state = new ZapSharedState();
        state.Reset([rootMove]);
        uint oldGeneration = state.NextGeneration();
        state.UpdateCandidateBaseline(0, 0);
        state.UpdateCandidateTrajectory(0, original);
        state.NextGeneration();
        Assert.False(state.SpliceTrajectoryTail(0, root.ZobristKey, -500, replacement[0].Move, true,
            replacement, oldGeneration, original[0].Move));
        Assert.Equal(0, state.GetCandidate(0).Score);
        Assert.Equal(original[0].Move, state.GetCandidate(0).Trajectory[0].Move);
    }

    [Fact]
    public void ZapTables_Invariants_MatchBoardGeometryAndMoveOrdering()
    {
        ZapTables.EnsureInitialized();

        // Rank & File masks
        ulong allRanks = 0UL;
        ulong allFiles = 0UL;
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(Bitboard.RankMasks[i], ZapTables.RankMasks[i]);
            Assert.Equal(Bitboard.FileMasks[i], ZapTables.FileMasks[i]);
            Assert.Equal(8, BitOperations.PopCount(ZapTables.RankMasks[i]));
            Assert.Equal(8, BitOperations.PopCount(ZapTables.FileMasks[i]));
            Assert.Equal(0UL, allRanks & ZapTables.RankMasks[i]);
            Assert.Equal(0UL, allFiles & ZapTables.FileMasks[i]);
            allRanks |= ZapTables.RankMasks[i];
            allFiles |= ZapTables.FileMasks[i];
        }
        Assert.Equal(~0UL, allRanks);
        Assert.Equal(~0UL, allFiles);

        // RelativeRankMasks
        for (int r = 0; r < 8; r++)
        {
            Assert.Equal(ZapTables.RankMasks[r], ZapTables.RelativeRankMasks[r]);
            Assert.Equal(ZapTables.RankMasks[7 - r], ZapTables.RelativeRankMasks[8 + r]);
            Assert.Equal(ZapTables.RankMasks[r], ZapTables.GetRelativeRankMask(Color.White, r));
            Assert.Equal(ZapTables.RankMasks[7 - r], ZapTables.GetRelativeRankMask(Color.Black, r));
        }
        Assert.Equal(0UL, ZapTables.GetRelativeRankMask(Color.White, -1));
        Assert.Equal(0UL, ZapTables.GetRelativeRankMask(Color.Black, 8));

        // PieceValues
        Assert.Equal([0, 100, 320, 330, 500, 900, 20000], ZapTables.PieceValues);
        for (int pt = 0; pt <= 6; pt++)
        {
            Assert.Equal(StaticExchangeEvaluator.GetPieceValue((PieceType)pt), ZapTables.GetPieceValue((PieceType)pt));
        }
        Assert.Equal(0, ZapTables.GetPieceValue((PieceType)99));

        // MvvLvaScores
        Assert.Equal(0, ZapTables.GetMvvLvaScore(PieceType.None, PieceType.Pawn));
        Assert.Equal(0, ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.None));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Queen, PieceType.Pawn) > ZapTables.GetMvvLvaScore(PieceType.Queen, PieceType.Queen));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Queen, PieceType.Queen) > ZapTables.GetMvvLvaScore(PieceType.Queen, PieceType.King));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Queen, PieceType.King) > ZapTables.GetMvvLvaScore(PieceType.Rook, PieceType.Pawn));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Rook, PieceType.Queen) > ZapTables.GetMvvLvaScore(PieceType.Bishop, PieceType.Pawn));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Bishop, PieceType.Pawn) > ZapTables.GetMvvLvaScore(PieceType.Knight, PieceType.Pawn));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Knight, PieceType.Queen) > ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Pawn));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Pawn) > ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Knight));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Knight) > ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Bishop));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Bishop) > ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Rook));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Rook) > ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Queen));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.Queen) > ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.King));
        Assert.True(ZapTables.GetMvvLvaScore(PieceType.Pawn, PieceType.King) > 0);

        // KingRingMasks & Distances
        Assert.Equal(3, BitOperations.PopCount(ZapTables.GetKingRingMask(Square.A1)));
        Assert.Equal(5, BitOperations.PopCount(ZapTables.GetKingRingMask(Square.D1)));
        Assert.Equal(8, BitOperations.PopCount(ZapTables.GetKingRingMask(Square.E4)));
        Assert.Equal(7, ZapTables.GetChebyshevDistance(Square.A1, Square.H8));
        Assert.Equal(14, ZapTables.GetManhattanDistance(Square.A1, Square.H8));
        Assert.Equal(1, ZapTables.GetChebyshevDistance(Square.E4, Square.F5));
        Assert.Equal(2, ZapTables.GetManhattanDistance(Square.E4, Square.F5));
        Assert.Equal(1, ZapTables.GetChebyshevDistance(Square.E4, Square.E5));
        Assert.Equal(1, ZapTables.GetManhattanDistance(Square.E4, Square.E5));

        for (int sq1 = 0; sq1 < 64; sq1++)
        {
            ulong ring = ZapTables.KingRingMasks[sq1];
            Assert.Equal(Bitboard.KingAttacks[sq1], ring);
            Assert.Equal(0UL, ring & (1UL << sq1));
            Assert.Equal(0, ZapTables.ChebyshevDistance[(sq1 * 64) + sq1]);
            Assert.Equal(0, ZapTables.ManhattanDistance[(sq1 * 64) + sq1]);

            ulong temp = ring;
            while (temp != 0)
            {
                Square neighbor = Bitboard.PopLsb(ref temp);
                Assert.Equal(1, ZapTables.GetChebyshevDistance((Square)sq1, neighbor));
            }

            for (int sq2 = 0; sq2 < 64; sq2++)
            {
                Assert.Equal(ZapTables.ChebyshevDistance[(sq1 * 64) + sq2], ZapTables.ChebyshevDistance[(sq2 * 64) + sq1]);
                Assert.Equal(ZapTables.ManhattanDistance[(sq1 * 64) + sq2], ZapTables.ManhattanDistance[(sq2 * 64) + sq1]);
            }
        }

        // PawnForwardSpanMasks
        Assert.Equal(0UL, ZapTables.GetPawnForwardSpanMask(Color.White, Square.A8));
        Assert.Equal(0UL, ZapTables.GetPawnForwardSpanMask(Color.White, Square.E8));
        Assert.Equal(0UL, ZapTables.GetPawnForwardSpanMask(Color.Black, Square.A1));
        Assert.Equal(0UL, ZapTables.GetPawnForwardSpanMask(Color.Black, Square.E1));

        ulong whiteE2Span = ZapTables.GetPawnForwardSpanMask(Color.White, Square.E2);
        Assert.Equal(18, BitOperations.PopCount(whiteE2Span));
        Assert.Equal(0UL, whiteE2Span & (ZapTables.RankMasks[0] | ZapTables.RankMasks[1]));
        Assert.Equal(
            ZapTables.FileMasks[3] | ZapTables.FileMasks[4] | ZapTables.FileMasks[5],
            whiteE2Span | ((ZapTables.RankMasks[0] | ZapTables.RankMasks[1]) & (ZapTables.FileMasks[3] | ZapTables.FileMasks[4] | ZapTables.FileMasks[5])));
        Assert.Equal(12, BitOperations.PopCount(ZapTables.GetPawnForwardSpanMask(Color.White, Square.A2)));

        ulong blackE7Span = ZapTables.GetPawnForwardSpanMask(Color.Black, Square.E7);
        Assert.Equal(18, BitOperations.PopCount(blackE7Span));
        Assert.Equal(0UL, blackE7Span & (ZapTables.RankMasks[6] | ZapTables.RankMasks[7]));
        Assert.Equal(12, BitOperations.PopCount(ZapTables.GetPawnForwardSpanMask(Color.Black, Square.H7)));
    }

    [Fact]
    public void ZapSearch_Warmup_PreservesTraceFilesResetsStateAndSubsequentSearchWorks()
    {
        string searchTracePath = Path.Combine(Path.GetTempPath(), $"zanak-warmup-trace-{Guid.NewGuid():N}.jsonl");
        string pickupTracePath = Path.Combine(Path.GetTempPath(), $"zanak-warmup-pickup-{Guid.NewGuid():N}.jsonl");
        System.IO.File.WriteAllText(searchTracePath, "preserve-search-trace");
        System.IO.File.WriteAllText(pickupTracePath, "preserve-pickup-trace");

        try
        {
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 2 })
            {
                SearchTraceFile = searchTracePath,
                PickupTraceFile = pickupTracePath
            };

            Assert.False(search.IsWarmedUp);
            search.Warmup();

            Assert.True(search.IsWarmedUp);
            Assert.Equal(searchTracePath, search.SearchTraceFile);
            Assert.Equal(pickupTracePath, search.PickupTraceFile);
            Assert.Equal("preserve-search-trace", System.IO.File.ReadAllText(searchTracePath));
            Assert.Equal("preserve-pickup-trace", System.IO.File.ReadAllText(pickupTracePath));
            Assert.Equal(0, search.SharedState.TotalNodes);
            Assert.Equal(0, search.SharedState.CandidateCount);
            Assert.Equal(0, search.WorkQueue.Count);
            Assert.Equal(0, search.WorkQueue.TotalEntries);
            Assert.Equal(0, search.RootFilterMs);
            Assert.Equal(0, search.SeedingMs);
            Assert.Equal(0, search.SeededRoots);
            Assert.Equal(-1, search.FirstBatchMs);
            Assert.Equal(string.Empty, search.LastSearchDiagnostics);

            search.EnsureWarmedUp();
            Assert.True(search.IsWarmedUp);

            search.SetWorkerThreads(1);
            Assert.False(search.IsWarmedUp);
            search.EnsureWarmedUp();
            Assert.True(search.IsWarmedUp);

            search.SearchTraceFile = string.Empty;
            search.PickupTraceFile = string.Empty;

            Board board = Fen.Parse(Fen.StartPosition);
            Move best = search.Search(in board, new SearchBudget(80, 200, 4, 8));
            Assert.False(best.IsNull);
            Assert.True(board.IsLegal(in best));
        }
        finally
        {
            System.IO.File.Delete(searchTracePath);
            System.IO.File.Delete(pickupTracePath);
        }
    }

    [Fact]
    public void NnuePath_CorruptAnakMoeModel_IsRejectedInsteadOfSilentlyUsingEmbeddedWeights()
    {
        var directory = Directory.CreateTempSubdirectory("zanak-moe-corrupt-");
        string path = Path.Combine(directory.FullName, "anak.nnue");
        try
        {
            System.IO.File.WriteAllBytes(path, [1, 2, 3, 4]);
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 });
            Assert.Throws<InvalidDataException>(() => search.SetNnuePath(directory.FullName));
            Assert.True(search.NnueEvaluator!.IsActive);
        }
        finally
        {
            System.IO.File.Delete(path);
            Directory.Delete(directory.FullName);
        }
    }

    [Fact]
    public void NnuePath_SingleMoeDirectory_LoadsWithoutLegacySixExperts()
    {
        string sourceMoe = Path.Combine(AppContext.BaseDirectory, "networks", "anak.nnue");
        Assert.True(System.IO.File.Exists(sourceMoe), $"Expected unified MoE model at {sourceMoe}");

        var directory = Directory.CreateTempSubdirectory("zanak-moe-only-");
        string destMoe = Path.Combine(directory.FullName, "anak.nnue");
        try
        {
            System.IO.File.Copy(sourceMoe, destMoe);
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 });
            search.Warmup();
            Assert.True(search.IsWarmedUp);

            search.SetNnuePath(directory.FullName);
            Assert.False(search.IsWarmedUp);
            Assert.True(search.NnueEvaluator!.IsActive);
            Assert.True(search.NnueEvaluator.IsUnifiedMoE);

            search.EnsureWarmedUp();
            Assert.True(search.IsWarmedUp);

            Board board = Fen.Parse(Fen.StartPosition);
            Move best = search.Search(in board, new SearchBudget(60, 150, 3, 6));
            Assert.True(board.IsLegal(in best));
        }
        finally
        {
            System.IO.File.Delete(destMoe);
            Directory.Delete(directory.FullName);
        }
    }

    [Fact]
    public async Task Uci_IsReadyAndGoMovetime_RespondsReadyOkAndLegalBestMove()
    {
        using var output = new SignalStringWriter();
        using var uci = new UciHandler(new StringReader(""), output);

        uci.HandleCommand("uci");
        Assert.Contains("uciok", output.Snapshot());

        uci.HandleCommand("isready");
        Assert.Equal(1, output.Snapshot().Count(l => l == "readyok"));

        uci.HandleCommand("isready");
        Assert.Equal(2, output.Snapshot().Count(l => l == "readyok"));

        uci.HandleCommand("position startpos");
        uci.HandleCommand("go movetime 50");

        string bestMoveLine = await output.WaitForLineAsync(
            l => l.StartsWith("bestmove ", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        string moveUci = bestMoveLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        Board start = Fen.Parse(Fen.StartPosition);
        Assert.True(start.IsLegal(LegalMove(in start, moveUci)));
    }

    [Fact]
    public async Task Uci_IsReadyDuringActiveInfiniteSearch_RespondsImmediatelyWithoutBlocking()
    {
        using var output = new SignalStringWriter();
        using var uci = new UciHandler(new StringReader(""), output);

        uci.HandleCommand("uci");
        uci.HandleCommand("isready");
        uci.HandleCommand("position startpos");
        uci.HandleCommand("go infinite");
        uci.HandleCommand("isready");

        Assert.Equal(2, output.Snapshot().Count(l => l == "readyok"));

        uci.HandleCommand("stop");
        string bestMoveLine = await output.WaitForLineAsync(
            l => l.StartsWith("bestmove ", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        string moveUci = bestMoveLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        Board start = Fen.Parse(Fen.StartPosition);
        Assert.True(start.IsLegal(LegalMove(in start, moveUci)));
    }

    private sealed class SignalStringWriter : TextWriter
    {
        private readonly object _lock = new();
        private readonly List<string> _lines = [];
        private readonly SemaphoreSlim _lineSignal = new(0);

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                _lines.Add(value ?? string.Empty);
            }
            _lineSignal.Release();
        }

        public string[] Snapshot()
        {
            lock (_lock) return [.. _lines];
        }

        public async Task<string> WaitForLineAsync(Func<string, bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (_lock)
                {
                    foreach (string line in _lines)
                    {
                        if (predicate(line)) return line;
                    }
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timed out waiting for matching UCI output line. Captured:\n{string.Join('\n', Snapshot())}");
                }

                await _lineSignal.WaitAsync(remaining);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lineSignal.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void GetSecondBestCandidate_WhenAllOthersDead_ReturnsSentinelScore()
    {
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);
        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());

        sharedState.UpdateCandidateBaseline(0, -50);
        sharedState.UpdateCandidateScreen(0, -50);
        for (int i = 1; i < moves.Count; i++)
        {
            sharedState.UpdateCandidateScreen(i, -30000);
        }
        sharedState.SortCandidatesAndPrune(hardAspirationMarginCp: 100);

        ZapRootCandidate second = sharedState.GetSecondBestCandidate();
        Assert.Equal(Move.Null, second.Move);
        Assert.Equal(-30000, second.Score);
    }

    [Fact]
    public void Search_SingleRootFilterSurvivor_ExitsFastAndPlaysRecapture()
    {
        // >= 7 pieces so Syzygy tablebase does not resolve at root before ZapRootFilter runs.
        Board board = Fen.Parse("3q4/5pkp/6p1/8/8/6P1/5P1P/3R2K1 w - - 0 1");
        using var search = new ZapSearch();
        search.EnsureWarmedUp();
        var budget = new SearchBudget(SoftLimitMs: 1000, HardLimitMs: 2000, TargetBaseDepth: 8, MaxDepthHorizon: 14, ExtendHorizonWithTime: true);

        Move best = search.Search(in board, in budget);

        Assert.Equal("d1d8", best.ToUci());
        Assert.Contains("zap exit single-survivor", search.LastSearchDiagnostics);
        Assert.True(search.TimeManager.ElapsedMilliseconds < 250);
    }

    [Fact]
    public void WorkQueue_TryDequeue_ExcludedRoot_SkipsExcludedHeap()
    {
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);
        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());
        sharedState.UpdateCandidateBaseline(0, 500);
        sharedState.UpdateCandidateBaseline(1, 0);

        Board board0 = board;
        Assert.True(board0.MakeMove(moves[0], out _));
        Board board1 = board;
        Assert.True(board1.MakeMove(moves[1], out _));

        var queue = new ZapWorkQueue(ZapParams.Default);
        queue.Enqueue(new ZapCrosspoint(board0, 0, 1, 500, 100, CrosspointKind.AdversarialRefutation, 4, moves[0], 23, 1, ZapAgentRole.Refutation));
        queue.Enqueue(new ZapCrosspoint(board1, 1, 1, 0, 100, CrosspointKind.AdversarialRefutation, 4, moves[1], 23, 1, ZapAgentRole.Refutation));

        Assert.True(queue.TryDequeue(out ZapCrosspoint picked, sharedState, 1, excludedRoot: 0));
        Assert.Equal(1, picked.RootCandidateIndex);
    }

    private sealed class ZeroEvaluator : IEvaluator
    {
        public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves) => (Value)0;
    }

    private sealed class ThrowingEvaluator : IEvaluator
    {
        public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves) =>
            throw new InvalidOperationException("Expired search must not evaluate.");
    }

    private sealed class C5Evaluator : IEvaluator
    {
        public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves)
        {
            int score = board.GetPieceAt(Square.C5) == Piece.BlackPawn ? -500 : 0;
            return (Value)(board.SideToMove == Color.White ? score : -score);
        }
    }

    private static Move LegalMove(in Board board, string uci)
    {
        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        return Assert.Single(moves.AsSpan().ToArray(), move => move.ToUci() == uci);
    }

    private static ZapTrajectoryStep[] Steps(Board board, params string[] moves)
    {
        var steps = new List<ZapTrajectoryStep>();
        foreach (string uci in moves)
        {
            Move move = LegalMove(in board, uci);
            ulong key = board.ZobristKey;
            Color side = board.SideToMove;
            Assert.True(board.MakeMove(move, out BoardState state));
            steps.Add(new ZapTrajectoryStep(state, key, move, side, 0, 0, 0));
        }
        return steps.ToArray();
    }
}
