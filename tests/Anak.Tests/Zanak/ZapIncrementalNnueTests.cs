using Anak.Core;
using Anak.Data.Nnue;
using Xunit;
using Zanak.Search;

namespace Anak.Tests.Zanak;

public sealed class ZapIncrementalNnueTests
{
    /// <summary>Forwards to the NNUE but is not incremental, so ZapProbe cannot push accumulators.</summary>
    private sealed class FullRefreshEvaluator(ZanakNnueEvaluator inner) : IEvaluator
    {
        public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves) => inner.Evaluate(in board, legalMoves);
    }

    private static Move FindLegalMove(in Board board, string uci)
    {
        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        return Assert.Single(moves.AsSpan().ToArray(), m => m.ToUci() == uci);
    }

    private static void ApplyUciMove(ref Board board, ZanakNnueEvaluator zanak, MultiTierNnueEvaluator reference, string uci)
    {
        Move move = FindLegalMove(in board, uci);
        Piece movedPiece = board.GetPieceAt(move.From);
        Assert.True(board.MakeMove(move, out BoardState state, movedPiece));
        zanak.PushMove(move, movedPiece, state.CapturedPiece, in board);
        reference.PushMove(move, movedPiece, state.CapturedPiece, in board);
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r1bqkb1r/pppp1ppp/2n2n2/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 4 4")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    public void IncrementalAccumulator_MatchesFullRefresh_AlongSpine(string fen)
    {
        using var nnue = new ZanakNnueEvaluator();
        Assert.True(nnue.IsActive, "NNUE parity tests require active networks.");

        Board root = Fen.Parse(fen);
        nnue.SetRootTopology(in root);

        var policy = new ZapPolicy();
        var reference = new FullRefreshEvaluator(nnue);

        Board incBoard = root;
        ZapTrajectory incTraj = default;
        int incScore = ZapProbe.Trace(ref incBoard, 12, policy, nnue, root.SideToMove, ref incTraj);

        Board refBoard = root;
        ZapTrajectory refTraj = default;
        int refScore = ZapProbe.Trace(ref refBoard, 12, policy, reference, root.SideToMove, ref refTraj);

        Assert.Equal(refScore, incScore);
        Assert.Equal(refTraj.Count, incTraj.Count);
        for (int i = 0; i < refTraj.Count; i++)
        {
            Assert.Equal(refTraj[i].Move, incTraj[i].Move);
            Assert.Equal(refTraj[i].StaticEval, incTraj[i].StaticEval);
        }
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "e2e4")]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1", "e7e5")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1g1")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R b KQkq - 0 1", "e8c8")]
    [InlineData("7k/8/8/3pP3/8/8/8/K7 w - d6 0 1", "e5d6")]
    [InlineData("7k/8/8/8/3Pp3/8/8/K7 b - d3 0 1", "e4d3")]
    [InlineData("7k/P7/8/8/8/8/8/K7 w - - 0 1", "a7a8q")]
    [InlineData("7k/8/8/8/8/8/p7/7K b - - 0 1", "a2a1n")]
    public void MasterWorkerAndFullRefresh_MatchAfterSpecialMoves(string fen, string uci)
    {
        Board root = Fen.Parse(fen);
        using var master = new ZanakNnueEvaluator();
        Assert.True(master.IsActive, "NNUE parity tests require active networks.");
        master.SetRootTopology(in root);
        using var worker = Assert.IsType<ZanakNnueEvaluator>(master.CreateWorkerInstance());
        using var full = Assert.IsType<ZanakNnueEvaluator>(master.CreateWorkerInstance());
        master.ResetAccumulatorStack(in root);
        worker.ResetAccumulatorStack(in root);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in root, ref moves);
        Move move = Assert.Single(moves.AsSpan().ToArray(), m => m.ToUci() == uci);
        Piece piece = root.GetPieceAt(move.From);
        Board child = root;
        Assert.True(child.MakeMove(move, out BoardState state));
        master.PushMove(move, piece, state.CapturedPiece, in child);
        worker.PushMove(move, piece, state.CapturedPiece, in child);

        Assert.Equal(master.ActiveRootExpert, worker.ActiveRootExpert);
        Assert.Equal(full.Evaluate(in child), master.Evaluate(in child));
        Assert.Equal(full.Evaluate(in child), worker.Evaluate(in child));
    }

    [Fact]
    public void UnifiedMoEProperties_MatchMultiTierNnueEvaluator()
    {
        using var expected = new MultiTierNnueEvaluator();
        using var actual = new ZanakNnueEvaluator();

        Assert.True(expected.IsActive, "NNUE parity tests require active networks.");
        Assert.True(actual.IsActive);
        Assert.Equal(expected.IsUnifiedMoE, actual.IsUnifiedMoE);
        Assert.Equal(expected.HasMoeModel, actual.HasMoeModel);
        Assert.True(actual.IsUnifiedMoE);
        Assert.True(actual.HasMoeModel);
        Assert.Equal(expected.NnueDirectory, actual.NnueDirectory);

        IMultiTierEvaluator multiTier = actual;
        Assert.Equal(expected.IsUnifiedMoE, multiTier.IsUnifiedMoE);
    }

    [Theory]
    [InlineData("r1bqkbnr/pp2pppp/2n5/8/4P3/8/PP3PPP/RNBQKB1R w KQkq - 0 6", NnueExpertType.Open, false)]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", NnueExpertType.Closed, false)]
    [InlineData("rnbqkb1r/pp2pppp/2p2n2/3p4/2PP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 0 4", NnueExpertType.Closed, false)]
    [InlineData("r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4", NnueExpertType.Open, true)]
    [InlineData("rnbqkbnr/ppp2ppp/8/3p4/3PP3/8/1PP2PPP/RNBQKBNR w KQkq - 0 3", NnueExpertType.Closed, true)]
    [InlineData("4k3/pp3ppp/8/8/8/8/PP3PPP/4K3 w - - 0 30", NnueExpertType.Endgame, false)]
    [InlineData("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1", NnueExpertType.Queenless, false)]
    [InlineData("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", NnueExpertType.Imbalance, false)]
    [InlineData("1nbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQK1NR w KQk - 0 1", NnueExpertType.Imbalance, false)]
    [InlineData("r1bq1rk1/ppp1ppbp/2np1np1/8/3PP3/2N1BP2/PPPQ2PP/2KR1BNR w - - 3 8", NnueExpertType.Attack, false)]
    public void ExactCentipawnParity_AcrossAllTopologiesAndBlendZone(
        string fen,
        NnueExpertType expectedExpert,
        bool expectBlendZone)
    {
        Board board = Fen.Parse(fen);
        Assert.Equal(expectedExpert, PawnTopology.ClassifyExpert(in board));

        int opennessPct = PawnTopology.CalculateOpennessPercent(in board);
        if (expectBlendZone)
        {
            Assert.InRange(opennessPct, 43, 57);
        }
        else if (expectedExpert is NnueExpertType.Open or NnueExpertType.Closed)
        {
            Assert.True(opennessPct <= 42 || opennessPct >= 58, $"Expected openness outside blend zone, got {opennessPct}");
        }

        MoveList legalMoves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);

        using var reference = new MultiTierNnueEvaluator();
        using var zanak = new ZanakNnueEvaluator();
        Assert.True(reference.IsActive);
        Assert.True(zanak.IsActive);

        // 1. Fresh static evaluation before SetRootTopology
        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);
        Assert.Equal(reference.Evaluate(in board, legalMoves.AsReadOnlySpan()).Raw, zanak.Evaluate(in board, legalMoves.AsReadOnlySpan()).Raw);

        // 2. After SetRootTopology (stack miss / full refresh path)
        reference.SetRootTopology(in board);
        zanak.SetRootTopology(in board);
        Assert.Equal(expectedExpert, zanak.ActiveRootExpert);
        Assert.Equal(reference.ActiveRootExpert, zanak.ActiveRootExpert);
        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);

        // 3. After ResetAccumulatorStack / InitSearch (stack hit path at depth 0)
        reference.InitSearch(in board);
        zanak.ResetAccumulatorStack(in board);
        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);

        // 4. After InitSearch directly
        zanak.InitSearch(in board);
        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);
        Assert.Equal(reference.Evaluate(in board, legalMoves.AsReadOnlySpan()).Raw, zanak.Evaluate(in board, legalMoves.AsReadOnlySpan()).Raw);
    }

    [Theory]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", new[] { "e1g1", "e8c8", "d5e6", "f7e6", "f3f6", "g7f6" })]
    [InlineData("4k2r/1P3ppp/8/3pP3/8/8/5PPP/R3K2R w KQk d6 0 1", new[] { "e5d6", "e8g8", "b7b8q", "h7h6", "e1c1" })]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", new[] { "e2e4", "c7c5", "g1f3", "d7d6", "d2d4", "c5d4", "f3d4" })]
    [InlineData("8/P5kp/8/8/8/8/2p4P/R3K2R w KQ - 0 1", new[] { "a7a8q", "c2c1n", "e1g1", "g7g6", "a1c1" })]
    public void IncrementalPushMoveSequences_MatchMultiTierEvaluatorAtEveryPly(string rootFen, string[] uciMoves)
    {
        Board board = Fen.Parse(rootFen);
        using var reference = new MultiTierNnueEvaluator();
        using var zanak = new ZanakNnueEvaluator();
        using var staticReference = new MultiTierNnueEvaluator();

        reference.InitSearch(in board);
        zanak.SetRootTopology(in board);
        zanak.ResetAccumulatorStack(in board);

        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);
        Assert.Equal(staticReference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);

        foreach (string uci in uciMoves)
        {
            ApplyUciMove(ref board, zanak, reference, uci);

            int expectedCp = reference.Evaluate(in board).Raw;
            int actualCp = zanak.Evaluate(in board).Raw;
            int staticCp = staticReference.Evaluate(in board).Raw;

            Assert.Equal(expectedCp, actualCp);
            Assert.Equal(staticCp, actualCp);
        }
    }

    [Fact]
    public void PushNullMoveAndRollback_MatchMultiTierEvaluator()
    {
        Board board = Fen.Parse("r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4");
        using var reference = new MultiTierNnueEvaluator();
        using var zanak = new ZanakNnueEvaluator();

        reference.InitSearch(in board);
        zanak.InitSearch(in board);
        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);

        // Push a real move to depth 1
        ApplyUciMove(ref board, zanak, reference, "g8f6");
        int depth1Eval = zanak.Evaluate(in board).Raw;
        Assert.Equal(reference.Evaluate(in board).Raw, depth1Eval);

        // Push null move to depth 2
        Board afterNull = board;
        Assert.True(afterNull.MakeNullMove(out BoardState nullState));
        zanak.PushNullMove(in afterNull);
        reference.PushNullMove(in afterNull);
        Assert.Equal(reference.Evaluate(in afterNull).Raw, zanak.Evaluate(in afterNull).Raw);

        // Push another real move after null move to depth 3
        Board afterNullChild = afterNull;
        ApplyUciMove(ref afterNullChild, zanak, reference, "e7e5");
        Assert.Equal(reference.Evaluate(in afterNullChild).Raw, zanak.Evaluate(in afterNullChild).Raw);

        // Roll back to depth 1 and verify depth-1 accumulator state is preserved
        zanak.RollbackTo(1);
        reference.RollbackTo(1);
        Assert.Equal(depth1Eval, zanak.Evaluate(in board).Raw);
        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);
    }

    [Fact]
    public void StackMiss_EvaluatesCorrectMoEExpertAndBlendZone()
    {
        // Pin Closed startpos on the stack, then evaluate boards of every other topology WITHOUT ResetAccumulatorStack/PushMove
        Board start = Fen.Parse(Fen.StartPosition);
        using var reference = new MultiTierNnueEvaluator();
        using var zanak = new ZanakNnueEvaluator();

        reference.InitSearch(in start);
        zanak.InitSearch(in start);

        string[] missFens =
        [
            "r1bqkbnr/pp2pppp/2n5/8/4P3/8/PP3PPP/RNBQKB1R w KQkq - 0 6",        // Open
            "r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4",     // Open/Closed blend (open side)
            "rnbqkbnr/ppp2ppp/8/3p4/3PP3/8/PP3PPP/RNBQKBNR w KQkq - 0 3",       // Open/Closed blend (closed side)
            "4k3/pp3ppp/8/8/8/8/PP3PPP/4K3 w - - 0 30",                         // Endgame
            "rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1",         // Queenless
            "rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",         // Imbalance
            "r1bq1rk1/ppp1ppbp/2np1np1/8/3PP3/2N1BP2/PPPQ2PP/2KR1BNR w - - 3 8" // Attack
        ];

        foreach (string fen in missFens)
        {
            Board target = Fen.Parse(fen);
            Assert.Equal(reference.Evaluate(in target).Raw, zanak.Evaluate(in target).Raw);
        }
    }

    [Fact]
    public void CreateWorkerInstance_MatchesMasterAndMultiTierWorkerAcrossTopologies()
    {
        Board root = Fen.Parse("r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4");
        using var refMaster = new MultiTierNnueEvaluator();
        using var refWorker = Assert.IsType<MultiTierNnueEvaluator>(refMaster.CreateWorkerInstance());
        using var zanakMaster = new ZanakNnueEvaluator();

        // Create worker before SetRootTopology, then sync independently (matches ZapWorkerPool lifecycle)
        using var zanakWorker = Assert.IsType<ZanakNnueEvaluator>(zanakMaster.CreateWorkerInstance());

        refMaster.InitSearch(in root);
        refWorker.InitSearch(in root);
        zanakMaster.InitSearch(in root);
        zanakWorker.SetRootTopology(in root);
        zanakWorker.ResetAccumulatorStack(in root);

        Assert.Equal(zanakMaster.IsUnifiedMoE, zanakWorker.IsUnifiedMoE);
        Assert.Equal(zanakMaster.HasMoeModel, zanakWorker.HasMoeModel);
        Assert.Equal(zanakMaster.ActiveRootExpert, zanakWorker.ActiveRootExpert);
        Assert.Equal(refWorker.Evaluate(in root).Raw, zanakWorker.Evaluate(in root).Raw);

        // Diverge worker stack from master stack: master advances e7e5, worker advances g8f6 + null move
        Board masterBoard = root;
        ApplyUciMove(ref masterBoard, zanakMaster, refMaster, "e7e5");

        Board workerBoard = root;
        ApplyUciMove(ref workerBoard, zanakWorker, refWorker, "g8f6");
        Assert.Equal(refMaster.Evaluate(in masterBoard).Raw, zanakMaster.Evaluate(in masterBoard).Raw);
        Assert.Equal(refWorker.Evaluate(in workerBoard).Raw, zanakWorker.Evaluate(in workerBoard).Raw);

        Assert.True(workerBoard.MakeNullMove(out _));
        zanakWorker.PushNullMove(in workerBoard);
        refWorker.PushNullMove(in workerBoard);
        Assert.Equal(refWorker.Evaluate(in workerBoard).Raw, zanakWorker.Evaluate(in workerBoard).Raw);

        // Verify master stack remained uncorrupted by worker operations
        Assert.Equal(refMaster.Evaluate(in masterBoard).Raw, zanakMaster.Evaluate(in masterBoard).Raw);
    }

    [Fact]
    public void UseNnueDisabled_MatchesMultiTierFallback()
    {
        Board board = Fen.Parse("r1bq1rk1/ppp1ppbp/2np1np1/8/3PP3/2N1BP2/PPPQ2PP/2KR1BNR w - - 3 8");
        MoveList legalMoves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);

        using var reference = new MultiTierNnueEvaluator { UseNnue = false };
        using var zanak = new ZanakNnueEvaluator();
        zanak.Networks.UseNnue = false;
        zanak.SetRootTopology(in board);
        zanak.ResetAccumulatorStack(in board);

        Assert.Equal(reference.Evaluate(in board).Raw, zanak.Evaluate(in board).Raw);
        Assert.Equal(
            reference.Evaluate(in board, legalMoves.AsReadOnlySpan()).Raw,
            zanak.Evaluate(in board, legalMoves.AsReadOnlySpan()).Raw);
    }

    [Fact]
    public void CreateWorkerInstance_ConcurrentEvaluationsAcrossTopologies_AreExactAndRaceFree()
    {
        (string Fen, NnueExpertType Expert)[] cases =
        [
            ("r1bqkbnr/pp2pppp/2n5/8/4P3/8/PP3PPP/RNBQKB1R w KQkq - 0 6", NnueExpertType.Open),
            ("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", NnueExpertType.Closed),
            ("r1bqkbnr/pp1ppppp/2n5/8/3NP3/8/PPP2PPP/RNBQKB1R b KQkq - 0 4", NnueExpertType.Open),
            ("4k3/pp3ppp/8/8/8/8/PP3PPP/4K3 w - - 0 30", NnueExpertType.Endgame),
            ("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1", NnueExpertType.Queenless),
            ("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", NnueExpertType.Imbalance),
            ("r1bq1rk1/ppp1ppbp/2np1np1/8/3PP3/2N1BP2/PPPQ2PP/2KR1BNR w - - 3 8", NnueExpertType.Attack)
        ];

        using var reference = new MultiTierNnueEvaluator();
        int[] expectedScores = new int[cases.Length];
        for (int i = 0; i < cases.Length; i++)
        {
            Board b = Fen.Parse(cases[i].Fen);
            reference.InitSearch(in b);
            expectedScores[i] = reference.Evaluate(in b).Raw;
        }

        using var master = new ZanakNnueEvaluator();
        Parallel.For(0, 4, workerIdx =>
        {
            using var worker = Assert.IsType<ZanakNnueEvaluator>(master.CreateWorkerInstance());
            for (int iter = 0; iter < 50; iter++)
            {
                int idx = (workerIdx + iter) % cases.Length;
                Board b = Fen.Parse(cases[idx].Fen);
                worker.InitSearch(in b);
                Assert.Equal(cases[idx].Expert, worker.ActiveRootExpert);
                Assert.Equal(expectedScores[idx], worker.Evaluate(in b).Raw);
            }
        });
    }
}
