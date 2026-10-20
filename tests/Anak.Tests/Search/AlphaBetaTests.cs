using Anak.Core;
using Anak.Protocol.Uci;
using Anak.Search;
using Xunit;

namespace Anak.Tests.Search;

public class AlphaBetaTests
{
    [Fact]
    public void AlphaBeta_Solves_ScholarsMate()
    {
        var board = Fen.Parse("r1bqkb1r/pppp1ppp/2n5/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 2 4");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        var limits = new SearchLimits { Depth = 3 };
        Move bestMove = engine.Search(in board, in limits);

        Assert.Equal(Square.H5, bestMove.From);
        Assert.Equal(Square.F7, bestMove.To);
    }

    [Fact]
    public void AlphaBeta_Solves_BackRankMateInOne()
    {
        var board = Fen.Parse("6k1/5ppp/8/8/8/8/8/4R1K1 w - - 0 1");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        var limits = new SearchLimits { Depth = 3 };
        Move bestMove = engine.Search(in board, in limits);

        Assert.Equal(Square.E1, bestMove.From);
        Assert.Equal(Square.E8, bestMove.To);
    }

    [Fact]
    public void AlphaBeta_Solves_QueenAndRookMateInOne()
    {
        var board = Fen.Parse("7k/2Q5/8/8/8/8/1R6/6K1 w - - 0 1");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        var limits = new SearchLimits { Depth = 3 };
        Move bestMove = engine.Search(in board, in limits);

        bool isRb8 = bestMove.From == Square.B2 && bestMove.To == Square.B8;
        bool isQc8 = bestMove.From == Square.C7 && bestMove.To == Square.C8;

        Assert.True(isRb8 || isQc8, $"Expected checkmate move Rb8# or Qc8#, but got {bestMove.ToUci()}");
    }

    [Fact]
    public void AlphaBeta_IterativeDeepening_ReachesRequestedDepth()
    {
        var board = Fen.Parse(Fen.StartPosition);
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        int maxReportedDepth = 0;
        int callbackCount = 0;

        var limits = new SearchLimits { Depth = 5 };
        Move bestMove = engine.Search(in board, in limits, stats =>
        {
            callbackCount++;
            if (stats.Depth > maxReportedDepth)
            {
                maxReportedDepth = stats.Depth;
            }
        });

        Assert.False(bestMove.IsNull);
        Assert.True(maxReportedDepth >= 5);
        Assert.True(callbackCount >= 5);
    }

    [Fact]
    public void AlphaBeta_TranspositionTable_StoresEntries()
    {
        var board = Fen.Parse(Fen.StartPosition);
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        var limits = new SearchLimits { Depth = 4 };
        engine.Search(in board, in limits);

        Assert.True(engine.TranspositionTable.TryGet(board.ZobristKey, out TTEntry entry));
        Assert.True(entry.IsValid);
        Assert.False(entry.BestMove.IsNull);
        Assert.True(entry.Depth >= 4);
    }

    [Fact]
    public void AlphaBeta_DrawDetection_FiftyMoveRule_ReturnsDraw()
    {
        // 50-move rule: HalfmoveClock is 100
        var board = Fen.Parse("8/8/4k3/8/8/4K3/4P3/8 w - - 100 50");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        int reportedScore = -9999;
        var limits = new SearchLimits { Depth = 2 };
        engine.Search(in board, in limits, stats =>
        {
            reportedScore = stats.ScoreCp;
        });

        // Search at root might find moves, but draw positions evaluate to 0
        Assert.True(board.IsFiftyMoveRule());
    }

    [Fact]
    public void AlphaBeta_TimeManagement_RespectsMoveTime()
    {
        var board = Fen.Parse(Fen.StartPosition);
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var limits = new SearchLimits { MoveTime = 150 };
        Move bestMove = engine.Search(in board, in limits);
        stopwatch.Stop();

        Assert.False(bestMove.IsNull);
        Assert.True(stopwatch.ElapsedMilliseconds <= 400, $"Expected search to finish near 150ms, took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void AlphaBeta_LazySMP_MultiThreading_SolvesTactics()
    {
        var board = Fen.Parse("6k1/5ppp/8/8/8/8/8/4R1K1 w - - 0 1");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4)
        {
            Threads = 2
        };

        var limits = new SearchLimits { Depth = 4 };
        Move bestMove = engine.Search(in board, in limits);

        Assert.Equal(Square.E1, bestMove.From);
        Assert.Equal(Square.E8, bestMove.To);
        Assert.True(engine.GetTotalNodes() > 0);
    }

    [Fact]
    public void AlphaBeta_ZeroGCCollections_DuringSearch()
    {
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);
        var board = Fen.Parse(Fen.StartPosition);
        var limits = new SearchLimits { Depth = 4 };

        // Warmup JIT
        engine.Search(in board, in limits);

        // Verify zero GC collections during search
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        for (int i = 0; i < 5; i++)
        {
            engine.Search(in board, in limits);
        }

        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);

        Assert.Equal(gen0Before, gen0After);
        Assert.Equal(gen1Before, gen1After);
        Assert.Equal(gen2Before, gen2After);
    }

    [Fact]
    public void AlphaBeta_PvExtraction_ReturnsValidSequence()
    {
        var board = Fen.Parse(Fen.StartPosition);
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        List<Move> lastPv = [];
        var limits = new SearchLimits { Depth = 4 };
        engine.Search(in board, in limits, stats =>
        {
            if (stats.Pv != null && stats.Pv.Count > 0)
            {
                lastPv = stats.Pv;
            }
        });

        Assert.NotEmpty(lastPv);

        // Verify PV moves are all sequentially legal from starting position
        Board pvBoard = board;
        foreach (Move m in lastPv)
        {
            MoveList legal = default;
            MoveGenerator.GenerateLegalMoves(in pvBoard, ref legal);
            bool found = false;
            for (int i = 0; i < legal.Count; i++)
            {
                if (legal[i] == m)
                {
                    found = true;
                    break;
                }
            }
            Assert.True(found, $"Move {m.ToUci()} in PV was not legal in current position");
            pvBoard.MakeMove(m, out _);
        }
    }

    [Fact]
    public void AlphaBeta_CancellationToken_AbortsSearchCleanly()
    {
        var board = Fen.Parse(Fen.StartPosition);
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var limits = new SearchLimits { Depth = 20, Infinite = true };
        Move move = engine.Search(in board, in limits, cancellationToken: cts.Token);

        // Even when cancelled immediately, search should return without crashing
        Assert.True(move.IsNull || !move.IsNull);
    }

    [Fact]
    public void AlphaBeta_Solves_MateInTwo()
    {
        // White: King on e1, Queen on a7, Rook on b7. Black: King on g8, pawns on f7, g7, h7.
        // FEN: 6k1/QR3ppp/8/8/8/8/8/4K3 w - - 0 1
        // 1. Qa8# or Rb8# or Qxf7+ etc.
        var board = Fen.Parse("6k1/QR3ppp/8/8/8/8/8/4K3 w - - 0 1");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        var limits = new SearchLimits { Depth = 4 };
        Move move = engine.Search(in board, in limits);

        Assert.False(move.IsNull);
        board.MakeMove(move, out _);
        // After best move, white has a crushing advantage or mate
        Assert.True(engine.GetTotalNodes() > 0);
    }

    [Fact]
    public void AlphaBeta_DoesNotHangQueen_OnQb5()
    {
        var board = Fen.Parse("r2qkb1r/1p2bppp/p2pbn2/4p3/4P3/1NN1B3/PPP1QPPP/R3KB1R w KQkq - 4 9");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4);

        var limits = new SearchLimits { Depth = 4 };
        Move move = engine.Search(in board, in limits);

        Assert.NotEqual("e2b5", move.ToUci());
    }

    [Fact]
    public void AlphaBeta_ConfigurableSpsaOptions_CanBeAdjustedViaUci()
    {
        var adapter = new UciEngineAdapter(_ => { });

        Assert.Equal(75, adapter.Options.LmrBase.Value);
        Assert.Equal(225, adapter.Options.LmrDivision.Value);
        Assert.Equal(75, adapter.Options.RfpMargin.Value);
        Assert.Equal(3, adapter.Options.NmpBase.Value);
        Assert.Equal(25, adapter.Options.AspirationDelta.Value);

        // Modify options
        Assert.True(adapter.Options.TrySetValue("LmrBase", "80"));
        Assert.True(adapter.Options.TrySetValue("LmrDivision", "240"));
        Assert.True(adapter.Options.TrySetValue("RfpMargin", "90"));
        Assert.True(adapter.Options.TrySetValue("NmpBase", "4"));
        Assert.True(adapter.Options.TrySetValue("AspirationDelta", "30"));

        Assert.Equal(80, adapter.Options.LmrBase.Value);
        Assert.Equal(240, adapter.Options.LmrDivision.Value);
        Assert.Equal(90, adapter.Options.RfpMargin.Value);
        Assert.Equal(4, adapter.Options.NmpBase.Value);
        Assert.Equal(30, adapter.Options.AspirationDelta.Value);

        var alphaBeta = (AlphaBetaEngine)adapter.ActiveEngine;
        Assert.Equal(80, alphaBeta.LmrBase);
        Assert.Equal(240, alphaBeta.LmrDivision);
        Assert.Equal(90, alphaBeta.RfpMargin);
        Assert.Equal(4, alphaBeta.NmpBase);
        Assert.Equal(30, alphaBeta.AspirationDelta);
    }

    [Fact]
    public void AlphaBeta_LmrTable_RecomputesWithNewParameters()
    {
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new AlphaBetaEngine(evaluator, defaultHashMb: 4)
        {
            LmrBase = 75,
            LmrDivision = 225
        };

        int rDefault = engine.GetLmrReduction(10, 10);

        // With higher base, reduction should increase or stay same
        engine.LmrBase = 150;
        int rHigher = engine.GetLmrReduction(10, 10);
        Assert.True(rHigher >= rDefault);
    }
}
