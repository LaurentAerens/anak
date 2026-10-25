using Anak.Core;
using Anak.Data.PolyGlot;
using Anak.Data.Syzygy;
using Xunit;
using File = System.IO.File;

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
    public void SyzygyProber_FallbackWhenMissingFiles_ReturnsFalse()
    {
        using var prober = new SyzygyProber();
        prober.SetPath(@"C:\nonexistent\syzygy");

        // 3-piece endgame: KQ vs K
        var board = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        bool probed = prober.TryProbeWdl(in board, out int wdlScore);

        Assert.False(probed);
        Assert.Equal(0, wdlScore);
        Assert.Equal(0, prober.MaxPieces);
    }

    [Fact]
    public void SyzygyProber_RootProbeWhenMissingFiles_ReturnsFalse()
    {
        using var prober = new SyzygyProber();
        prober.SetPath(@"C:\nonexistent\syzygy");

        var board = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        bool probed = prober.TryProbeRoot(in board, out Move bestMove, out int dtzScore);

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

    [Fact]
    public void SyzygyTablebase_AutoDiscoversShippedTables_ProbesKQvK()
    {
        var tb = new SyzygyTablebase();
        var board = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        bool probed = tb.TryProbeWdl(in board, out int wdlScore);
        Assert.True(probed);
        Assert.Equal(2, wdlScore); // Win for White
        Assert.True(tb.TbHits > 0);

        bool dtzProbed = tb.TryProbeDtz(in board, out Move bestMove, out int dtzScore);
        Assert.True(dtzProbed);
        Assert.NotEqual(Move.Null, bestMove);
    }

    [Fact]
    public void SyzygyTablebase_AutoDiscoversShippedTables_ProbesKRvK()
    {
        var tb = new SyzygyTablebase();
        // 3-piece endgame: White King & Rook vs Black King
        var board = Fen.Parse("4k3/8/8/8/8/8/8/3RK3 w - - 0 1");
        bool probed = tb.TryProbeWdl(in board, out int wdlScore);

        Assert.True(probed);
        Assert.Equal(2, wdlScore); // Win for White

        bool dtzProbed = tb.TryProbeDtz(in board, out Move bestMove, out int dtzScore);
        Assert.True(dtzProbed);
        Assert.NotEqual(Move.Null, bestMove);
    }

    [Fact]
    public void SyzygyTablebase_AutoDiscoversShippedTables_ProbesFourPieceKBBvK()
    {
        var tb = new SyzygyTablebase();
        // 4-piece endgame: White King & Two Bishops vs Black King
        var board = Fen.Parse("4k3/8/8/8/8/8/8/2BBK3 w - - 0 1");
        bool probed = tb.TryProbeWdl(in board, out int wdlScore);

        Assert.True(probed);
        Assert.Equal(2, wdlScore); // Win for White

        bool dtzProbed = tb.TryProbeDtz(in board, out Move bestMove, out int dtzScore);
        Assert.True(dtzProbed);
        Assert.NotEqual(Move.Null, bestMove);
    }

    [Fact]
    public void SyzygyProber_MatchesPythonChessReference_WdlAndDtz()
    {
        string refPath = Path.Combine(AppContext.BaseDirectory, "Data", "syzygy_reference.txt");
        Assert.True(File.Exists(refPath), $"Missing reference data: {refPath}");

        using var prober = new SyzygyProber();
        prober.SetPath(SyzygyTablebase.ResolvePath(string.Empty));
        Assert.True(prober.MaxPieces >= 4);

        int checkedPositions = 0;
        var failures = new List<string>();

        foreach (string line in File.ReadLines(refPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] parts = line.Split('|', StringSplitOptions.TrimEntries);
            string fen = parts[0];
            int expectedWdl = int.Parse(parts[1]);
            int expectedDtz = int.Parse(parts[2]);

            var board = Fen.Parse(fen);

            if (!prober.TryProbeWdl(in board, out int wdl) || wdl != expectedWdl)
            {
                failures.Add($"WDL {fen}: expected {expectedWdl}, got {(prober.TryProbeWdl(in board, out int w2) ? w2.ToString() : "n/a")}");
            }

            if (!prober.TryProbeDtz(in board, out int dtz) || dtz != expectedDtz)
            {
                failures.Add($"DTZ {fen}: expected {expectedDtz}, got {(prober.TryProbeDtz(in board, out int d2) ? d2.ToString() : "n/a")}");
            }

            checkedPositions++;
        }

        Assert.True(checkedPositions > 1000, $"Only {checkedPositions} reference positions were checked");
        Assert.True(failures.Count == 0, $"{failures.Count} mismatches out of {checkedPositions}:\n" + string.Join('\n', failures.Take(15)));
    }

    [Fact]
    public void SyzygyProber_MaxPieces_IsDerivedFromFilesOnDisk()
    {
        string source = SyzygyTablebase.ResolvePath(string.Empty);
        Assert.False(string.IsNullOrEmpty(source));

        string tempDir = Path.Combine(Path.GetTempPath(), "anak_syzygy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Only 3-piece tables: probing capabilities must shrink accordingly.
            foreach (string file in Directory.EnumerateFiles(source, "K?vK.rtb?"))
            {
                File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)));
            }

            using var small = new SyzygyProber();
            small.SetPath(tempDir);
            Assert.Equal(3, small.MaxPieces);

            var threePiece = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
            Assert.True(small.TryProbeWdl(in threePiece, out int wdl));
            Assert.Equal(2, wdl);

            var fourPiece = Fen.Parse("4k3/8/8/8/8/8/8/2BBK3 w - - 0 1");
            Assert.False(small.TryProbeWdl(in fourPiece, out _));

            using var full = new SyzygyProber();
            full.SetPath(source);
            Assert.True(full.MaxPieces >= 4);
            Assert.True(full.TryProbeWdl(in fourPiece, out int fullWdl));
            Assert.Equal(2, fullWdl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SyzygyProber_MissingCaptureTable_FailsGracefully()
    {
        string source = SyzygyTablebase.ResolvePath(string.Empty);
        string tempDir = Path.Combine(Path.GetTempPath(), "anak_syzygy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // KQvKR needs KQvK after the rook is captured; with that table absent the probe must fail, not throw.
            File.Copy(Path.Combine(source, "KQvKR.rtbw"), Path.Combine(tempDir, "KQvKR.rtbw"));

            using var prober = new SyzygyProber();
            prober.SetPath(tempDir);

            var board = Fen.Parse("8/8/8/3k4/8/8/2r5/K6Q w - - 0 1");
            Assert.False(prober.TryProbeWdl(in board, out _));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/3QK3 w - - 0 1", 60)]      // KQvK
    [InlineData("4k3/8/8/8/8/8/8/3RK3 w - - 0 1", 80)]      // KRvK
    [InlineData("8/8/8/8/3k4/8/8/KBN5 w - - 0 1", 160)]     // KBNvK (longest elementary mate)
    [InlineData("8/8/8/4k3/8/4K3/4P3/8 b - - 0 1", 60)]     // KPvK, lost for the defender
    [InlineData("8/6k1/8/8/2P5/8/1K6/8 w - - 0 1", 80)]     // KPvK, pawn must promote and mate
    public void SyzygyProber_TablebasePlay_ConvertsWinsToCheckmate(string fen, int maxPlies)
    {
        using var prober = new SyzygyProber();
        prober.SetPath(SyzygyTablebase.ResolvePath(string.Empty));

        var board = Fen.Parse(fen);
        Assert.True(prober.TryProbeWdl(in board, out int startWdl));

        // Normalize so that the winner is whoever the table says it is.
        Color winner = startWdl > 0 ? board.SideToMove : board.SideToMove.Opponent();
        Assert.NotEqual(0, startWdl);

        for (int ply = 0; ply < maxPlies; ply++)
        {
            MoveList moves = default;
            board.GenerateLegalMoves(ref moves);
            if (moves.Count == 0)
            {
                Assert.True(board.IsInCheck(), "Game ended in stalemate");
                Assert.Equal(winner.Opponent(), board.SideToMove);
                return;
            }

            Assert.True(prober.TryProbeRoot(in board, out Move move, out _), $"Probe failed at ply {ply}: {Fen.ToFen(in board)}");
            board.MakeMove(move, out _);
        }

        Assert.Fail($"No checkmate within {maxPlies} plies");
    }

    [Fact]
    public void PolyGlotBook_ShippedBookBin_LoadsLargeMasterBook()
    {
        var book = new PolyGlotBook();
        string? path = PolyGlotBook.ResolveBookPath("book.bin");
        Assert.NotNull(path);

        bool loaded = book.Load(path);
        Assert.True(loaded);
        Assert.True(book.IsLoaded);
        Assert.True(book.EntryCount > 90000, $"Expected > 90000 entries, got {book.EntryCount}");

        var board = Fen.Parse(Fen.StartPosition);
        bool found = book.TryGetMove(in board, out Move move);
        Assert.True(found);
        Assert.NotEqual(Move.Null, move);
    }
}
