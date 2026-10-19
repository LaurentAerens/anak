using System;
using Anak.Core;
using Anak.Protocol.Uci;
using Anak.Search;
using Xunit;

namespace Anak.Tests.Search;

public class MCTSTests
{
    [Fact]
    public void MCTS_Solves_ScholarsMate()
    {
        // Position before Scholar's mate:
        // White: Queen on h5, Bishop on c4, King on e1
        // Black: Pawns on a7, b7, c7, d7, f7, g7, h7, King on e8, Knight on c6
        // FEN: r1bqkb1r/pppp1ppp/2n5/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 2 4
        // Winning move: Qxf7# (h5 -> f7)
        var board = Fen.Parse("r1bqkb1r/pppp1ppp/2n5/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 2 4");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new MCTSEngine(evaluator, defaultHashMb: 4, arenaCapacity: 100_000);

        var limits = new SearchLimits { Nodes = 1000 };
        Move bestMove = engine.Search(in board, in limits);

        Assert.Equal(Square.H5, bestMove.From);
        Assert.Equal(Square.F7, bestMove.To);
    }

    [Fact]
    public void MCTS_Solves_BackRankMateInOne()
    {
        // Position: White King on g1, Rook on e1. Black King on g8, pawns on f7, g7, h7.
        // FEN: 6k1/5ppp/8/8/8/8/8/4R1K1 w - - 0 1
        // Winning move: Re1-e8# (e1 -> e8)
        var board = Fen.Parse("6k1/5ppp/8/8/8/8/8/4R1K1 w - - 0 1");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new MCTSEngine(evaluator, defaultHashMb: 4, arenaCapacity: 100_000);

        var limits = new SearchLimits { Nodes = 500 };
        Move bestMove = engine.Search(in board, in limits);

        Assert.Equal(Square.E1, bestMove.From);
        Assert.Equal(Square.E8, bestMove.To);
    }

    [Fact]
    public void MCTS_Solves_QueenAndRookMateInOne()
    {
        // Position: Black King on h8 trapped. White Queen on c7, Rook on b8.
        // FEN: 7k/2Q5/8/8/8/8/1R6/6K1 w - - 0 1
        // Winning moves: Rb8# or Qc8#
        var board = Fen.Parse("7k/2Q5/8/8/8/8/1R6/6K1 w - - 0 1");
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new MCTSEngine(evaluator, defaultHashMb: 4, arenaCapacity: 100_000);

        var limits = new SearchLimits { Nodes = 500 };
        Move bestMove = engine.Search(in board, in limits);

        bool isRb8 = bestMove.From == Square.B2 && bestMove.To == Square.B8;
        bool isQc8 = bestMove.From == Square.C7 && bestMove.To == Square.C8;

        Assert.True(isRb8 || isQc8, $"Expected checkmate move Rb8# or Qc8#, but got {bestMove.ToUci()}");
    }

    [Fact]
    public void MCTS_ZeroGCCollections_DuringSearch()
    {
        var evaluator = UciEngineAdapter.CreateEvaluator();
        var engine = new MCTSEngine(evaluator, defaultHashMb: 4, arenaCapacity: 100_000);
        var board = Fen.Parse(Fen.StartPosition);
        var limits = new SearchLimits { Nodes = 500 };

        // Warmup JIT
        engine.Search(in board, in limits);

        // Verify zero GC collections during search
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
}
