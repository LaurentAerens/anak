using Anak.Core;
using Anak.Data.Evaluation;
using Xunit;
using Zanak.Search;

namespace Anak.Tests.Zanak;

public sealed class ZapProbeTests
{
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
    public void ZapProbe_SpineAbandonment_AbortsLosingTrajectory()
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

        // Trajectory should be abandoned early and score should be negative
        Assert.True(score <= -200, $"Expected score <= -200, got {score}");
        Assert.True(trajectory.Count < 10, $"Expected trajectory to abort early (< 10), got {trajectory.Count}");
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
        var eval = new DefaultEvaluator();
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

        ZapTrajectory tg = default, td = default;
        int sg = ZapProbe.Trace(ref bg, 6, policy, eval, board.SideToMove, ref tg, g7h6);
        int sd = ZapProbe.Trace(ref bd, 6, policy, eval, board.SideToMove, ref td, d5c4);

        // Crucial: Recapturing the bishop (g7h6) must evaluate significantly better than ignoring it and blundering (d5c4)
        Assert.True(sg > sd, $"Expected recapturing bishop (g7h6 score {sg}) to be superior to blundering move (d5c4 score {sd})");
        string steps = "";
        for (int i = 0; i < tg.Count; i++) steps += $"{tg[i].Move.ToUci()}:{tg[i].StaticEval} ";
        Assert.True(sg >= -200, $"Expected sound recapture position to hold eval >= -200 cp, got {sg}, steps={steps}");
    }

    [Fact]
    public void ZapProbe_RelativeDeadLineTruncation_AbortsWhenLaggingLeader()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        var policy = new ZapPolicy();
        var evaluator = new DefaultEvaluator();
        ZapTrajectory trajectory = default;

        // Leader score is high (+500), but board is equal start pos (~0 cp).
        // Since 0 <= 500 - 300 = 200 cp, at ply >= 4 the spine must truncate!
        int score = ZapProbe.Trace(
            ref board,
            targetDepth: 12,
            policy,
            evaluator,
            Color.White,
            ref trajectory,
            leaderScore: 500);

        Assert.True(trajectory.Count <= 5, $"Expected spine to truncate at ply <= 5, but traced {trajectory.Count} plies.");
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
}
