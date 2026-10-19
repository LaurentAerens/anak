using Anak.Core;
using Anak.Data.PolyGlot;
using Xunit;

namespace Anak.Tests.Data;

public sealed class PolyGlotRandomnessTests
{
    [Fact]
    public void PolyGlotBook_DefaultsToWeightedRandomMode()
    {
        var book = new PolyGlotBook();
        Assert.Equal(BookMode.WeightedRandom, book.Mode);
        Assert.True(book.OwnBook);
    }

    [Fact]
    public void PolyGlotBook_UnloadedBook_ReturnsFalse()
    {
        var book = new PolyGlotBook();
        Board board = Fen.Parse(Fen.StartPosition);

        bool found = book.TryGetMove(in board, out Move move);
        Assert.False(found);
        Assert.True(move.IsNull);
    }

    [Fact]
    public void PolyGlotBook_ModeProperty_CanBeChanged()
    {
        var book = new PolyGlotBook
        {
            Mode = BookMode.BestMove
        };
        Assert.Equal(BookMode.BestMove, book.Mode);

        book.Mode = BookMode.UniformRandom;
        Assert.Equal(BookMode.UniformRandom, book.Mode);
    }

    [Fact]
    public void PolyGlotBook_LoadDefaultBook_LoadsCanonicalOpenings()
    {
        var book = new PolyGlotBook();
        bool loaded = book.LoadDefaultBook();

        Assert.True(loaded);
        Assert.True(book.IsLoaded);
        Assert.True(book.EntryCount > 0);

        Board startBoard = Fen.Parse(Fen.StartPosition);
        bool found = book.TryGetMove(in startBoard, out Move move);

        Assert.True(found);
        Assert.False(move.IsNull);
    }

    [Fact]
    public void PolyGlotBook_WeightedRandom_SelectsDiverseStartingMoves()
    {
        var book = new PolyGlotBook();
        book.LoadDefaultBook();
        Board startBoard = Fen.Parse(Fen.StartPosition);

        var distinctMoves = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            if (book.TryGetMove(in startBoard, out Move move, BookMode.WeightedRandom))
            {
                distinctMoves.Add(move.ToUci());
            }
        }

        // Must pick more than 1 distinct opening move across 100 iterations (e.g. e2e4, d2d4, c2c4, g1f3)
        Assert.True(distinctMoves.Count >= 2, $"Expected >= 2 distinct opening moves, found: {string.Join(", ", distinctMoves)}");
    }

    [Fact]
    public void PolyGlotBook_AnswersCommonRepliesToE4()
    {
        var book = new PolyGlotBook();
        book.LoadDefaultBook();

        Board board = Fen.Parse(Fen.StartPosition);
        board.MakeMove(new Move(Square.E2, Square.E4, MoveType.DoublePawnPush), out _);

        var distinctReplies = new HashSet<string>();
        for (int i = 0; i < 50; i++)
        {
            if (book.TryGetMove(in board, out Move reply, BookMode.WeightedRandom))
            {
                distinctReplies.Add(reply.ToUci());
            }
        }

        // Book should know major responses to 1.e4 (e7e5, c7c5, e7e6, c7c6, etc.)
        Assert.NotEmpty(distinctReplies);
    }
}
