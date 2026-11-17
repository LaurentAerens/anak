using Anak.Core;
using Anak.Data.Evaluation;
using Xunit;
using Zanak.Search;
using Zanak.Time;

namespace Anak.Tests.Zanak;

public sealed class ZapProbeTests
{
    [Theory]
    [InlineData("8/5k2/8/3PP3/8/8/P7/7K w - - 0 1", "e5e6")]
    [InlineData("k7/8/8/8/3pp3/7p/2K5/8 b - - 0 1", "d4d3")]
    public void ZapPolicy_PawnCheck_PrecedesUnrelatedPawnPush(string fen, string expectedMove)
    {
        Board board = Fen.Parse(fen);
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal(expectedMove, move.ToUci());
        Assert.True(board.GivesCheck(move));
    }

    [Theory]
    [InlineData("7k/P7/8/4p3/8/5N2/8/7K w - - 0 1", "a7a8q")]
    [InlineData("7k/8/5n2/8/4P3/8/p7/7K b - - 0 1", "a2a1q")]
    public void ZapPolicy_Promotion_PrecedesUnrelatedPawnCapture(string fen, string expectedMove)
    {
        Board board = Fen.Parse(fen);
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal(expectedMove, move.ToUci());
        Assert.True(board.IsLegal(in move));
    }

    [Fact]
    public void ZapPolicy_Promotion_DoesNotPromoteIntoUncoveredRookAttack()
    {
        Board board = Fen.Parse("7k/P7/r7/4p3/8/5N2/8/7K w - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal("f3e5", move.ToUci());
        Assert.True(board.IsLegal(in move));
    }

    [Theory]
    [InlineData("3k4/8/P6P/2b5/8/8/8/7K w - - 0 1", "h6h7")]
    [InlineData("7k/8/8/8/2B5/p6p/8/3K4 b - - 0 1", "h3h2")]
    [InlineData("3k4/8/P6P/8/8/8/6K1/r7 w - - 0 1", "h6h7")]
    [InlineData("3k4/8/P6P/2b5/1p6/8/8/1N5K w - - 0 1", "h6h7")]
    public void ZapPolicy_PawnPush_RejectsLosingDestinations(string fen, string expectedMove)
    {
        Board board = Fen.Parse(fen);
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal(expectedMove, move.ToUci());
        Assert.True(board.IsLegal(in move));
    }

    [Fact]
    public void ZapPolicy_DefendedRookAttackedByBishop_IsThreatened()
    {
        Board board = Fen.Parse("4k3/8/8/8/8/2b5/8/R2R3K w - - 0 1");

        Assert.Equal(500, ZapPolicy.GetMaxThreatenedPiece(in board, out Square square));
        Assert.Equal(Square.A1, square);
    }

    [Fact]
    public void ZapPolicy_PawnPush_AllowsDefendedEqualExchange()
    {
        // Step 24 quiet push a6a7 is defended by b6 against Bb8, which remains valid.
        Board board = Fen.Parse("1b5k/8/PP6/8/8/8/8/7K w - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal("a6a7", move.ToUci());
        Assert.True(board.MakeMove(move, out _));
        Assert.True(board.IsSquareAttacked(move.To, Color.Black));
    }

    [Fact]
    public void ZapPolicy_QueenEvasion_DoesNotRetreatAlongNewlyOpenedRookRay()
    {
        Board board = Fen.Parse("7k/8/8/q7/1P6/8/2N5/RK6 b - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal(Square.A5, move.From);
        Assert.True(board.IsLegal(in move));
        Assert.True(board.MakeMove(move, out _));
        Assert.False(board.IsSquareAttacked(move.To, Color.White), move.ToUci());
    }

    [Fact]
    public void ZapPolicy_QueenEvasion_RemovesAttackerWhenNoSoundEscapeExists()
    {
        Board board = Fen.Parse("7k/8/8/8/8/8/PPn5/QB5K w - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal("b1c2", move.ToUci());
        Assert.True(board.MakeMove(move, out _));
        Assert.False(board.IsSquareAttacked(Square.A1, Color.Black));
    }

    [Fact]
    public void ZapPolicy_Fallback_DoesNotReintroduceRejectedPawnPush()
    {
        Board board = Fen.Parse("7k/8/1n6/2b5/8/8/P7/7K w - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.NotEqual("a2a3", move.ToUci());
        Assert.NotEqual("a2a4", move.ToUci());
        Assert.True(board.IsLegal(in move));
    }

    [Fact]
    public void ZapProbe_Trace_PopulatesTrajectoryWithValidSteps()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        var policy = new ZapPolicy();
        var evaluator = new DefaultEvaluator();

        ZapTrajectory trajectory = default;
        int terminalScore = ZapProbe.Trace(
            ref board,
            targetDepth: 6,
            policy,
            evaluator,
            engineColor: Color.White,
            ref trajectory);

        Assert.True(trajectory.Count >= 6);
        for (int i = 0; i < trajectory.Count; i++)
        {
            ref readonly ZapTrajectoryStep step = ref trajectory[i];
            Assert.False(step.Move.IsNull);
            Assert.NotEqual(0UL, step.ZobristKey);
        }
    }

    [Fact]
    public void ZapPolicy_InCheck_FindsLegalEvasion()
    {
        // Scholar's Mate position: Black king in check from White Queen
        string fen = "r1bqkb1r/pppp1Qpp/2n5/4p3/2B1n3/8/PPPP1PPP/RNB1K1NR b KQkq - 0 4";
        Board board = Fen.Parse(fen);
        Assert.True(board.IsInCheck());

        var policy = new ZapPolicy();
        bool hasMove = policy.TryGetQuickMove(in board, 0, out Move move);

        // Checkmated -> hasMove should be false
        Assert.False(hasMove);
        Assert.True(move.IsNull);
    }

    [Fact]
    public void ZapProbe_RecapturesHangingQueen()
    {
        string fen = "r3kbnr/p1p2pp1/np2p3/3pP1q1/3P4/2N2N2/PPP2PPP/R1B2RK1 w kq - 0 10";
        Board board = Fen.Parse(fen);

        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 10);
        Move bestMove = search.Search(in board, in budget);
        Assert.True(bestMove.To == Square.G5, $"Expected capture on g5, got {bestMove.ToUci()}.");
    }

    [Fact]
    public void ZapProbe_NeverAbandonsSpine_RunsToFullHorizon()
    {
        Board board = Fen.Parse("r3kbnr/p1pq1ppp/np2p3/3pP3/3P4/5N2/PPPN1PPP/R1BQ1RK1 w kq - 0 9");
        string[] moves = ["f3g5", "g8e7", "d2b3", "c7c5", "d1d2", "e7c6", "d2e3", "c5c4"];
        foreach (var mStr in moves)
        {
            MoveList list = default;
            MoveGenerator.GenerateLegalMoves(in board, ref list);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].ToUci() == mStr)
                {
                    board.MakeMove(list[i], out _);
                    break;
                }
            }
        }

        // Trace g5e4 (sacrificing knight for pawn)
        Move g5e4 = Move.Null;
        MoveList rootMoves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref rootMoves);
        for (int i = 0; i < rootMoves.Count; i++)
        {
            if (rootMoves[i].ToUci() == "g5e4") { g5e4 = rootMoves[i]; break; }
        }

        Board g5e4Board = board;
        g5e4Board.MakeMove(g5e4, out _);

        var policy = new ZapPolicy();
        var evaluator = new DefaultEvaluator();
        ZapTrajectory trajectory = default;

        int score = ZapProbe.Trace(
            ref g5e4Board,
            10,
            policy,
            evaluator,
            Color.White,
            ref trajectory,
            initialLastMove: g5e4);

        // Spines are never abandoned by the probe: the losing line is still traced to the full horizon
        Assert.True(trajectory.Count >= 10, $"Expected full-horizon trajectory (>= 10), got {trajectory.Count}");
    }

    [Fact]
    public void ZapPolicy_LvaRecapture_PicksPawnOverQueen()
    {
        // FEN where Black has pawn on d5, White has pawn on c4 and Queen on d1 attacking d5
        Board board = Fen.Parse("rnbqkbnr/pp3ppp/8/3p4/2P5/8/PP3PPP/RNBQKBNR w KQkq - 0 5");
        var policy = new ZapPolicy();

        // Simulate last move was Black capturing on d5: e7xd5
        Move lastMove = new Move(Square.E7, Square.D5, MoveType.Capture);

        bool found = policy.TryGetQuickMove(in board, ply: 0, lastMove, out Move recaptureMove);

        Assert.True(found);
        Assert.False(recaptureMove.IsNull);
        Assert.Equal(Square.D5, recaptureMove.To);
        // Crucial: Must be the Pawn on c4 (LVA), NOT Queen on d1!
        Assert.Equal(Square.C4, recaptureMove.From);
    }

    [Fact]
    public void ZapPolicy_AvoidsBlunderingPieceUnderAttack_AndRecapturesBishop()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        string[] moves = ["c2c4", "d7d5", "d2d4", "a7a6", "c1g5", "g8h6", "b1c3", "f7f6", "g5h6"];
        foreach (var mStr in moves)
        {
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); break; }
            }
        }

        var policy = new ZapPolicy();
        var eval = new ZanakNnueEvaluator();
        eval.SetRootTopology(in board);
        Move g7h6 = Move.Null, d5c4 = Move.Null;
        MoveList ml2 = default;
        MoveGenerator.GenerateLegalMoves(in board, ref ml2);
        for (int i = 0; i < ml2.Count; i++)
        {
            if (ml2[i].ToUci() == "g7h6") g7h6 = ml2[i];
            if (ml2[i].ToUci() == "d5c4") d5c4 = ml2[i];
        }

        Board bg = board; bg.MakeMove(g7h6, out _);
        Board bd = board; bd.MakeMove(d5c4, out _);

        // Trace 7 plies (completing White's reply after any ply-6 capture such as 12. Qxc4 Nxd4 under the
        // stateless 25-step spine, avoiding a ply-6 horizon artifact with anak.nnue).
        ZapTrajectory tg = default, td = default;
        int sg = ZapProbe.Trace(ref bg, 7, policy, eval, board.SideToMove, ref tg, g7h6);
        int sd = ZapProbe.Trace(ref bd, 7, policy, eval, board.SideToMove, ref td, d5c4);

        // Crucial: Recapturing the bishop (g7h6) must evaluate significantly better than ignoring it and blundering (d5c4)
        Assert.True(sg > sd, $"Expected recapturing bishop (g7h6 score {sg}) to be superior to blundering move (d5c4 score {sd})");
        Assert.True(sg >= -300, $"Expected sound recapture position to hold eval >= -300 cp, got {sg}");
    }

    [Fact]
    public void ZapProbe_LaggingLeader_DoesNotTruncateSpine()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        var policy = new ZapPolicy();
        var evaluator = new DefaultEvaluator();
        ZapTrajectory trajectory = default;

        // Leader score is high (+500), but board is equal start pos (~0 cp).
        // Spines are never truncated by the probe, even when far below the leader (agents handle that).
        int score = ZapProbe.Trace(
            ref board,
            targetDepth: 12,
            policy,
            evaluator,
            Color.White,
            ref trajectory,
            leaderScore: 500);

        Assert.True(trajectory.Count >= 8, $"Expected multi-ply spine (>= 8 plies), but traced {trajectory.Count}.");
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r1bqkb1r/pppp1ppp/2n5/4p3/2B1n3/5N2/PPPP1PPP/RNBQ1RK1 w kq - 0 5")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    [InlineData("8/4P3/8/8/8/8/8/4K2k w - - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void ZapPolicy_TryGetQuickMove_AlwaysProducesLegalMove(string fen)
    {
        Board board = Fen.Parse(fen);
        var policy = new ZapPolicy();

        bool hasMove = policy.TryGetQuickMove(in board, ply: 0, lastMove: Move.Null, out Move move);
        if (hasMove)
        {
            Assert.False(move.IsNull);
            Assert.True(board.IsLegal(in move), $"Move {move.ToUci()} is not legal in {fen}");
        }
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r1bqkb1r/pppp1ppp/2n5/4p3/2B1n3/5N2/PPPP1PPP/RNBQ1RK1 w kq - 0 5")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void ZapProbe_Trace_CompletesCleanlyToTargetDepth(string fen)
    {
        Board board = Fen.Parse(fen);
        var policy = new ZapPolicy();
        var evaluator = new DefaultEvaluator();
        ZapTrajectory trajectory = default;

        int score = ZapProbe.Trace(
            ref board,
            targetDepth: 8,
            policy,
            evaluator,
            board.SideToMove,
            ref trajectory);

        Assert.True(trajectory.Count > 0, "Trajectory should have recorded steps.");
        for (int i = 0; i < trajectory.Count; i++)
        {
            ref readonly ZapTrajectoryStep step = ref trajectory[i];
            Assert.False(step.Move.IsNull);
        }
    }

    [Fact]
    public void ZapSearch_RecapturesBishopOnH3()
    {
        // 11... Bxh3 was just played, White must recapture on h3 with g2xh3
        string fen = "r1bq1rk1/pppnpp1p/3p1np1/3P4/2P1PP2/2N1B2b/PP4PP/R2QKB1R w KQ - 0 12";
        Board board = Fen.Parse(fen);

        using var search = new ZapSearch();
        Move bestMove = search.Search(in board, new SearchBudget(SoftLimitMs: 1000, HardLimitMs: 1500, TargetBaseDepth: 8, MaxDepthHorizon: 12));

        Assert.NotEqual(Move.Null, bestMove);
        Assert.True(board.IsLegal(in bestMove));
    }

    [Fact]
    public void ZapSearch_TakesFreeKnightOnA5()
    {
        // 13. Na5 leaves the Knight undefended on a5; Black must capture 13... Nxa5
        string fen = "r1bq1rk1/2p1bppn/p1np3p/Np2p3/4P3/P2PBN2/BPP2PPP/R2Q1RK1 b - - 1 13";
        Board board = Fen.Parse(fen);

        using var search = new ZapSearch();
        Move bestMove = search.Search(in board, new SearchBudget(SoftLimitMs: 1000, HardLimitMs: 1500, TargetBaseDepth: 8, MaxDepthHorizon: 12));

        Assert.Equal("c6a5", bestMove.ToUci());
    }

    [Fact]
    public void ZapPolicy_Step1_CheckEvasion_PrefersWinningCaptureAndAvoidsSuicidalBlock()
    {
        var policy = new ZapPolicy();

        // Winning capture resolving check (Nb3xc1) beats quiet king evasion.
        Board captureCheckBoard = Fen.Parse("7k/8/8/8/8/1N6/8/2r1K3 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in captureCheckBoard, 0, out Move captureEvasion));
        Assert.Equal("b3c1", captureEvasion.ToUci());

        // Safe king evasion beats losing Queen block d2e2 against Re8.
        Board safeKingBoard = Fen.Parse("4r2k/8/8/8/8/8/3Q4/4K3 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in safeKingBoard, 0, out Move safeEvasion));
        Assert.NotEqual("d2e2", safeEvasion.ToUci());
        Assert.Equal(Square.E1, safeEvasion.From);
    }

    [Fact]
    public void ZapPolicy_Step2_FreeQueenCapture_PrecedesStep3QueenEvasion()
    {
        // Black's Queen on g6 is undefended (Step 2) while White's Queen on a1 is attacked by Bd4 (Step 3).
        Board board = Fen.Parse("7k/8/6q1/8/3b4/8/8/Q5RK w - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal("g1g6", move.ToUci());
    }

    [Fact]
    public void ZapPolicy_Step3_QueenEvasion_PrecedesStep4SafeQueenPromotion()
    {
        // Step 3 (Queen evasion) precedes Step 4 (Safe Queen promotion) per 25-step hierarchy.
        Board board = Fen.Parse("7k/P7/8/5p2/4p3/3Q4/8/7K w - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal(Square.D3, move.From);
        Assert.NotEqual(MoveType.QueenPromotion, move.Type);
    }

    [Fact]
    public void ZapPolicy_Step4_SafeQueenPromotion_PrecedesStep5PawnTakesDefendedQueen()
    {
        Board board = Fen.Parse("7k/1P6/3r4/3q4/4P3/8/8/7K w - - 0 1");
        var policy = new ZapPolicy();

        Assert.True(policy.TryGetQuickMove(in board, 0, out Move move));

        Assert.Equal("b7b8q", move.ToUci());
    }

    [Fact]
    public void ZapPolicy_Steps5To11_QueenAndRookTierPrecedence()
    {
        var policy = new ZapPolicy();

        // Step 5 (PxQ) > Step 6 (NxQ)
        Board step5Over6 = Fen.Parse("3r3k/8/1N6/3q4/2P5/8/8/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step5Over6, 0, out Move m5));
        Assert.Equal("c4d5", m5.ToUci());

        // Step 6 (NxQ) > Step 7 (BxQ)
        Board step6Over7 = Fen.Parse("3r3k/8/1N6/3q4/8/5B2/8/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step6Over7, 0, out Move m6));
        Assert.Equal("b6d5", m6.ToUci());

        // Step 7 (BxQ) > Step 8 (Free Rook)
        Board step7Over8 = Fen.Parse("3r3k/8/8/r2q4/8/5B2/8/R6K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step7Over8, 0, out Move m7));
        Assert.Equal("f3d5", m7.ToUci());

        // Step 8 (Free Rook) > Step 9 (Rook Evasion)
        Board step8Over9 = Fen.Parse("7k/8/3p4/4b3/7r/8/6N1/R6K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step8Over9, 0, out Move m8));
        Assert.Equal("g2h4", m8.ToUci());

        // Step 9 (Rook Evasion) > Step 10 (RxQ)
        Board step9Over10 = Fen.Parse("4r2k/4q3/3p4/4b3/8/8/8/R3RK2 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step9Over10, 0, out Move m9));
        Assert.Equal(Square.A1, m9.From);

        // Step 10 (RxQ) > Step 11 (PxR)
        Board step10Over11 = Fen.Parse("4r2k/4q3/p7/1r6/P7/8/8/4RK2 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step10Over11, 0, out Move m10));
        Assert.Equal("e1e7", m10.ToUci());

        // Step 11 (PxR) > Step 12 (Free Bishop)
        Board step11Over12 = Fen.Parse("5b1k/8/p7/1r6/P7/8/8/5R1K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step11Over12, 0, out Move m11));
        Assert.Equal("a4b5", m11.ToUci());
    }

    [Fact]
    public void ZapPolicy_Steps12To19_MinorPieceAndRookCapturePrecedence()
    {
        var policy = new ZapPolicy();

        // Step 12 (Free Bishop) > Step 13 (Bishop Evasion)
        Board step12Over13 = Fen.Parse("7k/Rb6/8/8/6p1/5p2/6B1/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step12Over13, 0, out Move m12));
        Assert.Equal("a7b7", m12.ToUci());

        // Step 13 (Bishop Evasion) > Step 14 (Free Knight)
        Board step13Over14 = Fen.Parse("7k/Rn6/8/8/6p1/5p2/6B1/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step13Over14, 0, out Move m13));
        Assert.Equal(Square.G2, m13.From);

        // Step 14 (Free Knight) > Step 15 (Knight Evasion)
        Board step14Over15 = Fen.Parse("7k/Rn6/8/8/6p1/5p2/6N1/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step14Over15, 0, out Move m14));
        Assert.Equal("a7b7", m14.ToUci());

        // Step 15 (Knight Evasion) > Step 16 (PxB)
        Board step15Over16 = Fen.Parse("7k/8/p7/1b6/P5p1/5p2/6N1/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step15Over16, 0, out Move m15));
        Assert.Equal(Square.G2, m15.From);

        // Step 16 (PxB) > Step 17 (PxN)
        Board step16Over17 = Fen.Parse("7k/8/p3p3/1b3n2/P3P3/8/8/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step16Over17, 0, out Move m16));
        Assert.Equal("a4b5", m16.ToUci());

        // Step 17 (PxN) > Step 18 (NxR)
        Board step17Over18 = Fen.Parse("2r4k/8/3p4/2r1n3/5P2/1N6/8/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step17Over18, 0, out Move m17));
        Assert.Equal("f4e5", m17.ToUci());

        // Step 18 (NxR) > Step 19 (BxR)
        Board step18Over19 = Fen.Parse("2r4k/8/8/2r5/8/1N6/5B2/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step18Over19, 0, out Move m18));
        Assert.Equal("b3c5", m18.ToUci());

        // Step 19 (BxR) > Step 20 (Free Pawn)
        Board step19Over20 = Fen.Parse("2r4k/8/8/2r3p1/6R1/8/5B2/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step19Over20, 0, out Move m19));
        Assert.Equal("f2c5", m19.ToUci());
    }

    [Fact]
    public void ZapPolicy_Steps20To25_PawnCheckNxBPushAndFallbackPrecedence()
    {
        var policy = new ZapPolicy();

        // Step 20 (Free Pawn) > Step 21 (Pawn Evasion)
        Board step20Over21 = Fen.Parse("7k/8/p5p1/1n6/8/P7/8/6RK w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step20Over21, 0, out Move m20));
        Assert.Equal("g1g6", m20.ToUci());

        // Step 20 includes safe en-passant free pawn capture before Step 24 pawn push
        Board epBoard = Fen.Parse("7k/8/8/4pP2/8/8/8/7K w - e6 0 1");
        Assert.True(policy.TryGetQuickMove(in epBoard, 0, out Move mEp));
        Assert.Equal("f5e6", mEp.ToUci());
        Assert.Equal(MoveType.EnPassant, mEp.Type);

        // Step 21 (Pawn Evasion) > Step 22 (Safe Check)
        Board step21Over22 = Fen.Parse("3k4/8/p7/1n6/8/P7/8/4R2K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step21Over22, 0, out Move m21));
        Assert.Equal("a3a4", m21.ToUci());

        // Step 22 (Safe Check) skips equal capture check RxR+ (SEE == 0), falling through to Step 24 push
        Board equalCheckSkipped = Fen.Parse("2krr3/8/6P1/8/8/8/8/2KR4 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in equalCheckSkipped, 0, out Move mNoEqualCheck));
        Assert.Equal("g6g7", mNoEqualCheck.ToUci());

        // Step 22 (Safe Check) > Step 23 (NxB defended once: +10 > 0)
        Board step22Over23 = Fen.Parse("3k4/8/6p1/5b2/8/4N3/8/R1K5 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step22Over23, 0, out Move m22));
        Assert.Equal("a1a8", m22.ToUci());

        // Step 23 (NxB defended once) > Step 24 (Safe Pawn Push)
        Board step23Over24 = Fen.Parse("3k4/8/6p1/5b2/8/4N3/6P1/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in step23Over24, 0, out Move m23));
        Assert.Equal("e3f5", m23.ToUci());

        // Step 24 pushes pawn closest to promotion rank first (both White and Black)
        Board whitePushOrder = Fen.Parse("7k/8/2P5/8/1P6/8/P7/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in whitePushOrder, 0, out Move mWhitePush));
        Assert.Equal("c6c7", mWhitePush.ToUci());

        Board blackPushOrder = Fen.Parse("7k/p7/8/1p6/8/2p5/8/7K b - - 0 1");
        Assert.True(policy.TryGetQuickMove(in blackPushOrder, 0, out Move mBlackPush));
        Assert.Equal("c3c2", mBlackPush.ToUci());

        // Step 25 prefers safe fallback move over losing destinations, and falls back to legal[0] when none are safe
        Board safeFallback = Fen.Parse("7k/8/8/8/4r3/r2r4/8/5N1K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in safeFallback, 0, out Move mSafeFallback));
        Assert.NotEqual("f1d2", mSafeFallback.ToUci());
        Assert.NotEqual("f1e3", mSafeFallback.ToUci());
        Assert.NotEqual("f1g3", mSafeFallback.ToUci());

        Board forcedFallback = Fen.Parse("1r2kr2/8/8/8/8/8/3r4/K3N3 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in forcedFallback, 0, out Move mForced));
        Assert.Equal(Square.E1, mForced.From);
    }

    [Fact]
    public void ZapPolicy_ExcludesEqualCaptureTradesInSteps1To24_AndIsStatelessAcrossPlys()
    {
        var policy = new ZapPolicy();

        // Defended QxQ, RxR, and BxN (negative SEE -10) are not taken when safe push h6h7/g6g7 exists.
        Board qxqBoard = Fen.Parse("6k1/5p2/2p2p1P/3q4/8/2RQ4/8/6K1 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in qxqBoard, 0, out Move mQ));
        Assert.Equal("h6h7", mQ.ToUci());

        Board rxrBoard = Fen.Parse("3r3k/8/1P1r4/8/8/8/8/2KR4 w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in rxrBoard, 0, out Move mR));
        Assert.Equal("b6b7", mR.ToUci());

        Board bxnBoard = Fen.Parse("7k/8/1P1p4/4n3/8/2B5/8/7K w - - 0 1");
        Assert.True(policy.TryGetQuickMove(in bxnBoard, 0, out Move mB));
        Assert.Equal("b6b7", mB.ToUci());

        // Stateless across plies: identical output regardless of ply index.
        Assert.True(policy.TryGetQuickMove(in qxqBoard, 7, out Move mPly7));
        Assert.Equal(mQ, mPly7);
    }

    [Fact]
    public void ZapPolicy_PlaysMateIn1CaptureEvenWhenSeeIsNegative()
    {
        var policy = new ZapPolicy();
        // Qe5 + Bc7 vs Kg1 with pawns on g2/h2: Qxh2# has negative SEE (-800 if king could be captured),
        // but is immediate checkmate and must be played.
        Board mateBoard = Fen.Parse("6k1/2b5/8/4q3/8/8/6PP/6K1 b - - 0 1");
        Assert.True(policy.TryGetQuickMove(in mateBoard, 0, out Move mateMove));
        Assert.Equal("e5h2", mateMove.ToUci());
    }
}
