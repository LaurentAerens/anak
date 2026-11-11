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
        // Scholar's Mate position: 1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# (or position where Black is in check)
        // FEN: Black king in check from White Queen
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
        Assert.True(sg >= -200, $"Expected sound recapture position to hold eval >= -200 cp, got {sg}");
        Assert.True(sg >= -200, $"Expected sound recapture position to hold eval >= -200 cp, got {sg}");
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

    [Fact]
    public void ZapSearch_ViennaGambit_AvoidsKd2Blunder()
    {
        // Moves: 1. e4 e5 2. Nc3 Nf6 3. f4 d5 4. fxe5 Nxe4 5. Qf3
        // Followed by Black reply (e.g. 5... Nc6 or 5... Qh4+ or 5... Nxc3 or 5... f5)
        Board board = Fen.Parse(Fen.StartPosition);
        // 1. e4 e5 2. Nc3 Nf6 3. f4 d5 4. fxe5 Nxe4 5. Qf3
        // Followed by Black reply.
        Board b5 = Fen.Parse(Fen.StartPosition);
        string[] m5 = ["e2e4", "e7e5", "b1c3", "g8f6", "f2f4", "d7d5", "f4e5", "f6e4", "d1f3"];
        Move lastM5 = Move.Null;
        foreach (var mStr in m5)
        {
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in b5, ref ml);
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { b5.MakeMove(ml[i], out _); lastM5 = ml[i]; break; }
            }
        }

        // Test: 1. e4 e5 2. Nc3 Nf6 3. f4 d5 4. fxe5 Nxe4 5. Qf3
        // If Black plays 5... Nc6:
        MoveList blMoves = default;
        MoveGenerator.GenerateLegalMoves(in b5, ref blMoves);
        Board bTest = b5;
        Move nc6 = Move.Null;
        for (int i = 0; i < blMoves.Count; i++) if (blMoves[i].ToUci() == "b8c6") nc6 = blMoves[i];
        bTest.MakeMove(nc6, out _);

        var sNc6 = new ZapSearch();
        var budNc6 = new global::Zanak.Time.SearchBudget(500, 1000, 6, 12);
        Move wMove = sNc6.Search(in bTest, in budNc6, lastMove: nc6);

        Board bAfterNxe4 = bTest;
        Move mNxe4 = Move.Null;
        for (int i = 0; i < blMoves.Count; i++) if (blMoves[i].ToUci() == "c3e4") mNxe4 = blMoves[i];
        // Wait, mNxe4 is White move, let's find c3e4 in White legal moves:
        MoveList wLegals = default;
        MoveGenerator.GenerateLegalMoves(in bTest, ref wLegals);
        for (int i = 0; i < wLegals.Count; i++) if (wLegals[i].ToUci() == "c3e4") mNxe4 = wLegals[i];
        bAfterNxe4.MakeMove(mNxe4, out _);

        MoveList bRepliesToNxe4 = default;
        MoveGenerator.GenerateLegalMoves(in bAfterNxe4, ref bRepliesToNxe4);
        int maxOppLoss = 0;
        Move topReply = Move.Null;
        for (int r = 0; r < bRepliesToNxe4.Count; r++)
        {
            Move rep = bRepliesToNxe4[r];
            if (rep.IsCapture)
            {
                int see = StaticExchangeEvaluator.Evaluate(in bAfterNxe4, rep);
                if (see > maxOppLoss) { maxOppLoss = see; topReply = rep; }
            }
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(500, 1000, 6, 12);
        Move best = search.Search(in bTest, in budget, lastMove: nc6);

        // After 1. e4 e5 2. Nc3 Nf6 3. f4 d5 4. fxe5 Nxe4 5. Qf3:
        // White must NOT make suicidal early uncastled King wanders like e1d1, e1d2, or e1e2!
        Assert.NotEqual("e1d1", best.ToUci());
        Assert.NotEqual("e1d2", best.ToUci());
        Assert.NotEqual("e1e2", best.ToUci());
    }

    [Fact]
    public void ZapSearch_WhenQueenAttackedByPawn_RescuesQueenInsteadOfCounterAttacking()
    {
        Board board = Fen.Parse("rnb1k2r/4bppp/pp2pn2/2pq4/3P4/2PB1N2/PP3PPP/RNBQR1K1 w kq - 0 9");
        string[] moves = ["c3c4", "d5h5", "e1e5", "g7g5", "c1g5", "c5d4", "g5f6", "h5g4", "h2h3"];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }

        // board is now Black to move. White just played h2h3, attacking Black Queen on g4!

        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(500, 1000, 6, 12);

        Move best = search.Search(in board, in budget, lastMove: lastMove);

        // Black must NOT play Rg8 (which hangs the Queen to hxg4)
        Assert.NotEqual("h8g8", best.ToUci());
        // Black must move the Queen out of attack:
        Assert.True(best.From == Square.G4, $"Expected Queen on g4 to escape, got {best.ToUci()}");
    }

    [Fact]
    public void ZapSearch_InCheck_AvoidsEvasionThatHangsQueen()
    {
        Board board = Fen.Parse("rnb1k2r/4bppp/pp2pn2/2pq4/3P4/2PB1N2/PP3PPP/RNBQR1K1 w kq - 0 9");
        string[] moves = [
            "d1a4", "b8d7", "c1d2", "b6b5", "a4b3", "c5c4", "b3c2", "c4d3", "c2d3", "a8b8",
            "a2a4", "b8b6", "f3g5", "c8b7", "f2f3", "b7c6", "g5e4", "h7h5", "a4a5", "b6b7",
            "d2c1", "h5h4", "b2b3", "h4h3", "g2g3", "e8g8", "b3b4", "e6e5", "e4c5", "b7c7",
            "d4e5", "e7c5"
        ];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }

        // board is now White in check from Black bishop on c5.
        // White Queen is on d3.
        // On move 25, White was in check from Black bishop on c5!
        // Moves that evade check:
        // Kf1, Kh1, Re3, Be3.
        // If White plays Re3, the Rook is pinned to King on g1 by Black bishop on c5!
        // Because of the pin, Re3 cannot capture Qxd3!
        // White played 25. Re3?? which walked into an absolute pin AND hung Queen on d3.
        // White should have played e.g. 25. Be3 or moved the King, or blocked differently.
        // Let's test that on move 25, White does NOT play Re3 (which hangs the Queen to Qxd3 without recapture possible!)

        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(500, 1000, 6, 12);

        Move best = search.Search(in board, in budget, lastMove: lastMove);

        // 25. Re3 hangs Queen on d3 to Qxd3+
        Assert.NotEqual("e1e3", best.ToUci());
    }

    [Fact]
    public void ZapSearch_BlunderShield_PreventsHangingQueenOnMove1()
    {
        // FEN: Black bishop on g4 pins White knight on f3 to White Queen on d1.
        // Moving the knight away (e.g. f3-e5 or f3-g5) hangs the Queen on d1 to g4xd1!
        Board board = Fen.Parse("r2qkbnr/ppp1pppp/2n5/3p4/4P1b1/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 4");


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(500, 1000, 6, 12);

        Move best = search.Search(in board, in budget);

        // Best move must NOT hang the Queen to g4xd1
        Assert.NotEqual("f3e5", best.ToUci());
        Assert.NotEqual("f3g5", best.ToUci());
        Assert.NotEqual("f3d4", best.ToUci());
    }

    [Fact]
    public void ZapSearch_InvestigateGame1_Move11()
    {
        Board board = Fen.Parse("r1bqkb1r/1p1p2pp/p1n1p3/2p1Pp2/5B2/2P2N2/PPP2PPP/R2QKB1R w KQkq - 2 9");
        string[] moves = ["h2h4", "f8e7", "d1d2", "d8a5", "h4h5"];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");
        // Must not blunder bishop backwards
        Assert.NotEqual("e7d8", best.ToUci());
        Assert.NotEqual("e7f8", best.ToUci());
        Assert.False(best.IsNull);
    }

    [Fact]
    public void ZapSearch_InvestigateGame1_Move13_DoesNotPlayBf6()
    {
        Board board = Fen.Parse("r1bqkb1r/1p1p2pp/p1n1p3/2p1Pp2/5B2/2P2N2/PPP2PPP/R2QKB1R w KQkq - 2 9");
        string[] moves = [
            "h2h4", "f8e7", "d1d2", "d8a5", "h4h5", "e7d8",
            "h1g1", "g7g6", "f4g5"
        ];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candLog = "";
        for (int i = 0; i < Math.Min(6, search.SharedState.CandidateCount); i++)
        {
            var c = search.SharedState.GetCandidate(i);
            candLog += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}\n";
        }

        // Must NOT play suicidal blunder 13... Bf6 hanging the bishop to 14. Bxf6!
        Assert.NotEqual("d8f6", best.ToUci());
        Assert.False(search.SharedState.GetBestCandidate().Score >= 20000, "Must not hallucinate mate score!");
    }

    [Fact]
    public void ZapSearch_InvestigateGame1_Move12_SavesBishopOnC7()
    {
        Board board = Fen.Parse("rn1qk2r/pbppb1pp/1p2p3/5p2/2PP4/2N2NP1/PPQnPPBP/R3K2R w KQkq - 0 9");
        string[] moves = [
            "c2d2", "e7d6", "d4d5", "c7c5", "e1c1", "d6c7", "d5d6"
        ];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candLog = "";
        for (int i = 0; i < search.SharedState.CandidateCount; i++)
        {
            var c = search.SharedState.GetCandidate(i);
            candLog += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}\n";
            foreach (var step in c.Trajectory)
            {
                candLog += $"   {step.SideToMove}: {step.Move.ToUci()} (ev={step.StaticEval}, d={step.EvalDelta})\n";
            }
        }

        Assert.NotEqual("h8f8", best.ToUci());
    }

    [Fact]
    public void ZapSearch_InvestigateGame2_Move9()
    {
        Board board = Fen.Parse("rnb1k2r/pp1pppbp/6p1/2q1n3/Q1P5/2N3PN/PP2PPBP/R1B1K2R w KQkq - 4 9");


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candLog = "";
        for (int i = 0; i < Math.Min(6, search.SharedState.CandidateCount); i++)
        {
            var c = search.SharedState.GetCandidate(i);
            candLog += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}\n";
            foreach (var step in c.Trajectory)
            {
                candLog += $"   {step.SideToMove}: {step.Move.ToUci()} (ev={step.StaticEval}, d={step.EvalDelta})\n";
            }
        }

        Assert.False(best.IsNull);
        Assert.False(search.SharedState.GetBestCandidate().IsDeadStem);
    }

    [Fact]
    public void ZapSearch_InvestigateGame3_Move21_DetectsPawnFork()
    {
        Board board = Fen.Parse("r1b1kb1r/pp2nppp/1q1pp3/8/2BpP3/8/PPPPNPPP/R1BQ1RK1 w kq - 2 9");
        string[] moves = [
            "c2c3", "d4c3", "c4b3", "c3b2", "c1b2", "c8d7",
            "d1c2", "h7h5", "a1b1", "a7a5", "b2d4", "b6b5",
            "d2d3", "a8c8", "c2d1", "b5c6", "g2g3", "b7b5",
            "d1d2", "c6a6", "d2f4", "h5h4", "b1b2", "e7c6"
        ];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candList = "";
        for (int i = 0; i < Math.Min(6, search.SharedState.CandidateCount); i++)
        {
            var c = search.SharedState.GetCandidate(i);
            candList += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}\n";
        }

        // Must NOT play suicidal blunder b1d1 allowing e7-e5 pawn fork
        Assert.False(best.IsNull);
        Assert.NotEqual("b1d1", best.ToUci());
    }

    [Fact]
    public void ZapSearch_InvestigateGame1_Move19()
    {
        Board board = Fen.Parse("r1bq1rk1/ppppbppp/2n5/1B6/3pPP2/3P4/PPPN2PP/R1BQ1RK1 w - - 3 9");
        string[] moves = [
            "b5a4", "d7d5", "a2a3", "h7h6", "d1h5", "c8e6",
            "f4f5", "e6d7", "a4c6", "d7c6", "e4e5", "g8h7",
            "f5f6", "g7f6", "d2f3", "a8c8", "e5f6", "e7f6",
            "c1h6", "f8e8", "f3e5"
        ];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        // Let's inspect g6f5 directly by tracing it with depth 12
        Move g6f5Move = Move.Null;
        MoveList mlCheck = default;
        MoveGenerator.GenerateLegalMoves(in board, ref mlCheck);
        for (int i = 0; i < mlCheck.Count; i++) { if (mlCheck[i].ToUci() == "g6f5") g6f5Move = mlCheck[i]; }
        Board g6f5Board = board;
        g6f5Board.MakeMove(g6f5Move, out _);

        // Check legal moves for White after g6f5:
        MoveList whiteReplies = default;
        MoveGenerator.GenerateLegalMoves(in g6f5Board, ref whiteReplies);
        string whiteRepliesInfo = "";
        var pol = new ZapPolicy();
        var evalr = new DefaultEvaluator();
        for (int i = 0; i < whiteReplies.Count; i++)
        {
            Board wBoard = g6f5Board;
            wBoard.MakeMove(whiteReplies[i], out _);
            ZapTrajectory wTraj = default;
            int s = ZapProbe.Trace(ref wBoard, 10, pol, evalr, Color.Black, ref wTraj, initialLastMove: whiteReplies[i]);
            if (s <= -300)
            {
                whiteRepliesInfo += $"White reply {whiteReplies[i].ToUci()}: score={s}, traj: ";
                for (int t = 0; t < Math.Min(4, wTraj.Count); t++) whiteRepliesInfo += $"{wTraj[t].SideToMove} {wTraj[t].Move.ToUci()} (ev={wTraj[t].StaticEval}) ";
                whiteRepliesInfo += "\n";
            }
        }

        string candList = $"Total Nodes: {search.SharedState.TotalNodes}\nWhite refutations:\n{whiteRepliesInfo}\n";
        for (int i = 0; i < Math.Min(2, search.SharedState.CandidateCount); i++)
        {
            var c = search.SharedState.GetCandidate(i);
            candList += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}, status={c.Status}, history={c.ScoreHistory}\n";
            foreach (var step in c.Trajectory)
            {
                candList += $"   {step.SideToMove}: {step.Move.ToUci()} (ev={step.StaticEval}, d={step.EvalDelta})\n";
            }
        }

        // Must NOT play suicidal blunders c6a4 or c6b5 that walk into Bf4+ discovered checkmate
        Assert.NotEqual("c6a4", best.ToUci());
        Assert.NotEqual("c6b5", best.ToUci());
    }

    [Fact]
    public void ZapSearch_InvestigateGame1_Move10_RecapturesOnF5()
    {
        Board board = Fen.Parse("rnbqk2r/pp2p1bp/2pp1np1/5p2/2PPP3/2NB1N2/PP3PP1/R1BQK2R w KQkq - 0 9");
        string[] moves = ["e4f5", "c8f5", "d3f5"];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        // Trace after 10...gxf5 11.Qe2
        Board bAfterQe2 = board;
        Move g6f5M = Move.Null, d1e2M = Move.Null;
        MoveList ml1 = default; MoveGenerator.GenerateLegalMoves(in bAfterQe2, ref ml1);
        for (int i = 0; i < ml1.Count; i++) if (ml1[i].ToUci() == "g6f5") g6f5M = ml1[i];
        bAfterQe2.MakeMove(g6f5M, out _);
        MoveList ml2 = default; MoveGenerator.GenerateLegalMoves(in bAfterQe2, ref ml2);
        for (int i = 0; i < ml2.Count; i++) if (ml2[i].ToUci() == "d1e2") d1e2M = ml2[i];
        bAfterQe2.MakeMove(d1e2M, out _);
        var pol = new ZapPolicy();
        var ev = new DefaultEvaluator();

        ZapTrajectory directTraj = default;
        int directScore = ZapProbe.Trace(ref bAfterQe2, 10, pol, ev, Color.Black, ref directTraj, initialLastMove: d1e2M);
        string directInfo = $"Trace after 11.Qe2 = {directScore}, traj: ";
        for (int t = 0; t < directTraj.Count; t++) directInfo += $"{directTraj[t].SideToMove} {directTraj[t].Move.ToUci()} (ev={directTraj[t].StaticEval}) ";

        string candList = $"Total Nodes: {search.SharedState.TotalNodes}\n{directInfo}\n";
        for (int i = 0; i < search.SharedState.CandidateCount; i++)
        {
            var c = search.SharedState.GetCandidate(i);
            if (i < 6 || c.Move.ToUci() == "g6f5")
            {
                candList += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}, status={c.Status}, history={c.ScoreHistory}\n";
                foreach (var step in c.Trajectory)
                {
                    candList += $"   {step.SideToMove}: {step.Move.ToUci()} (ev={step.StaticEval}, d={step.EvalDelta})\n";
                }
            }
        }


        // Must NOT play suicidal blunder a7a6, a7a5, or e7e6 ignoring the attack on the bishop
        Assert.NotEqual("a7a6", best.ToUci());
        Assert.NotEqual("a7a5", best.ToUci());
        Assert.NotEqual("e7e6", best.ToUci());
        Assert.True(best.ToUci() == "g6f5" || best.ToUci() == "e7e5", $"best move was {best.ToUci()} with score {lastInfo}\nCandidates:\n{candList}");
    }

    [Fact]
    public void ZapSearch_InvestigateVienna_Move6_RecapturesKnight_AvoidsKd2Blunder()
    {
        // Vienna Gambit: 1. e4 e5 2. Nc3 Nf6 3. f4 d5 4. fxe5 Nxe4 5. Qf3 Nxc3
        Board board = Fen.Parse(Fen.StartPosition);
        string[] moves = ["e2e4", "e7e5", "b1c3", "g8f6", "f2f4", "d7d5", "f4e5", "f6e4", "d1f3", "e4c3"];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }


        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candList = $"Total Nodes: {search.SharedState.TotalNodes}\n";
        for (int i = 0; i < search.SharedState.CandidateCount; i++)
        {
            var c = search.SharedState.GetCandidate(i);
            if (i < 8 || c.Move.ToUci() == "b2c3" || c.Move.ToUci() == "d2c3" || c.Move.ToUci() == "b2b4")
            {
                candList += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}, status={c.Status}, history={c.ScoreHistory}\n";
                foreach (var step in c.Trajectory)
                {
                    candList += $"   {step.SideToMove}: {step.Move.ToUci()} (ev={step.StaticEval}, d={step.EvalDelta})\n";
                }
            }
        }

        // White must recapture the knight on c3 (b2c3 or d2c3 or f3c3), never blunder Kd2 or ignore with b2b4!
        Assert.NotEqual("e1d2", best.ToUci());
        Assert.True(best.ToUci() == "b2c3" || best.ToUci() == "d2c3" || best.ToUci() == "f3c3",
            $"Expected recapture on c3, but got {best.ToUci()} with score {lastInfo}\nCandidates:\n{candList}");
    }

    [Fact]
    public void ZapSearch_InvestigateGame2_Move8_RecapturesOnE4()
    {
        // 1. e4 e5 2. Nf3 Nc6 3. c3 Nf6 4. d4 Nxe4 5. dxe5 h5 6. Bd3 d5 7. Nbd2 Bf5 8. Nxe4
        Board board = Fen.Parse(Fen.StartPosition);
        string[] moves = ["e2e4", "e7e5", "g1f3", "b8c6", "c2c3", "g8f6", "d2d4", "f6e4", "d4e5", "h7h5", "f1d3", "d7d5", "b1d2", "c8f5", "d2e4"];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }

        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candList = $"Total Nodes: {search.SharedState.TotalNodes}\n";
        for (int i = 0; i < search.SharedState.CandidateCount; i++)
        {
            var c = search.SharedState.GetCandidate(i);
            if (i < 6 || c.Move.ToUci() == "d2c3" || c.Move.ToUci() == "b2c3" || c.Move.ToUci() == "f3c3")
            {
                candList += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}, status={c.Status}, history={c.ScoreHistory}\n";
                foreach (var step in c.Trajectory)
                {
                    candList += $"   {step.SideToMove}: {step.Move.ToUci()} (ev={step.StaticEval}, d={step.EvalDelta})\n";
                }
            }
        }

        // Black MUST recapture the knight on e4 (d5xe4 or f5xe4) and NOT play Qe7 blundering a piece!
        Assert.True(best.ToUci() == "d5e4" || best.ToUci() == "f5e4", $"Expected recapture on e4, but got {best.ToUci()} with score {lastInfo}\nCandidates:\n{candList}");
    }

    [Fact]
    public void ZapSearch_InvestigateGame3_Move16_RecapturesBishopOnH6()
    {
        // 1. e4 e5 2. Nc3 Nc6 3. g3 Nf6 4. Bg2 Bc5 5. Na4 Bb4 6. c3 Be7 7. d4 d5 8. exd5
        // Nxd5 9. dxe5 Be6 10. Ne2 Nxe5 11. O-O Bf8 12. Bf4 Ng4 13. Nc5 Bxc5 14. Nd4 Nh6
        // 15. Nxe6 fxe6 16. Bxh6
        Board board = Fen.Parse(Fen.StartPosition);
        string[] moves = [
            "e2e4", "e7e5", "b1c3", "b8c6", "g2g3", "g8f6", "f1g2", "f8c5",
            "c3a4", "c5b4", "c2c3", "b4e7", "d2d4", "d7d5", "e4d5", "f6d5",
            "d4e5", "c8e6", "g1e2", "c6e5", "e1g1", "e7f8", "c1f4", "e5g4",
            "a4c5", "f8c5", "e2d4", "g4h6", "d4e6", "f7e6", "f4h6"
        ];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }

        var search = new ZapSearch();
        var budget = new global::Zanak.Time.SearchBudget(100, 200, 6, 12);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candList = $"Total Nodes: {search.SharedState.TotalNodes}\n";
        for (int i = 0; i < search.SharedState.CandidateCount; i++)
        {
            var c = search.SharedState.GetCandidate(i);
            if (i < 6 || c.Move.ToUci() == "d2c3" || c.Move.ToUci() == "b2c3" || c.Move.ToUci() == "f3c3")
            {
                candList += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}, status={c.Status}, history={c.ScoreHistory}\n";
                foreach (var step in c.Trajectory)
                {
                    candList += $"   {step.SideToMove}: {step.Move.ToUci()} (ev={step.StaticEval}, d={step.EvalDelta})\n";
                }
            }
        }

        // Black must NOT play Nb6 blundering the knight, but play g7xh6 or castle e8-g8
        Assert.NotEqual("d5b6", best.ToUci());
        Assert.True(best.ToUci() == "g7h6" || best.ToUci() == "e8g8", $"Expected recapture on h6 (g7h6) or castling (e8g8), but got {best.ToUci()} with score {lastInfo}\nCandidates:\n{candList}");
    }

    [Fact]
    public void ZapSearch_InvestigateGame4_Move24_RecapturesBishopOnB4()
    {
        // 1. e4 c5 2. Nf3 d6 3. Bc4 Nf6 4. O-O Bd7 5. d3 Nc6 6. Be3 e5 7. Nc3 Be6 8. Nd5
        // Nxd5 9. Bxd5 Qb6 10. Bxe6 fxe6 11. b3 Rc8 12. c4 h6 13. Qc2 Nb4 14. Qc3 Nc6
        // 15. h3 Rd8 16. Rad1 Rg8 17. Qd2 a5 18. Qe2 Kd7 19. Nd2 Kc8 20. Nf3 Qb4 21. Bd2
        // Qb6 22. Bc3 Rd7 23. Nh4 Nb4 24. Bxb4
        Board board = Fen.Parse(Fen.StartPosition);
        string[] moves = [
            "e2e4", "c7c5", "g1f3", "d7d6", "f1c4", "g8f6", "e1g1", "c8d7",
            "d2d3", "b8c6", "c1e3", "e7e5", "b1c3", "d7e6", "c3d5", "f6d5",
            "c4d5", "d8b6", "d5e6", "f7e6", "b2b3", "a8c8", "c2c4", "h7h6",
            "d1c2", "c6b4", "c2c3", "b4c6", "h2h3", "c8d8", "a1d1", "h8g8",
            "c3d2", "a7a5", "d2e2", "e8d7", "f3d2", "d7c8", "d2f3", "b6b4",
            "e3d2", "b4b6", "d2c3", "d8d7", "f3h4", "c6b4", "c3b4"
        ];
        Move lastMove = Move.Null;
        for (int mIdx = 0; mIdx < moves.Length; mIdx++)
        {
            var mStr = moves[mIdx];
            MoveList ml = default;
            MoveGenerator.GenerateLegalMoves(in board, ref ml);
            bool found = false;
            for (int i = 0; i < ml.Count; i++)
            {
                if (ml[i].ToUci() == mStr) { board.MakeMove(ml[i], out _); lastMove = ml[i]; found = true; break; }
            }
            Assert.True(found, $"Move {mIdx}: {mStr} was not legal");
        }

        var search = new ZapSearch(evaluator: new Anak.Data.Evaluation.DefaultEvaluator());
        var budget = new global::Zanak.Time.SearchBudget(1000, 2000, 8, 14);
        string lastInfo = "";
        Move best = search.Search(in board, in budget, lastMove: lastMove, infoCallback: s => lastInfo = $"{s.ScoreCp} (depth {s.Depth})");

        string candList = $"Best: {best.ToUci()}\nSearch candidates:\n";
        for (int i = 0; i < search.SharedState.CandidateCount; i++)
        {
            var c = search.SharedState.GetCandidate(i);
            if (i < 8 || c.Move.ToUci() == "c5b4" || c.Move.ToUci() == "a5b4")
            {
                candList += $"{c.Move.ToUci()}: score={c.Score}, dead={c.IsDeadStem}, status={c.Status}, history={c.ScoreHistory}\n";
                foreach (var st in c.Trajectory)
                {
                    candList += $"   {st.SideToMove}: {st.Move.ToUci()} (ev={st.StaticEval})\n";
                }
            }
        }

        // Black MUST recapture the bishop on b4 (c5xb4 or a5xb4) and NOT play Be7 blundering into Ng6!
        Assert.False(best.ToUci() == "f8e7", $"Expected not f8e7, but got {best.ToUci()} with score {lastInfo}\nCandidates:\n{candList}");
        Assert.True(best.ToUci() == "c5b4" || best.ToUci() == "a5b4", $"Expected recapture on b4 (c5b4 or a5b4), but got {best.ToUci()} with score {lastInfo}\nCandidates:\n{candList}");
    }
}
