using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Anak.Core;
using Anak.Data.Nnue;
using Anak.Protocol.Uci;
using Anak.Search;
using Xunit;
using Xunit.Abstractions;

namespace Anak.Tests.Search;

public class MathSimplificationEquivalenceTests
{
    private readonly ITestOutputHelper _output;

    public MathSimplificationEquivalenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static readonly string[] SuitePositions =
    [
        // Standard Benchmark Positions (1-6)
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        "8/8/4k3/8/8/4K3/4P3/8 w - - 0 1",

        // Diverse Openings & Middlegames (Closed, Open, Semi-Open, Tension, Rams) (7-30)
        "rnbqk2r/1p3pbp/p2ppnp1/8/2PNP3/2NB4/PP3PPP/R1BQ1RK1 w kq - 2 9",
        "rq2kb1r/pb1p1ppp/1pn1pn2/8/2PpP3/2N2NP1/PP3PBP/R1BQ1RK1 w kq - 0 9",
        "r1bq1rk1/pp1n1pbp/2pp1np1/4p3/2PPP3/2N1BP2/PP1QN1PP/R3KB1R w KQ - 2 9",
        "r1bq1rk1/pp1nppbp/3p1np1/8/2PNP3/2N5/PP2BPPP/R1BQ1RK1 w - - 4 9",
        "r2qk2r/pppn1ppp/3bpn2/7b/2BP4/2N1PN1P/PP3PP1/R1BQ1RK1 w kq - 1 9",
        "r1b1kb1r/p1pqnppp/1pn1p3/3pP3/P2P2Q1/2N2N2/1PPB1PPP/R3KB1R w KQkq - 3 9",
        "r1bq1rk1/pp2nppp/2n1p3/2ppP3/3P2Q1/P1P2N2/2P2PPP/R1B1KB1R w KQ - 5 9",
        "r2qkbnr/pp1nppp1/2p5/7p/3P3P/3b1NN1/PPP2PP1/R1BQK2R w KQkq - 0 9",
        "r1b1kb1r/1pqn1ppp/p2ppn2/8/3NPP2/2N2B2/PPP3PP/R1BQK2R w KQkq - 3 9",
        "rnb1k2r/p1q1nppp/1p2p3/2ppP3/P2P4/2P2N2/2P2PPP/R1BQKB1R w KQkq - 0 9",
        "r1bq1rk1/pppp1ppp/2n5/1B6/1b2P3/2N1Q3/PPP2PPP/R1B1K2R w KQ - 5 9",
        "r2qkb1r/pp3p1p/n1p1pp2/5b2/2BP1B2/5N2/PPP2PPP/R2QK2R w KQkq - 2 9",
        "rn2kb1r/ppq2p1p/2p1pp2/5b2/3P4/5N2/PPP1BPPP/R1BQ1RK1 w kq - 0 9",
        "r1bq1rk1/pp3pbp/n2ppnp1/2pP4/2P1P3/2NB1N1P/PP3PP1/R1BQK2R w KQ - 2 9",
        "rnbq1rk1/pp3pbp/3p1np1/3pp3/2P1P3/2N1BP2/PP1Q2PP/R3KBNR w KQ - 0 9",
        "1rbqkb1r/npp2ppp/p3pn2/8/2pP4/4P1P1/PP1N1PBP/RNBQ1RK1 w k - 3 9",
        "rn1qkb1r/pb1p1ppp/1p2p3/4P3/2Pp2n1/5NP1/PP1N1PBP/R1BQK2R w KQkq - 1 9",
        "r1b1k2r/pp1npp1p/3p1np1/q1pP4/2P1PP2/2PB4/P5PP/R1BQK1NR w KQkq - 3 9",
        "rnbq1rk1/4ppbp/p2p1np1/1PpP2B1/4P3/2N5/PP2BPPP/R2QK1NR w KQ - 0 9",
        "rn2kb1r/ppp2ppp/4pnb1/q3N3/3P2P1/2N4P/PPP2P2/R1BQKB1R w KQkq - 0 9",
        "rnb2rk1/pp2ppbp/3p1np1/q7/2PN4/2N3P1/PP2PPBP/R1BQ1RK1 w - - 1 9",
        "1rbq1rk1/pp1nbppp/2pp1n2/4p3/2PP4/2N2NP1/PPQ1PPBP/R1B2RK1 w - - 6 9",
        "r1b1k1nr/pp1pqpbp/2p3p1/8/2BpP3/2P2Q2/PP1P1PPP/RNB2RK1 w kq - 3 9",
        "r2qkb1r/pppn1p1p/3p1np1/8/2PNP3/2N5/PP2QPPP/R1B1K2R w KQkq - 0 9",

        // En Passant & Tactical Positions (31-40)
        "rn1qkb1r/pp1n1ppp/2p5/4p2b/P1NP4/2N2P2/1P2P1PP/R1BQKB1R w KQkq e6 0 9",
        "rnb1k2r/pp1p2pp/4p3/q1p2p2/2PPn3/2PQ1NP1/P2BPP1P/R3KB1R w KQkq f6 0 9",
        "rnbqk2r/ppp2pb1/3p3p/4p1p1/3PP1n1/2N3B1/PPPQ1PPP/R3KBNR w KQkq e6 0 9",
        "rnb1k2r/ppq2pb1/2pp1npp/4p3/3PPP1B/2PB4/PP1N2PP/R2QK1NR w KQkq e6 0 9",
        "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
        "rnbqkbnr/pppp1ppp/8/8/3pP3/8/PPP2PPP/RNBQKBNR b KQkq e3 0 3",
        "8/8/8/2k5/3pP3/8/8/4K3 b - e3 0 1", // EP with horizontal pin risk
        "8/8/8/8/k2pP2Q/8/8/4K3 b - e3 0 1", // EP pinned horizontally by Queen!
        "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
        "r3k2r/pppq1ppp/2npbn2/2b1p3/2B1P3/2NPBN2/PPPQ1PPP/R3K2R b KQkq - 6 6",

        // Specialized Experts: Queenless, Imbalance, Attack (Opposite Castling), Endgame (41-56)
        "r3k2r/ppp2ppp/2n1bn2/3p4/3P4/2N1BN2/PPP2PPP/R3K2R w KQkq - 0 1", // Queenless
        "2kr1b1r/ppp2ppp/2n1bn2/3p4/3P4/2N1BN2/PPP2PPP/R4RK1 b - - 0 1",   // Queenless
        "r1bq1rk1/ppp2ppp/2n2n2/3p4/3P4/2PB1N2/PP3PPP/R1BQR1K1 w - - 0 1", // Open
        "r1bq1rk1/2p1bppp/p1np1n2/1p2p3/4P3/1BP2N1P/PP1P1PP1/RNBQR1K1 w - - 0 1", // Closed Ruy Lopez
        "2kr3r/ppp1qppp/2npbn2/4p1B1/4P3/2NP1Q2/PPP2PPP/R4RK1 w - - 0 1", // Opposite-side castling (Attack)
        "r4rk1/ppp1qppp/2npbn2/4p3/4P1b1/2NP1N2/PPP1QPPP/2KR3R b - - 0 1", // Opposite-side castling Black to move
        "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNB1KB1R w KQkq - 0 1", // Imbalance (missing White Queen)
        "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2",
        "4k3/pppppppp/8/8/8/8/PPPPPPPP/4K2R w K - 0 1", // Imbalance (Rook advantage)
        "8/5pk1/6p1/8/3R4/6P1/5PK1/2r5 w - - 0 1", // Rook Endgame
        "8/2k5/8/2n5/4B3/8/2K5/8 b - - 0 1", // Minor Piece Endgame
        "4k3/P7/8/8/8/8/7p/4K3 w - - 0 1", // Promotions both sides
        "r3k2r/P6P/8/8/8/8/p6p/R3K2R b KQkq - 0 1", // Capture promotions & castling rights corner captures
        "r1bq1rk1/ppp2ppp/2nb1n2/3pp3/8/2PP1NP1/PP2PPBP/RNBQ1RK1 b - - 0 6",
        "2rq1rk1/pp2bppp/2n1pn2/2pp4/3P1B2/2PBPN2/PP1N1PPP/R2Q1RK1 w - - 4 9",
        "r4rk1/1bq1bppp/p1n1pn2/1p1p4/3P1B2/1B2PN2/PPQN1PPP/R4RK1 w - - 2 12",

        // Tactical Checks, Double Checks, Pins, and SEE stress positions (57-64)
        "rnb1kbnr/pppp1ppp/8/4p3/5PPq/8/PPPPP2P/RNBQKBNR w KQkq - 1 3", // In check
        "r1bqk2r/ppp2ppp/2n5/3np1N1/2B5/8/PPPP1PPP/RNBQK2R b KQkq - 1 5", // Fried liver f7 target
        "4k3/8/4r3/8/4N3/8/4K3/8 w - - 0 1", // Pinned knight on e4
        "4k3/8/4r3/8/4R3/8/4K3/8 w - - 0 1", // Pinned rook on e4 (can move along file)
        "4k3/8/4r3/8/4B3/8/4K3/8 w - - 0 1", // Pinned bishop on e4 (cannot move)
        "4k2b/8/8/4B3/8/8/4K3/8 w - - 0 1", // Pinned bishop on diagonal (can move along diagonal)
        "3qk3/8/8/8/8/8/4R3/4K2R w K - 0 1", // Castling while safe
        "3r1k2/8/8/8/8/8/8/R3K2R w KQ - 0 1" // Queenside castling blocked by d1 attack
    ];

    public record PositionSnapshot(
        string Fen,
        ulong ZobristKey,
        ulong PawnKey,
        int OpennessMillipawns,
        byte ExpertId,
        int StaticEval,
        ulong SeeAndLegalityChecksum,
        string BestMoveUci,
        int SearchScoreCp,
        ulong SearchNodes);

    [Fact]
    public void VerifyGivesCheckEquivalence()
    {
        for (int i = 0; i < SuitePositions.Length; i++)
        {
            string fen = SuitePositions[i];
            Board board = Fen.Parse(fen);
            MoveList legalMoves = default;
            MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);

            for (int m = 0; m < legalMoves.Count; m++)
            {
                Move mv = legalMoves[m];
                bool fast = board.GivesCheck(mv);

                Board next = board;
                next.MakeMove(mv, out _);
                bool expected = next.IsInCheck();
                Assert.True(fast == expected, $"Pos {i} ({fen}) move {mv.ToUci()}: GivesCheck={fast}, expected={expected}");

                MoveList replyMoves = default;
                MoveGenerator.GenerateLegalMoves(in next, ref replyMoves);
                for (int r = 0; r < replyMoves.Count; r++)
                {
                    Move rmv = replyMoves[r];
                    bool rFast = next.GivesCheck(rmv);
                    Board next2 = next;
                    next2.MakeMove(rmv, out _);
                    bool rExpected = next2.IsInCheck();
                    Assert.True(rFast == rExpected, $"Pos {i} ({fen}) after {mv.ToUci()} reply {rmv.ToUci()}: GivesCheck={rFast}, expected={rExpected}");
                }
            }
        }
    }

    [Fact]
    public void VerifyOrGenerateBaselineSnapshots()
    {
        string snapshotPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Search", "baseline_snapshots_64.json");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var currentSnapshots = new List<PositionSnapshot>(SuitePositions.Length);

        for (int i = 0; i < SuitePositions.Length; i++)
        {
            string fen = SuitePositions[i];
            Board board = Fen.Parse(fen);

            ulong zobrist = board.ZobristKey;
            ulong pawnKey = CorrectionHistory.ComputePawnKey(in board);
            float openness = PawnTopology.CalculateOpenness(in board);
            int opennessMilli = (int)MathF.Round(openness * 1000.0f);
            byte expert = (byte)PawnTopology.ClassifyExpert(in board);

            // Reset evaluator state & compute static eval
            if (evaluator is IIncrementalEvaluator inc)
            {
                inc.InitSearch(in board);
            }
            MoveList legalMoves = default;
            MoveGenerator.GenerateLegalMoves(in board, ref legalMoves);
            int staticEval = evaluator.Evaluate(in board, legalMoves.AsReadOnlySpan()).Raw;

            // Compute SEE and IsLegal checksum across all legal moves and synthetic candidate moves
            ulong seeAndLegalHash = (ulong)legalMoves.Count;
            for (int m = 0; m < legalMoves.Count; m++)
            {
                Move mv = legalMoves[m];
                Assert.True(board.IsLegal(in mv), $"Legal move {mv.ToUci()} reported illegal on {fen}");
                int see = StaticExchangeEvaluator.Evaluate(in board, mv);
                bool see0 = StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, mv, 0);
                bool seeNeg50 = StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, mv, -50);
                bool see200 = StaticExchangeEvaluator.HasPositiveOrEqualSee(in board, mv, 200);

                seeAndLegalHash = (seeAndLegalHash * 1099511628211UL) ^ (ulong)(uint)mv.RawValue;
                seeAndLegalHash = (seeAndLegalHash * 1099511628211UL) ^ (ulong)(uint)(see + 100000);
                seeAndLegalHash = (seeAndLegalHash * 1099511628211UL) ^ (ulong)((see0 ? 1u : 0u) | (seeNeg50 ? 2u : 0u) | (see200 ? 4u : 0u));
            }

            // Also probe pseudo/synthetic moves for IsLegal equivalence
            for (int from = 0; from < 64; from += 3)
            {
                for (int to = 0; to < 64; to += 5)
                {
                    foreach (MoveType mt in new[] { MoveType.Quiet, MoveType.Capture, MoveType.EnPassant, MoveType.DoublePawnPush, MoveType.QueenPromotion })
                    {
                        Move cand = new Move((Square)from, (Square)to, mt);
                        bool leg = board.IsLegal(in cand);
                        seeAndLegalHash = (seeAndLegalHash * 1099511628211UL) ^ (leg ? 0xA5A5UL : 0x5A5AUL);
                    }
                }
            }

            // Fresh engine for 100% deterministic single-thread depth 6 search
            var engine = new AlphaBetaEngine(UciEngineAdapter.CreateEvaluator(), defaultHashMb: 16)
            {
                Threads = 1,
                EnableQsChecks = false
            };
            var limits = new SearchLimits { Depth = 6 };
            ulong nodes = 0;
            int scoreCp = 0;
            Move bestMove = engine.Search(in board, in limits, info =>
            {
                if (info.Depth == 6)
                {
                    nodes = info.Nodes;
                    scoreCp = info.ScoreCp;
                }
            });
            if (nodes == 0)
            {
                nodes = engine.GetTotalNodes();
                scoreCp = engine.LastScoreCp;
            }

            currentSnapshots.Add(new PositionSnapshot(
                fen,
                zobrist,
                pawnKey,
                opennessMilli,
                expert,
                staticEval,
                seeAndLegalHash,
                bestMove.ToUci(),
                scoreCp,
                nodes));
        }

        if (!System.IO.File.Exists(snapshotPath))
        {
            string json = JsonSerializer.Serialize(currentSnapshots, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(snapshotPath, json);
            _output.WriteLine($"Generated baseline snapshot file with {currentSnapshots.Count} positions at {snapshotPath}");
            return;
        }

        var baseline = JsonSerializer.Deserialize<List<PositionSnapshot>>(System.IO.File.ReadAllText(snapshotPath))!;
        Assert.Equal(baseline.Count, currentSnapshots.Count);

        for (int i = 0; i < baseline.Count; i++)
        {
            var exp = baseline[i];
            var act = currentSnapshots[i];

            Assert.True(exp.ZobristKey == act.ZobristKey, $"Pos {i} ({exp.Fen}): ZobristKey mismatch");
            Assert.True(exp.PawnKey == act.PawnKey, $"Pos {i} ({exp.Fen}): PawnKey mismatch");
            Assert.True(exp.OpennessMillipawns == act.OpennessMillipawns, $"Pos {i} ({exp.Fen}): Openness mismatch ({exp.OpennessMillipawns} vs {act.OpennessMillipawns})");
            Assert.True(exp.ExpertId == act.ExpertId, $"Pos {i} ({exp.Fen}): ExpertId mismatch ({exp.ExpertId} vs {act.ExpertId})");
            Assert.True(exp.StaticEval == act.StaticEval, $"Pos {i} ({exp.Fen}): StaticEval mismatch ({exp.StaticEval} vs {act.StaticEval})");
            Assert.True(exp.SeeAndLegalityChecksum == act.SeeAndLegalityChecksum, $"Pos {i} ({exp.Fen}): SeeAndLegalityChecksum mismatch");
            Assert.True(exp.BestMoveUci == act.BestMoveUci, $"Pos {i} ({exp.Fen}): BestMove mismatch ({exp.BestMoveUci} vs {act.BestMoveUci})");
            Assert.True(exp.SearchScoreCp == act.SearchScoreCp, $"Pos {i} ({exp.Fen}): SearchScoreCp mismatch ({exp.SearchScoreCp} vs {act.SearchScoreCp})");
            Assert.True(exp.SearchNodes == act.SearchNodes, $"Pos {i} ({exp.Fen}): SearchNodes mismatch ({exp.SearchNodes} vs {act.SearchNodes})");
        }
    }
}
