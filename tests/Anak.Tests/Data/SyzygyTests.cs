using Anak.Core;
using Anak.Data.PolyGlot;
using Anak.Data.Syzygy;
using Xunit;

namespace Anak.Tests.Data;

public class SyzygyTests
{
    [Fact]
    public void SyzygyTablebase_DefaultProperties_InitializedCorrectly()
    {
        var tb = new SyzygyTablebase();
        Assert.Equal(string.Empty, tb.SyzygyPath);
        Assert.Equal(1, tb.SyzygyProbeDepth);
        Assert.Equal(0UL, tb.TbHits);
    }

    [Fact]
    public void SyzygyTablebase_ConfigurableOptions_WorkCorrectly()
    {
        var tb = new SyzygyTablebase();
        tb.SyzygyPath = @"C:\nonexistent\syzygy";
        tb.SyzygyProbeDepth = 4;

        Assert.Equal(@"C:\nonexistent\syzygy", tb.SyzygyPath);
        Assert.Equal(4, tb.SyzygyProbeDepth);
    }

    [Fact]
    public void SyzygyWdl_FallbackWhenMissingFiles_ReturnsFalse()
    {
        var wdl = new SyzygyWdl();
        wdl.SetPath(@"C:\nonexistent\syzygy");

        // 3-piece endgame: KQ vs K
        var board = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        bool probed = wdl.TryProbe(in board, out int wdlScore);

        Assert.False(probed);
        Assert.Equal(0, wdlScore);
    }

    [Fact]
    public void SyzygyDtz_FallbackWhenMissingFiles_ReturnsFalse()
    {
        var wdl = new SyzygyWdl();
        var dtz = new SyzygyDtz();
        dtz.SetPath(@"C:\nonexistent\syzygy");

        var board = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        bool probed = dtz.TryProbe(in board, wdl, out Move bestMove, out int dtzScore);

        Assert.False(probed);
        Assert.Equal(Move.Null, bestMove);
        Assert.Equal(0, dtzScore);
    }

    [Fact]
    public void SyzygyTablebase_ResetHits_ClearsCounter()
    {
        var tb = new SyzygyTablebase();
        Assert.Equal(0UL, tb.TbHits);

        // Safe fallback probing
        var board = Fen.Parse(Fen.StartPosition);
        tb.TryProbeWdl(in board, out _);
        tb.TryProbeDtz(in board, out _, out _);

        tb.ResetHits();
        Assert.Equal(0UL, tb.TbHits);
    }

    [Fact]
    public void PolyGlotBook_NonExistentFile_ReturnsFalse()
    {
        var book = new PolyGlotBook();
        bool loaded = book.Load(@"C:\nonexistent\book.bin");
        Assert.False(loaded);
        Assert.False(book.IsLoaded);
        Assert.Equal(0, book.EntryCount);
    }

    [Fact]
    public void PolyGlotBook_OwnBookDisabled_DoesNotProbe()
    {
        var book = new PolyGlotBook
        {
            OwnBook = false
        };

        var board = Fen.Parse(Fen.StartPosition);
        bool moveFound = book.TryGetMove(in board, out Move move);

        Assert.False(moveFound);
        Assert.Equal(Move.Null, move);
    }
}
