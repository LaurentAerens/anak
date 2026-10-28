using System.Runtime.CompilerServices;
using Anak.Core;
using Anak.Search;
using Xunit;

namespace Anak.Tests.Search;

public class MinimaxFoundationsTests
{
    [Fact]
    public void TTEntry_SizeIs32Bytes()
    {
        Assert.Equal(32, Unsafe.SizeOf<TTEntry>());
    }

    [Fact]
    public void TTCluster_SizeIs64Bytes()
    {
        Assert.Equal(64, Unsafe.SizeOf<TTCluster>());
    }

    [Theory]
    [InlineData(500, 2, 500)]
    [InlineData(-500, 4, -500)]
    [InlineData(29990, 3, 29993)]  // Mate in 10: 30000 - 10 = 29990. At ply 3 -> 29993
    [InlineData(-29990, 5, -29995)] // Mated in 10: -29990. At ply 5 -> -29995
    public void TTEntry_MateScoreConversion_RoundTrips(int score, int ply, int expectedTt)
    {
        short ttScore = TTEntry.ScoreToTt(score, ply);
        Assert.Equal((short)expectedTt, ttScore);

        int searchScore = TTEntry.ScoreFromTt(ttScore, ply);
        Assert.Equal(score, searchScore);
    }

    [Fact]
    public void TranspositionTable_MinimaxStoreAndRetrieve()
    {
        var tt = new TranspositionTable(1);
        ulong key = 0xABCDEF1234567890UL;
        Move move = new Move(Square.E2, Square.E4, MoveType.DoublePawnPush);

        tt.Store(key, move, depth: 8, score: 150, age: tt.CurrentAge, flags: TTFlags.Exact);

        bool found = tt.TryGet(key, out TTEntry entry);
        Assert.True(found);
        Assert.Equal(key, entry.Key);
        Assert.Equal(move, entry.BestMove);
        Assert.Equal((byte)8, entry.Depth);
        Assert.Equal((short)150, entry.Score);
        Assert.Equal(TTFlags.Exact, entry.Flags);
        Assert.Equal(tt.CurrentAge, entry.Age);
        Assert.True(entry.IsValid);
    }

    [Fact]
    public void TranspositionTable_MinimaxHashfullPermille_Works()
    {
        var tt = new TranspositionTable(1);
        Assert.Equal(0, tt.HashfullPermille());

        for (ulong i = 1; i <= 100; i++)
        {
            tt.Store(i * 99991UL, new Move(Square.D2, Square.D4), depth: 6, score: 50, age: tt.CurrentAge, flags: TTFlags.Exact);
        }

        int hashfull = tt.HashfullPermille();
        Assert.InRange(hashfull, 1, 1000);
    }

    [Fact]
    public void SearchStack_ResetAndKillers()
    {
        SearchStack stack = default;
        Move k1 = new Move(Square.E2, Square.E4);
        Move k2 = new Move(Square.D2, Square.D4);

        stack.AddKiller(k1);
        Assert.Equal(k1, stack.Killer0);
        Assert.Equal(Move.Null, stack.Killer1);

        stack.AddKiller(k2);
        Assert.Equal(k2, stack.Killer0);
        Assert.Equal(k1, stack.Killer1);

        // Same move again should not shift
        stack.AddKiller(k2);
        Assert.Equal(k2, stack.Killer0);
        Assert.Equal(k1, stack.Killer1);

        stack.Reset();
        Assert.Equal(Move.Null, stack.Killer0);
        Assert.Equal(Move.Null, stack.Killer1);
        Assert.Equal(0, stack.StaticEval);
        Assert.Equal(0, stack.MoveCount);
        Assert.False(stack.InCheck);
        Assert.Equal(Piece.None, stack.MovedPiece);
    }

    [Fact]
    public void HistoryTable_GravityUpdates_RemainBounded()
    {
        var history = new HistoryTable();

        for (int i = 0; i < 500; i++)
        {
            history.Update(Color.White, Square.E2, Square.E4, 1000);
        }

        int score = history.GetScore(Color.White, Square.E2, Square.E4);
        Assert.InRange(score, 10000, HistoryTable.MaxHistory);

        for (int i = 0; i < 500; i++)
        {
            history.Update(Color.White, Square.E2, Square.E4, -2000);
        }

        score = history.GetScore(Color.White, Square.E2, Square.E4);
        Assert.InRange(score, -HistoryTable.MaxHistory, 0);
    }

    [Fact]
    public void ContinuationHistory_1PlyAnd2Ply_Updates()
    {
        var cont = new ContinuationHistory();

        cont.Update1Ply(Piece.BlackKnight, Square.F6, Piece.WhiteBishop, Square.G5, 500);
        cont.Update2Ply(Piece.WhitePawn, Square.E4, Piece.WhiteBishop, Square.G5, 300);

        int s1 = cont.Get1PlyScore(Piece.BlackKnight, Square.F6, Piece.WhiteBishop, Square.G5);
        int s2 = cont.Get2PlyScore(Piece.WhitePawn, Square.E4, Piece.WhiteBishop, Square.G5);
        int combined = cont.GetScore(Piece.BlackKnight, Square.F6, Piece.WhitePawn, Square.E4, Piece.WhiteBishop, Square.G5);

        Assert.True(s1 > 0);
        Assert.True(s2 > 0);
        Assert.Equal(s1 + s2, combined);
    }

    [Fact]
    public void CaptureHistory_UpdatesCorrectly()
    {
        var capHist = new CaptureHistory();

        capHist.Update(Piece.WhiteBishop, Square.F6, Piece.BlackKnight, 400);
        int score = capHist.GetScore(Piece.WhiteBishop, Square.F6, Piece.BlackKnight);
        Assert.True(score > 0);
    }

    [Fact]
    public void CorrectionHistory_PawnHashAdjustment()
    {
        var corr = new CorrectionHistory();
        Board board = Fen.Parse("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        ulong pawnKey = CorrectionHistory.ComputePawnKey(in board);
        Assert.True(pawnKey != 0);

        corr.Update(pawnKey, depth: 8, searchScore: 100, staticEval: 20);
        int correction = corr.GetCorrection(pawnKey);
        Assert.True(correction > 0);
        Assert.True(correction <= CorrectionHistory.MaxCorrection);

        int adjusted = corr.GetAdjustedEval(in board, 50);
        Assert.Equal(50 + correction, adjusted);
    }

    [Fact]
    public void MovePicker_OrdersStagesProperly()
    {
        Board board = Fen.Parse("r1bqkbnr/ppp2ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4");

        Move ttMove = new Move(Square.B5, Square.C6, MoveType.Capture);

        var history = new HistoryTable();
        var capHist = new CaptureHistory();
        var contHist = new ContinuationHistory();

        var picker = new MovePicker(
            in board,
            ttMove,
            killer0: new Move(Square.D2, Square.D3, MoveType.Quiet),
            killer1: new Move(Square.D2, Square.D4, MoveType.DoublePawnPush),
            counterMove: Move.Null,
            history: history,
            captureHistory: capHist,
            continuationHistory: contHist);

        Assert.True(picker.MoveCount > 0);

        // First move must be TT move
        Assert.True(picker.Next(out Move firstMove));
        Assert.Equal(ttMove, firstMove);

        // Following moves should be generated without error
        int totalPicked = 1;
        while (picker.Next(out Move m))
        {
            Assert.False(m.IsNull);
            Assert.NotEqual(firstMove, m);
            totalPicked++;
        }

        Assert.Equal(picker.MoveCount, totalPicked);
    }

    [Fact]
    public void QuiescenceSearch_StandPatAndCaptures()
    {
        Board board = Fen.Parse("r1bqk1nr/pppp1ppp/2n5/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4");
        var eval = new Anak.Data.Evaluation.DefaultEvaluator();
        var qs = new QuiescenceSearch(eval);

        int score = qs.Search(ref board, -Value.InfiniteValue, Value.InfiniteValue);
        Assert.InRange(score, -1000, 1000);
    }

    [Fact]
    public void SearchHeuristics_LmrTable_FormulaMatches()
    {
        for (int depth = 1; depth < 32; depth++)
        {
            for (int moves = 1; moves < 32; moves++)
            {
                int expected = (int)((SearchHeuristics.TunedLmrBase / 100.0) + (Math.Log(depth) * Math.Log(moves) / (SearchHeuristics.TunedLmrDivision / 100.0)));
                Assert.Equal(expected, SearchHeuristics.GetBaseLmr(depth, moves));
            }
        }
    }

    [Fact]
    public void SearchHeuristics_DynamicNmpReduction_RespectsOpenness()
    {
        int rClosed = SearchHeuristics.GetNmpReduction(depth: 10, eval: 100, beta: 50, openness: 0.30f);
        int rOpen = SearchHeuristics.GetNmpReduction(depth: 10, eval: 100, beta: 50, openness: 0.70f);

        Assert.True(rClosed >= rOpen);
    }

    [Fact]
    public void SearchHeuristics_HasNonPawnMaterial_IdentifiesEndgame()
    {
        Board board = Fen.Parse("8/8/4k3/8/8/4K3/4P3/8 w - - 0 1");
        Assert.False(SearchHeuristics.HasNonPawnMaterial(in board, Color.White));
        Assert.False(SearchHeuristics.HasNonPawnMaterial(in board, Color.Black));

        Board boardWithKnights = Fen.Parse("8/8/4k3/8/8/4KN2/4P3/8 w - - 0 1");
        Assert.True(SearchHeuristics.HasNonPawnMaterial(in boardWithKnights, Color.White));
    }
}
