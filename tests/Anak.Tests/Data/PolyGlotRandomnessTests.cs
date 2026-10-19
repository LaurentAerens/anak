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
}
